using System.Text.RegularExpressions;
using DocIntel.Core.Models;

namespace DocIntel.Core.Evaluation;

/// <summary>How a predicted field compares with ground truth.</summary>
public enum FieldOutcome
{
    /// <summary>Truth present, prediction matches.</summary>
    Correct,

    /// <summary>Truth present, prediction present but different. Counts as both a false positive and a false negative.</summary>
    Wrong,

    /// <summary>Truth present, nothing predicted.</summary>
    Missed,

    /// <summary>Truth absent, something predicted (hallucination).</summary>
    Spurious,

    /// <summary>Truth absent, nothing predicted.</summary>
    CorrectlyEmpty,
}

/// <summary>Field-aware equality: forgiving on formatting, strict on meaning.</summary>
public static partial class FieldComparer
{
    public static FieldOutcome Compare(string field, InvoiceData truth, InvoiceData predicted)
    {
        var t = InvoiceFields.GetValue(truth, field);
        var p = InvoiceFields.GetValue(predicted, field);

        return (t, p) switch
        {
            (null, null) => FieldOutcome.CorrectlyEmpty,
            (null, _) => FieldOutcome.Spurious,
            (_, null) => FieldOutcome.Missed,
            _ => AreEqual(field, t, p) ? FieldOutcome.Correct : FieldOutcome.Wrong,
        };
    }

    private static bool AreEqual(string field, object truth, object predicted) => (truth, predicted) switch
    {
        (decimal a, decimal b) => decimal.Round(a, 2) == decimal.Round(b, 2),
        (DateOnly a, DateOnly b) => a == b,
        (IReadOnlyList<LineItem> a, IReadOnlyList<LineItem> b) => LinesEqual(a, b),
        (string a, string b) => NormalizeText(field, a) == NormalizeText(field, b),
        _ => Equals(truth, predicted),
    };

    /// <summary>Line items match when the same amounts are present (order-insensitive) - descriptions vary too much to compare literally.</summary>
    private static bool LinesEqual(IReadOnlyList<LineItem> a, IReadOnlyList<LineItem> b) =>
        a.Count == b.Count &&
        a.Select(l => l.Amount ?? -1).Order().SequenceEqual(b.Select(l => l.Amount ?? -1).Order());

    internal static string NormalizeText(string field, string value)
    {
        var v = WhitespaceRegex().Replace(value.Trim(), " ").ToUpperInvariant();
        if (field is InvoiceFields.SupplierVatNumber or InvoiceFields.InvoiceNumber)
        {
            v = v.Replace(" ", "", StringComparison.Ordinal);
        }

        if (field is InvoiceFields.SupplierName or InvoiceFields.CustomerName)
        {
            v = v.TrimEnd('.', ',').Replace("LIMITED", "LTD", StringComparison.Ordinal).Replace("LTD.", "LTD", StringComparison.Ordinal);
        }

        return v;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
