namespace DocIntel.Core.Models;

public sealed record TokenUsage(long InputTokens, long OutputTokens);

/// <summary>What an extractor returns: the data, a 0..1 confidence per field, and run metadata.</summary>
public sealed record ExtractionResult
{
    public required InvoiceData Data { get; init; }

    /// <summary>Confidence per field name (see <see cref="InvoiceFields"/>), 0 = not found / no trust, 1 = certain.</summary>
    public required IReadOnlyDictionary<string, double> Confidence { get; init; }

    public required string Extractor { get; init; }

    public string? Model { get; init; }

    public TimeSpan Duration { get; init; }

    public TokenUsage? Usage { get; init; }

    public decimal? EstimatedCostUsd { get; init; }

    public double ConfidenceFor(string field) => Confidence.TryGetValue(field, out var c) ? c : 0d;
}
