namespace DocIntel.Core.Models;

/// <summary>One line on an invoice. Every numeric value is nullable because extractors may not find it.</summary>
public sealed record LineItem
{
    public string Description { get; init; } = "";
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? Amount { get; init; }
}

/// <summary>
/// The structured result of reading an invoice. This is the contract shared by every extractor,
/// the validator, the review UI and the evaluation harness.
/// </summary>
public sealed record InvoiceData
{
    public string? InvoiceNumber { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? SupplierName { get; init; }
    public string? SupplierVatNumber { get; init; }
    public string? CustomerName { get; init; }

    /// <summary>ISO 4217 code, e.g. GBP.</summary>
    public string? Currency { get; init; }

    public decimal? Subtotal { get; init; }
    public decimal? TaxAmount { get; init; }
    public decimal? Total { get; init; }
    public IReadOnlyList<LineItem> LineItems { get; init; } = [];
}

/// <summary>Field names (camelCase, matching the JSON contract) and helpers to address fields generically.</summary>
public static class InvoiceFields
{
    public const string InvoiceNumber = "invoiceNumber";
    public const string InvoiceDate = "invoiceDate";
    public const string DueDate = "dueDate";
    public const string SupplierName = "supplierName";
    public const string SupplierVatNumber = "supplierVatNumber";
    public const string CustomerName = "customerName";
    public const string Currency = "currency";
    public const string Subtotal = "subtotal";
    public const string TaxAmount = "taxAmount";
    public const string Total = "total";
    public const string LineItems = "lineItems";

    /// <summary>Every field that is scored, in display order.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        InvoiceNumber, InvoiceDate, DueDate, SupplierName, SupplierVatNumber, CustomerName,
        Currency, Subtotal, TaxAmount, Total, LineItems,
    ];

    /// <summary>Fields an invoice cannot be posted without.</summary>
    public static readonly IReadOnlyList<string> Required = [InvoiceNumber, InvoiceDate, SupplierName, Total];

    public static bool IsRequired(string field) => Required.Contains(field);

    public static object? GetValue(InvoiceData data, string field) => field switch
    {
        InvoiceNumber => data.InvoiceNumber,
        InvoiceDate => data.InvoiceDate,
        DueDate => data.DueDate,
        SupplierName => data.SupplierName,
        SupplierVatNumber => data.SupplierVatNumber,
        CustomerName => data.CustomerName,
        Currency => data.Currency,
        Subtotal => data.Subtotal,
        TaxAmount => data.TaxAmount,
        Total => data.Total,
        LineItems => data.LineItems.Count == 0 ? null : data.LineItems,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown invoice field."),
    };
}
