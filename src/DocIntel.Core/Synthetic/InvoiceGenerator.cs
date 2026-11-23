using System.Globalization;
using System.Text;
using DocIntel.Core.Models;

namespace DocIntel.Core.Synthetic;

public sealed record SyntheticInvoice(string Id, string Template, string Text, InvoiceData Truth, bool HasArithmeticError);

/// <summary>
/// Generates a labelled invoice dataset with known ground truth. All companies and numbers are fictional,
/// so the dataset is safe to publish. Four layouts of increasing difficulty:
/// <list type="bullet">
/// <item><c>classic</c> - letterhead, labelled fields, aligned table.</item>
/// <item><c>modern</c> - key/value header, markdown-style table, currency codes, ISO dates.</item>
/// <item><c>email</c> - invoice details written as prose in an email body.</item>
/// <item><c>ocr-noisy</c> - classic layout with OCR damage to labels and spacing.</item>
/// </list>
/// A small share carry a printed arithmetic error, which the validator must route to review.
/// </summary>
public sealed class InvoiceGenerator(int seed = 42)
{
    public static readonly IReadOnlyList<string> Templates = ["classic", "modern", "email", "ocr-noisy"];

    private static readonly string[] NamePrefixes = ["Northwind", "Bluebell", "Harbour", "Kestrel", "Oakridge", "Silverline", "Pennine", "Larkspur", "Granite", "Meridian", "Thistle", "Copperfield"];
    private static readonly string[] NameSuffixes = ["Analytics", "Facilities", "Logistics", "Print Works", "Consulting", "Catering", "Cleaning Services", "IT Solutions", "Recruitment", "Engineering"];
    private static readonly string[] LegalForms = ["Ltd", "Limited", "LLP", "Ltd"];
    private static readonly string[] Services =
    [
        "Consultancy day rate", "Software licence (annual)", "On-site support hours", "Office cleaning - monthly",
        "Pallet storage (per week)", "Courier delivery", "Printed brochures (A4, 500)", "Catering - staff lunch",
        "Network switch installation", "Payroll processing fee", "Temporary staff hours", "Equipment hire",
    ];

    private static readonly CultureInfo En = CultureInfo.InvariantCulture;
    private readonly Random _rng = new(seed);

    public IEnumerable<SyntheticInvoice> Generate(int count)
    {
        for (var i = 1; i <= count; i++)
        {
            yield return Next(i);
        }
    }

    private SyntheticInvoice Next(int n)
    {
        var template = Templates[(n - 1) % Templates.Count];
        var currency = Pick(("GBP", 6), ("EUR", 2), ("USD", 2));
        var taxRate = currency == "GBP" ? Pick((0.20m, 8), (0.05m, 1), (0m, 1)) : Pick((0.20m, 1), (0m, 1));

        var lines = Enumerable.Range(0, _rng.Next(1, 6)).Select(_ =>
        {
            var qty = _rng.Next(10) == 0 ? 7.5m : _rng.Next(1, 21);
            var unit = decimal.Round((decimal)(_rng.NextDouble() * 900 + 15), 2);
            return new LineItem { Description = Services[_rng.Next(Services.Length)], Quantity = qty, UnitPrice = unit, Amount = decimal.Round(qty * unit, 2) };
        }).ToList();

        var subtotal = lines.Sum(l => l.Amount!.Value);
        var tax = decimal.Round(subtotal * taxRate, 2, MidpointRounding.AwayFromZero);
        var total = subtotal + tax;

        var hasError = _rng.Next(100) < 7;
        if (hasError)
        {
            total += Pick((10m, 1), (-1m, 1), (100m, 1)); // a typo on the printed document
        }

        var invoiceDate = new DateOnly(2026, 1, 5).AddDays(_rng.Next(0, 290));
        DateOnly? dueDate = _rng.Next(100) < 15 ? null : invoiceDate.AddDays(Pick((14, 1), (30, 3), (45, 1)));
        string? vat = currency switch
        {
            "GBP" when _rng.Next(100) >= 20 => "GB" + Digits(9),
            "EUR" when _rng.Next(2) == 0 => "IE" + Digits(7) + "W",
            _ => null,
        };

        var truth = new InvoiceData
        {
            InvoiceNumber = Pick(($"INV-2026-{n:0000}", 2), ($"A-{10000 + n * 7}", 1), ($"{7000 + n}/B", 1), ($"SI{n:000000}", 1)),
            InvoiceDate = invoiceDate,
            DueDate = dueDate,
            SupplierName = CompanyName(),
            SupplierVatNumber = vat,
            CustomerName = _rng.Next(100) < 5 ? null : CompanyName(),
            Currency = currency,
            Subtotal = subtotal,
            TaxAmount = tax,
            Total = total,
            LineItems = lines,
        };

        var text = template switch
        {
            "classic" => Classic(truth, taxRate),
            "modern" => Modern(truth, taxRate),
            "email" => Email(truth),
            _ => OcrNoise(Classic(truth, taxRate)),
        };

        return new SyntheticInvoice($"inv-{n:0000}", template, text, truth, hasError);
    }

