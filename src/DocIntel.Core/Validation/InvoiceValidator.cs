using System.Text.RegularExpressions;
using DocIntel.Core.Models;

namespace DocIntel.Core.Validation;

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <param name="Code">Stable machine-readable code, e.g. TOTALS_MISMATCH.</param>
/// <param name="Fields">Fields the issue casts doubt on; their confidence is capped.</param>
public sealed record ValidationIssue(string Code, IssueSeverity Severity, string Message, IReadOnlyList<string> Fields);

/// <summary>
/// Deterministic business rules applied after extraction. The model can be confidently wrong;
/// arithmetic and format checks catch a large share of those cases for free.
/// </summary>
public sealed partial class InvoiceValidator(TimeProvider clock)
{
    private const decimal Tolerance = 0.02m;
    private static readonly decimal[] UkVatRates = [0m, 0.05m, 0.20m];

    public InvoiceValidator() : this(TimeProvider.System)
    {
    }

    public IReadOnlyList<ValidationIssue> Validate(InvoiceData d)
    {
        var issues = new List<ValidationIssue>();

        foreach (var field in InvoiceFields.Required)
        {
            if (InvoiceFields.GetValue(d, field) is null)
            {
                issues.Add(new("MISSING_REQUIRED", IssueSeverity.Error, $"Required field '{field}' was not found.", [field]));
            }
        }

        if (d is { Subtotal: { } sub, TaxAmount: { } tax, Total: { } total } && Math.Abs(sub + tax - total) > Tolerance)
        {
            issues.Add(new("TOTALS_MISMATCH", IssueSeverity.Error,
                $"Subtotal {sub:0.00} + tax {tax:0.00} = {sub + tax:0.00}, but total is {total:0.00}.",
                [InvoiceFields.Subtotal, InvoiceFields.TaxAmount, InvoiceFields.Total]));
        }

        if (d.LineItems.Count > 0 && d.Subtotal is { } subtotal && d.LineItems.All(l => l.Amount is not null))
        {
            var sum = d.LineItems.Sum(l => l.Amount!.Value);
            if (Math.Abs(sum - subtotal) > Tolerance)
            {
                issues.Add(new("LINES_SUBTOTAL_MISMATCH", IssueSeverity.Error,
                    $"Line items sum to {sum:0.00}, but subtotal is {subtotal:0.00}.",
                    [InvoiceFields.LineItems, InvoiceFields.Subtotal]));
            }
        }

        foreach (var line in d.LineItems)
        {
            if (line is { Quantity: { } q, UnitPrice: { } p, Amount: { } a } && Math.Abs(decimal.Round(q * p, 2) - a) > Tolerance)
            {
                issues.Add(new("LINE_ARITHMETIC", IssueSeverity.Warning,
                    $"Line '{line.Description}': {q} x {p:0.00} != {a:0.00}.", [InvoiceFields.LineItems]));
            }
        }

        if (d is { InvoiceDate: { } issued, DueDate: { } due } && due < issued)
        {
            issues.Add(new("DUE_BEFORE_INVOICE", IssueSeverity.Error,
                $"Due date {due:yyyy-MM-dd} is before invoice date {issued:yyyy-MM-dd}.", [InvoiceFields.DueDate, InvoiceFields.InvoiceDate]));
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (d.InvoiceDate is { } invoiceDate && invoiceDate > today.AddDays(1))
        {
            issues.Add(new("FUTURE_INVOICE_DATE", IssueSeverity.Warning,
                $"Invoice date {invoiceDate:yyyy-MM-dd} is in the future.", [InvoiceFields.InvoiceDate]));
        }

        if (d.SupplierVatNumber is { } vat && vat.StartsWith("GB", StringComparison.OrdinalIgnoreCase) && !UkVatRegex().IsMatch(vat))
        {
            issues.Add(new("VAT_NUMBER_FORMAT", IssueSeverity.Error,
                $"'{vat}' is not a valid UK VAT number format.", [InvoiceFields.SupplierVatNumber]));
        }

        if (d is { Currency: "GBP", Subtotal: > 0m, TaxAmount: { } t } && !UkVatRates.Any(r => Math.Abs(t / d.Subtotal.Value - r) < 0.005m))
        {
            issues.Add(new("UNUSUAL_TAX_RATE", IssueSeverity.Warning,
                $"Tax is {t / d.Subtotal.Value:P1} of subtotal; UK VAT rates are 0%, 5% or 20%.", [InvoiceFields.TaxAmount]));
        }

        if (d.Currency is { } currency && !CurrencyRegex().IsMatch(currency))
        {
            issues.Add(new("CURRENCY_FORMAT", IssueSeverity.Warning,
                $"'{currency}' is not an ISO 4217 code.", [InvoiceFields.Currency]));
        }

        if (d.Total is < 0m)
        {
            issues.Add(new("NEGATIVE_TOTAL", IssueSeverity.Warning, "Total is negative - is this a credit note?", [InvoiceFields.Total]));
        }

        return issues;
    }

    [GeneratedRegex(@"^GB(\d{9}|\d{12}|GD\d{3}|HA\d{3})$", RegexOptions.IgnoreCase)]
    private static partial Regex UkVatRegex();

    [GeneratedRegex("^[A-Z]{3}$")]
    private static partial Regex CurrencyRegex();
}
