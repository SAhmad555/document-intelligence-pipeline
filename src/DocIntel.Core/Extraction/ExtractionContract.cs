using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DocIntel.Core.Models;

namespace DocIntel.Core.Extraction;

/// <summary>
/// The prompt and JSON schema sent to the model, plus parsing of its reply. Kept separate from the
/// HTTP call so it can be unit tested and versioned (the version is stored with every result).
/// </summary>
public static class ExtractionContract
{
    public const string PromptVersion = "invoice-v1";

    public const string SystemPrompt =
        """
        You extract structured data from supplier invoices for an accounts-payable system.
        The output is posted to a ledger, so a wrong value is far more costly than a missing one.

        Rules:
        - Copy values exactly as printed on the document. Do not correct arithmetic, even if totals do not add up - a downstream validator checks that.
        - If a field is not present, return null. Never guess or derive a value that is not printed (e.g. do not compute a due date from payment terms).
        - Dates: return ISO format YYYY-MM-DD. Interpret numeric dates as day/month/year unless the document clearly uses another order.
        - Amounts: plain numbers without currency symbols or thousands separators. "total" is the gross amount payable including tax.
        - currency: ISO 4217 code (GBP, EUR, USD...) inferred from symbols or codes on the document.
        - supplierName is the party issuing the invoice; customerName is the party being billed.
        - supplierVatNumber: the supplier's VAT/tax registration number with spaces removed, or null.
        - lineItems: one entry per billed line, in document order. Use null for any number not printed on that line.
        - confidence: for every field give a number from 0 to 1 for how sure you are the value is exactly right.
          Use 1.0 only for values printed clearly with an unambiguous label. Use <= 0.5 when the text is damaged,
          ambiguous, or you had to choose between candidates. A null field that is genuinely absent gets 1.0.
        """;

    public const string UserInstruction = "Extract the invoice fields from this document.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>JSON schema for structured outputs. Every property is required; optional values are nullable types.</summary>
    public static Dictionary<string, JsonElement> BuildSchema()
    {
        static JsonObject Nullable(string type) => new() { ["type"] = new JsonArray(type, "null") };

        var confidence = new JsonObject();
        foreach (var field in InvoiceFields.All)
        {
            confidence[field] = new JsonObject { ["type"] = "number" };
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                [InvoiceFields.InvoiceNumber] = Nullable("string"),
                [InvoiceFields.InvoiceDate] = Nullable("string"),
                [InvoiceFields.DueDate] = Nullable("string"),
                [InvoiceFields.SupplierName] = Nullable("string"),
                [InvoiceFields.SupplierVatNumber] = Nullable("string"),
                [InvoiceFields.CustomerName] = Nullable("string"),
                [InvoiceFields.Currency] = Nullable("string"),
                [InvoiceFields.Subtotal] = Nullable("number"),
                [InvoiceFields.TaxAmount] = Nullable("number"),
                [InvoiceFields.Total] = Nullable("number"),
                [InvoiceFields.LineItems] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["description"] = new JsonObject { ["type"] = "string" },
                            ["quantity"] = Nullable("number"),
                            ["unitPrice"] = Nullable("number"),
                            ["amount"] = Nullable("number"),
                        },
                        ["required"] = new JsonArray("description", "quantity", "unitPrice", "amount"),
                    },
                },
                ["confidence"] = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = confidence,
                    ["required"] = new JsonArray(InvoiceFields.All.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
                },
            },
            ["required"] = new JsonArray(InvoiceFields.All.Append("confidence").Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
        };

        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schema.ToJsonString())!;
    }

    /// <summary>Turns the model's JSON reply into domain objects. Throws <see cref="ExtractionFailedException"/> on unusable output.</summary>
    public static (InvoiceData Data, Dictionary<string, double> Confidence) Parse(string json)
    {
        ModelReply reply;
        try
        {
            reply = JsonSerializer.Deserialize<ModelReply>(json, JsonOptions)
                ?? throw new ExtractionFailedException("Model returned an empty JSON document.");
        }
        catch (JsonException ex)
        {
            throw new ExtractionFailedException("Model output was not valid JSON for the invoice schema.", ex);
        }

        var data = new InvoiceData
        {
            InvoiceNumber = Clean(reply.InvoiceNumber),
            InvoiceDate = ParseIsoDate(reply.InvoiceDate),
            DueDate = ParseIsoDate(reply.DueDate),
            SupplierName = Clean(reply.SupplierName),
            SupplierVatNumber = Clean(reply.SupplierVatNumber)?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant(),
            CustomerName = Clean(reply.CustomerName),
            Currency = Clean(reply.Currency)?.ToUpperInvariant(),
            Subtotal = Round(reply.Subtotal),
            TaxAmount = Round(reply.TaxAmount),
            Total = Round(reply.Total),
            LineItems = (reply.LineItems ?? []).Select(l => new LineItem
            {
                Description = l.Description?.Trim() ?? "",
                Quantity = l.Quantity,
                UnitPrice = Round(l.UnitPrice),
                Amount = Round(l.Amount),
            }).ToList(),
        };

        var confidence = InvoiceFields.All.ToDictionary(
            f => f,
            f => Math.Clamp(reply.Confidence?.GetValueOrDefault(f) ?? 0d, 0d, 1d));

        // A date the model returned in a non-ISO shape is unusable - don't trust it.
        if (reply.InvoiceDate is not null && data.InvoiceDate is null) confidence[InvoiceFields.InvoiceDate] = 0;
        if (reply.DueDate is not null && data.DueDate is null) confidence[InvoiceFields.DueDate] = 0;

        return (data, confidence);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static decimal? Round(decimal? value) => value is null ? null : decimal.Round(value.Value, 2, MidpointRounding.AwayFromZero);

    private static DateOnly? ParseIsoDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private sealed record ModelReply
    {
        public string? InvoiceNumber { get; init; }
        public string? InvoiceDate { get; init; }
        public string? DueDate { get; init; }
        public string? SupplierName { get; init; }
        public string? SupplierVatNumber { get; init; }
        public string? CustomerName { get; init; }
        public string? Currency { get; init; }
        public decimal? Subtotal { get; init; }
        public decimal? TaxAmount { get; init; }
        public decimal? Total { get; init; }
        public List<ModelLine>? LineItems { get; init; }
        public Dictionary<string, double>? Confidence { get; init; }
    }

    private sealed record ModelLine
    {
        public string? Description { get; init; }
        public decimal? Quantity { get; init; }
        public decimal? UnitPrice { get; init; }
        public decimal? Amount { get; init; }
    }
}
