using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DocIntel.Core.Models;

namespace DocIntel.Core.Extraction;

/// <summary>
/// Regex/heuristic extractor for plain-text invoices. It exists for two reasons:
/// it is the <b>baseline</b> the LLM extractor is measured against, and it lets the whole
/// pipeline run offline with no API key. It is deliberately label-driven, so it breaks on
/// prose-style or OCR-damaged documents - which is exactly what the eval shows.
/// </summary>
public sealed partial class RuleBasedExtractor : IInvoiceExtractor
{
    private const double Labelled = 0.9;
    private const double Heuristic = 0.6;

    private static readonly string[] DateFormats =
    [
        "dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd.MM.yyyy", "d MMMM yyyy", "dd MMMM yyyy",
        "d MMM yyyy", "dd MMM yyyy", "MMMM d, yyyy", "MMM d, yyyy",
    ];

    public string Name => "rule-based";

    public bool CanHandle(DocumentInput input) => input.IsText;

    public Task<ExtractionResult> ExtractAsync(DocumentInput input, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(input))
        {
            throw new UnsupportedDocumentException(
                $"The rule-based extractor only reads text documents; '{input.FileName}' is {input.NormalizedContentType}. Configure the Claude extractor for PDFs and images.");
        }

        var stopwatch = Stopwatch.StartNew();
        var result = Extract(input.ReadText());
        return Task.FromResult(result with { Duration = stopwatch.Elapsed });
    }

    internal ExtractionResult Extract(string text)
    {
        // Line-anchored patterns treat '\r' as content, so normalise Windows line endings first.
        text = text.ReplaceLineEndings("\n");
        var confidence = new Dictionary<string, double>();

        string? Capture(Regex regex, string field, double score = Labelled)
        {
            var match = regex.Match(text);
            if (!match.Success)
            {
                confidence[field] = 0;
                return null;
            }

            confidence[field] = score;
            return match.Groups[1].Value.Trim();
        }

        var invoiceNumber = Capture(InvoiceNumberRegex(), InvoiceFields.InvoiceNumber);
        var invoiceDate = ParseDate(Capture(InvoiceDateRegex(), InvoiceFields.InvoiceDate));
        var dueDate = ParseDate(Capture(DueDateRegex(), InvoiceFields.DueDate));
        if (invoiceDate is null) confidence[InvoiceFields.InvoiceDate] = 0;
        if (dueDate is null) confidence[InvoiceFields.DueDate] = 0;

        var vat = Capture(VatNumberRegex(), InvoiceFields.SupplierVatNumber);
        vat = vat is null ? null : WhitespaceRegex().Replace(vat, "").ToUpperInvariant();

        var supplier = Capture(SupplierRegex(), InvoiceFields.SupplierName) ?? FirstLineHeuristic(text, confidence);
        var customer = CaptureCustomer(text, confidence);
        var currency = DetectCurrency(text, confidence);

        var subtotal = ParseAmount(Capture(SubtotalRegex(), InvoiceFields.Subtotal));
        var tax = ParseAmount(Capture(TaxRegex(), InvoiceFields.TaxAmount));
        var total = ParseAmount(Capture(TotalRegex(), InvoiceFields.Total));

        var lines = ExtractLineItems(text);
        var linesSum = lines.Sum(l => l.Amount ?? 0);
        confidence[InvoiceFields.LineItems] = lines.Count == 0 ? 0 : subtotal == linesSum ? 0.85 : 0.5;

        var data = new InvoiceData
        {
            InvoiceNumber = invoiceNumber,
            InvoiceDate = invoiceDate,
            DueDate = dueDate,
            SupplierName = supplier,
            SupplierVatNumber = vat,
            CustomerName = customer,
            Currency = currency,
            Subtotal = subtotal,
            TaxAmount = tax,
            Total = total,
            LineItems = lines,
        };

        return new ExtractionResult { Data = data, Confidence = confidence, Extractor = Name };
    }

    private static string? FirstLineHeuristic(string text, Dictionary<string, double> confidence)
    {
        // Letterhead convention: the first line without digits that isn't the word "invoice" is usually the supplier.
        var candidate = text.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 2 && !l.Any(char.IsDigit) && !l.Contains("invoice", StringComparison.OrdinalIgnoreCase));
        confidence[InvoiceFields.SupplierName] = candidate is null ? 0 : Heuristic;
        return candidate;
    }

    private static string? CaptureCustomer(string text, Dictionary<string, double> confidence)
    {
        var match = CustomerRegex().Match(text);
        if (!match.Success)
        {
            confidence[InvoiceFields.CustomerName] = 0;
            return null;
        }

        var sameLine = match.Groups[1].Value.Trim();
        if (sameLine.Length > 0)
        {
            confidence[InvoiceFields.CustomerName] = Labelled;
            return sameLine;
        }

        // "Bill To:" on its own line - the name is on the next non-empty line.
        var rest = text[(match.Index + match.Length)..].Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        confidence[InvoiceFields.CustomerName] = rest is null ? 0 : 0.8;
        return rest;
    }

    private static string? DetectCurrency(string text, Dictionary<string, double> confidence)
    {
        var code = CurrencyCodeRegex().Match(text);
        string? currency = code.Success ? code.Value.ToUpperInvariant()
            : text.Contains('£') ? "GBP"
            : text.Contains('€') ? "EUR"
            : text.Contains('$') ? "USD"
            : null;
        confidence[InvoiceFields.Currency] = currency is null ? 0 : code.Success ? Labelled : 0.8;
        return currency;
    }

    private static List<LineItem> ExtractLineItems(string text)
    {
        var items = new List<LineItem>();
        foreach (var regex in new[] { SpacedLineItemRegex(), PipeLineItemRegex() })
        {
            foreach (Match m in regex.Matches(text))
            {
                items.Add(new LineItem
                {
                    Description = m.Groups["desc"].Value.Trim(),
                    Quantity = ParseAmount(m.Groups["qty"].Value),
                    UnitPrice = ParseAmount(m.Groups["unit"].Value),
                    Amount = ParseAmount(m.Groups["amt"].Value),
                });
            }

            if (items.Count > 0) break;
        }

        return items;
    }

    internal static DateOnly? ParseDate(string? value) =>
        value is not null && DateOnly.TryParseExact(value.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date)
            ? date
            : null;

    internal static decimal? ParseAmount(string? value) =>
        value is not null && decimal.TryParse(value.Replace(",", "", StringComparison.Ordinal).Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : null;

    private const string DatePattern = @"(\d{1,2}/\d{1,2}/\d{4}|\d{4}-\d{2}-\d{2}|\d{1,2}\.\d{1,2}\.\d{4}|\d{1,2}\s+[A-Za-z]{3,9}\s+\d{4}|[A-Za-z]{3,9}\s+\d{1,2},\s+\d{4})";
    private const string AmountPattern = @"(?:[£€$]|GBP|EUR|USD)?\s*(-?[\d,]+\.\d{2})";

    [GeneratedRegex(@"^\s*(?:invoice\s*(?:no\.?|number|#)|inv\.?\s*(?:no\.?|#)|invoice\s+ref(?:erence)?)\s*[:#]?\s*([A-Z0-9][A-Z0-9\-/]*\d[A-Z0-9\-/]*)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex InvoiceNumberRegex();

    [GeneratedRegex(@"^\s*(?:invoice\s+date|date\s+of\s+issue|issue\s+date|date)\s*[:\-]?\s*" + DatePattern, RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex InvoiceDateRegex();

    [GeneratedRegex(@"^\s*(?:due\s+date|payment\s+due|due\s+by|due)\s*[:\-]?\s*" + DatePattern, RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DueDateRegex();

    [GeneratedRegex(@"\b(GB\s?\d{3}\s?\d{4}\s?\d{2})\b", RegexOptions.IgnoreCase)]
    private static partial Regex VatNumberRegex();

    [GeneratedRegex(@"^\s*(?:from|supplier|seller|vendor)\s*:\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SupplierRegex();

    [GeneratedRegex(@"^\s*(?:bill(?:ed)?\s+to|customer|client|invoice\s+to)\s*:[ \t]*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex CustomerRegex();

    [GeneratedRegex(@"\b(GBP|EUR|USD)\b")]
    private static partial Regex CurrencyCodeRegex();

    [GeneratedRegex(@"^\s*(?:sub-?\s?total|net\s+amount|net\s+total|net)\b[^\n\d]*?" + AmountPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SubtotalRegex();

    [GeneratedRegex(@"^\s*(?:vat|tax|sales\s+tax)\b[^\n]*?" + AmountPattern + @"\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TaxRegex();

    [GeneratedRegex(@"^\s*(?:total\s+due|total|amount\s+due|balance\s+due|grand\s+total|amount\s+payable)\b[^\n\d]*?" + AmountPattern, RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex TotalRegex();

    [GeneratedRegex(@"^\s*(?<desc>[A-Za-z][^\n]*?)\s{2,}(?<qty>\d+(?:\.\d+)?)\s{2,}(?:[£€$]\s?)?(?<unit>[\d,]+\.\d{2})\s{2,}(?:[£€$]\s?)?(?<amt>[\d,]+\.\d{2})[ \t]*$", RegexOptions.Multiline)]
    private static partial Regex SpacedLineItemRegex();

    [GeneratedRegex(@"^\s*\|\s*(?<desc>[A-Za-z][^|\n]*?)\s*\|\s*(?<qty>\d+(?:\.\d+)?)\s*\|\s*(?:[A-Z]{3}\s)?(?<unit>[\d,]+\.\d{2})\s*\|\s*(?:[A-Z]{3}\s)?(?<amt>[\d,]+\.\d{2})\s*\|", RegexOptions.Multiline)]
    private static partial Regex PipeLineItemRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