    private static string Classic(InvoiceData d, decimal rate)
    {
        var s = Symbol(d.Currency!);
        var sb = new StringBuilder();
        sb.AppendLine(d.SupplierName);
        sb.AppendLine("Unit 4, Riverside Business Park, Leeds LS1 4AB");
        if (d.SupplierVatNumber is not null) sb.AppendLine($"VAT Reg No: {SpacedVat(d.SupplierVatNumber)}");
        sb.AppendLine();
        sb.AppendLine("INVOICE");
        sb.AppendLine();
        sb.AppendLine($"Invoice No: {d.InvoiceNumber}");
        sb.AppendLine($"Invoice Date: {d.InvoiceDate!.Value.ToString("dd/MM/yyyy", En)}");
        if (d.DueDate is { } due) sb.AppendLine($"Due Date: {due.ToString("dd/MM/yyyy", En)}");
        sb.AppendLine();
        if (d.CustomerName is not null)
        {
            sb.AppendLine("Bill To:");
            sb.AppendLine(d.CustomerName);
            sb.AppendLine("1 King Street, Manchester M2 6AZ");
            sb.AppendLine();
        }

        sb.AppendLine($"{"Description",-34}  {"Qty",6}  {"Unit Price",12}  {"Amount",12}");
        sb.AppendLine(new string('-', 72));
        foreach (var l in d.LineItems)
        {
            sb.AppendLine($"{l.Description,-34}  {Num(l.Quantity!.Value),6}  {s + Money(l.UnitPrice!.Value),12}  {s + Money(l.Amount!.Value),12}");
        }

        sb.AppendLine(new string('-', 72));
        sb.AppendLine($"Subtotal: {s}{Money(d.Subtotal!.Value)}");
        sb.AppendLine($"VAT ({rate * 100:0.##}%): {s}{Money(d.TaxAmount!.Value)}");
        sb.AppendLine($"Total Due: {s}{Money(d.Total!.Value)}");
        sb.AppendLine();
        sb.AppendLine("Payment by BACS within terms. Thank you for your business.");
        return sb.ToString();
    }

