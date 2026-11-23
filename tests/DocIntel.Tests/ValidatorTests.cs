using DocIntel.Core.Models;
using DocIntel.Core.Validation;

namespace DocIntel.Tests;

public class ValidatorTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly InvoiceValidator Validator = new(new FixedClock(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero)));

    internal static InvoiceData ValidInvoice() => new()
    {
        InvoiceNumber = "INV-1",
        InvoiceDate = new DateOnly(2026, 3, 1),
        DueDate = new DateOnly(2026, 3, 31),
        SupplierName = "Northwind Analytics Ltd",
        SupplierVatNumber = "GB123456789",
        CustomerName = "Bluebell Catering Ltd",
        Currency = "GBP",
        Subtotal = 100m,
        TaxAmount = 20m,
        Total = 120m,
        LineItems = [new LineItem { Description = "Consulting", Quantity = 2, UnitPrice = 50m, Amount = 100m }],
    };

    private static IEnumerable<string> Codes(InvoiceData d) => Validator.Validate(d).Select(i => i.Code);

    [Fact]
    public void Valid_invoice_has_no_issues() => Assert.Empty(Validator.Validate(ValidInvoice()));

    [Fact]
    public void Missing_required_fields_are_errors()
    {
        var issues = Validator.Validate(ValidInvoice() with { InvoiceNumber = null, Total = null });
        Assert.Equal(2, issues.Count(i => i is { Code: "MISSING_REQUIRED", Severity: IssueSeverity.Error }));
    }

    [Fact]
    public void Totals_that_do_not_add_up_are_flagged() =>
        Assert.Contains("TOTALS_MISMATCH", Codes(ValidInvoice() with { Total = 130m }));

    [Fact]
    public void Rounding_within_two_pence_is_tolerated() =>
        Assert.DoesNotContain("TOTALS_MISMATCH", Codes(ValidInvoice() with { Total = 120.01m }));

    [Fact]
    public void Line_items_must_sum_to_subtotal() =>
        Assert.Contains("LINES_SUBTOTAL_MISMATCH", Codes(ValidInvoice() with { Subtotal = 90m, TaxAmount = 30m }));

    [Fact]
    public void Line_arithmetic_is_a_warning()
    {
        var bad = ValidInvoice() with { LineItems = [new LineItem { Description = "x", Quantity = 3, UnitPrice = 50m, Amount = 100m }] };
        var issue = Assert.Single(Validator.Validate(bad), i => i.Code == "LINE_ARITHMETIC");
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Due_date_before_invoice_date_is_an_error() =>
        Assert.Contains("DUE_BEFORE_INVOICE", Codes(ValidInvoice() with { DueDate = new DateOnly(2026, 2, 1) }));

    [Fact]
    public void Future_invoice_date_is_a_warning() =>
        Assert.Contains("FUTURE_INVOICE_DATE", Codes(ValidInvoice() with { InvoiceDate = new DateOnly(2027, 1, 1), DueDate = null }));

    [Theory]
    [InlineData("GB12345678", true)]
    [InlineData("GB123456789", false)]
    [InlineData("GB123456789012", false)]
    [InlineData("GBGD123", false)]
    [InlineData("IE1234567W", false)] // non-UK numbers are not checked
    public void Uk_vat_number_format(string vat, bool flagged) =>
        Assert.Equal(flagged, Codes(ValidInvoice() with { SupplierVatNumber = vat }).Contains("VAT_NUMBER_FORMAT"));

    [Fact]
    public void Unusual_uk_tax_rate_is_a_warning() =>
        Assert.Contains("UNUSUAL_TAX_RATE", Codes(ValidInvoice() with { TaxAmount = 17.5m, Total = 117.5m }));
}
