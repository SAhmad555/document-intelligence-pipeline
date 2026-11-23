using System.Text.Json;
using DocIntel.Core.Extraction;
using DocIntel.Core.Models;
using DocIntel.Core.Synthetic;

namespace DocIntel.Tests;

public class ExtractionTests
{
    [Fact]
    public void Rule_based_extracts_a_classic_layout_exactly()
    {
        var invoice = new InvoiceGenerator(seed: 7).Generate(1).Single();
        Assert.Equal("classic", invoice.Template);

        var result = new RuleBasedExtractor().Extract(invoice.Text);

        Assert.Equal(invoice.Truth.InvoiceNumber, result.Data.InvoiceNumber);
        Assert.Equal(invoice.Truth.InvoiceDate, result.Data.InvoiceDate);
        Assert.Equal(invoice.Truth.Total, result.Data.Total);
        Assert.Equal(invoice.Truth.LineItems.Count, result.Data.LineItems.Count);
    }

    [Fact]
    public void Rule_based_handles_windows_line_endings()
    {
        var invoice = new InvoiceGenerator(seed: 7).Generate(1).Single();
        var result = new RuleBasedExtractor().Extract(invoice.Text.ReplaceLineEndings("\r\n"));
        Assert.Equal(invoice.Truth.LineItems.Count, result.Data.LineItems.Count);
    }

    [Fact]
    public async Task Rule_based_rejects_pdfs_with_a_clear_message()
    {
        var pdf = new DocumentInput("scan.pdf", "application/pdf", [0x25, 0x50, 0x44, 0x46]);
        var ex = await Assert.ThrowsAsync<UnsupportedDocumentException>(() => new RuleBasedExtractor().ExtractAsync(pdf));
        Assert.Contains("Claude", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generator_is_deterministic_for_a_seed()
    {
        var a = new InvoiceGenerator(123).Generate(10).Select(i => i.Text).ToList();
        var b = new InvoiceGenerator(123).Generate(10).Select(i => i.Text).ToList();
        Assert.Equal(a, b);
    }

    [Fact]
    public void Generator_ground_truth_is_internally_consistent()
    {
        foreach (var inv in new InvoiceGenerator(1).Generate(200).Where(i => !i.HasArithmeticError))
        {
            Assert.Equal(inv.Truth.Subtotal, inv.Truth.LineItems.Sum(l => l.Amount));
            Assert.Equal(inv.Truth.Total, inv.Truth.Subtotal + inv.Truth.TaxAmount);
        }
    }

    [Fact]
    public void Schema_requires_every_field_and_forbids_extras()
    {
        var schema = ExtractionContract.BuildSchema();
        Assert.False(schema["additionalProperties"].GetBoolean());

        var required = schema["required"].EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.All(InvoiceFields.All, f => Assert.Contains(f, required));
        Assert.Contains("confidence", required);
    }

    [Fact]
    public void Parses_model_reply_into_domain_types()
    {
        const string json = """
            {
              "invoiceNumber": " INV-9 ", "invoiceDate": "2026-04-03", "dueDate": null,
              "supplierName": "Kestrel Logistics Ltd", "supplierVatNumber": "gb 123 4567 89", "customerName": null,
              "currency": "gbp", "subtotal": 100, "taxAmount": 20, "total": 120.004,
              "lineItems": [{ "description": "Pallets", "quantity": 4, "unitPrice": 25, "amount": 100 }],
              "confidence": { "invoiceNumber": 0.99, "invoiceDate": 1.4, "dueDate": 1, "supplierName": 0.9,
                "supplierVatNumber": 0.8, "customerName": 1, "currency": 0.95, "subtotal": 0.9, "taxAmount": 0.9,
                "total": 0.97, "lineItems": 0.85 }
            }
            """;

        var (data, confidence) = ExtractionContract.Parse(json);

        Assert.Equal("INV-9", data.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 4, 3), data.InvoiceDate);
        Assert.Equal("GB123456789", data.SupplierVatNumber);
        Assert.Equal("GBP", data.Currency);
        Assert.Equal(120.00m, data.Total);
        Assert.Single(data.LineItems);
        Assert.Equal(1.0, confidence[InvoiceFields.InvoiceDate]); // clamped
    }

    [Fact]
    public void Non_iso_date_from_model_gets_zero_confidence()
    {
        var reply = JsonSerializer.Serialize(new
        {
            invoiceNumber = "1", invoiceDate = "03/04/2026", dueDate = (string?)null, supplierName = "S", supplierVatNumber = (string?)null,
            customerName = (string?)null, currency = "GBP", subtotal = 1, taxAmount = 0, total = 1, lineItems = Array.Empty<object>(),
            confidence = InvoiceFields.All.ToDictionary(f => f, _ => 0.9),
        });

        var (data, confidence) = ExtractionContract.Parse(reply);
        Assert.Null(data.InvoiceDate);
        Assert.Equal(0, confidence[InvoiceFields.InvoiceDate]);
    }

    [Fact]
    public void Invalid_model_json_raises_extraction_failed() =>
        Assert.Throws<ExtractionFailedException>(() => ExtractionContract.Parse("not json"));
}