    private static string Modern(InvoiceData d, decimal rate)
    {
        var c = d.Currency!;
        var sb = new StringBuilder();
        sb.AppendLine("# Tax Invoice");
        sb.AppendLine();
        sb.AppendLine($"Inv # {d.InvoiceNumber}");
        sb.AppendLine($"Issue date: {d.InvoiceDate!.Value.ToString("yyyy-MM-dd", En)}");
        if (d.DueDate is { } due) sb.AppendLine($"Payment due: {due.ToString("yyyy-MM-dd", En)}");
        sb.AppendLine($"Supplier: {d.SupplierName}");
        if (d.SupplierVatNumber is not null) sb.AppendLine($"Tax ID: {d.SupplierVatNumber}");
        if (d.CustomerName is not null) sb.AppendLine($"Customer: {d.CustomerName}");
        sb.AppendLine();
        sb.AppendLine("| Item | Qty | Unit | Line total |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var l in d.LineItems)
        {
            sb.AppendLine($"| {l.Description} | {Num(l.Quantity!.Value)} | {c} {Money(l.UnitPrice!.Value)} | {c} {Money(l.Amount!.Value)} |");
        }

        sb.AppendLine();
        sb.AppendLine($"Net total: {c} {Money(d.Subtotal!.Value)}");
        sb.AppendLine($"Tax @ {rate * 100:0.##}%: {c} {Money(d.TaxAmount!.Value)}");
        sb.AppendLine($"Amount due: {c} {Money(d.Total!.Value)}");
        return sb.ToString();
    }

    private static string Email(InvoiceData d)
    {
        var s = Symbol(d.Currency!);
        var sb = new StringBuilder();
        sb.AppendLine($"Subject: Our invoice {d.InvoiceNumber}");
        sb.AppendLine();
        sb.AppendLine(d.CustomerName is null ? "Hello," : $"Hi {d.CustomerName} accounts team,");
        sb.AppendLine();
        sb.Append(CultureInfo.InvariantCulture, $"Please find below our invoice ref {d.InvoiceNumber}, raised on {d.InvoiceDate!.Value.ToString("d MMMM yyyy", En)}");
        sb.AppendLine(d.DueDate is { } due ? $", and payable by {due.ToString("d MMMM yyyy", En)}." : ".");
        sb.AppendLine();
        sb.AppendLine("This covers:");
        foreach (var l in d.LineItems)
        {
            sb.AppendLine($"  - {Num(l.Quantity!.Value)} x {l.Description} at {s}{Money(l.UnitPrice!.Value)} each = {s}{Money(l.Amount!.Value)}");
        }

        sb.AppendLine();
        sb.AppendLine($"That comes to {s}{Money(d.Subtotal!.Value)} before tax, plus {s}{Money(d.TaxAmount!.Value)} VAT, so {s}{Money(d.Total!.Value)} in total.");
        sb.AppendLine();
        sb.AppendLine("Kind regards,");
        sb.AppendLine("Jo Bennett, Credit Control");
        sb.AppendLine(d.SupplierName);
        if (d.SupplierVatNumber is not null) sb.AppendLine($"VAT registration {d.SupplierVatNumber}");
        return sb.ToString();
    }

    private string OcrNoise(string text)
    {
        // Damage labels the way OCR does: l/I and 0/O confusion, broken words, ragged spacing. Values stay intact.
        (string From, string To)[] swaps =
        [
            ("Invoice No:", "lnvoice N0:"), ("Invoice Date:", "Invo ice Date :"), ("Due Date:", "Due Dale:"),
            ("Subtotal:", "Sub tota1:"), ("Total Due:", "T0tal Due:"), ("Bill To:", "BilI To:"), ("VAT (", "VAT  ("),
        ];

        foreach (var (from, to) in swaps)
        {
            if (_rng.Next(100) < 60) text = text.Replace(from, to, StringComparison.Ordinal);
        }

        return text.Replace("  ", _rng.Next(2) == 0 ? "   " : "  ", StringComparison.Ordinal);
    }

    private string CompanyName() =>
        $"{NamePrefixes[_rng.Next(NamePrefixes.Length)]} {NameSuffixes[_rng.Next(NameSuffixes.Length)]} {LegalForms[_rng.Next(LegalForms.Length)]}";

    private string Digits(int count) => string.Concat(Enumerable.Range(0, count).Select(_ => _rng.Next(10)));

    private T Pick<T>(params (T Value, int Weight)[] options)
    {
        var roll = _rng.Next(options.Sum(o => o.Weight));
        foreach (var (value, weight) in options)
        {
            if ((roll -= weight) < 0) return value;
        }

        return options[^1].Value;
    }

    private static string SpacedVat(string vat) =>
        vat.StartsWith("GB", StringComparison.Ordinal) && vat.Length == 11 ? $"GB {vat[2..5]} {vat[5..9]} {vat[9..]}" : vat;

    private static string Symbol(string currency) => currency switch { "GBP" => "£", "EUR" => "€", _ => "$" };

    private static string Money(decimal value) => value.ToString("#,##0.00", En);

    private static string Num(decimal value) => value.ToString("0.##", En);
}
