using DocIntel.Core.Extraction;
using DocIntel.Core.Models;
using DocIntel.Core.Validation;

namespace DocIntel.Core.Pipeline;

public enum ReviewStatus
{
    /// <summary>Every check passed; safe to post without a human.</summary>
    AutoApproved,

    /// <summary>Routed to the human review queue.</summary>
    NeedsReview,

    /// <summary>A reviewer confirmed or corrected the data.</summary>
    Approved,

    /// <summary>A reviewer rejected the document (not an invoice, duplicate, ...).</summary>
    Rejected,

    /// <summary>Extraction itself failed; the document needs manual keying.</summary>
    Failed,
}

public sealed record ReviewPolicy
{
    public const string SectionName = "ReviewPolicy";

    /// <summary>Minimum confidence for a required field before a human must look.</summary>
    public double RequiredFieldThreshold { get; init; } = 0.85;

    /// <summary>Minimum confidence for an optional field that has a value.</summary>
    public double OptionalFieldThreshold { get; init; } = 0.6;

    /// <summary>Confidence ceiling applied to any field implicated in a validation error.</summary>
    public double ValidationErrorConfidenceCap { get; init; } = 0.5;
}

public sealed record PipelineResult(
    ExtractionResult Extraction,
    IReadOnlyList<ValidationIssue> Issues,
    ReviewStatus Status,
    IReadOnlyList<string> ReviewReasons);

/// <summary>Extract, then validate, then adjust confidence, then route to auto-approve or human review.</summary>
public sealed class DocumentPipeline(IInvoiceExtractor extractor, InvoiceValidator validator, ReviewPolicy policy)
{
    public string ExtractorName => extractor.Name;

    public async Task<PipelineResult> ProcessAsync(DocumentInput input, CancellationToken cancellationToken = default)
    {
        var extraction = await extractor.ExtractAsync(input, cancellationToken);
        var issues = validator.Validate(extraction.Data);
        var adjusted = AdjustConfidence(extraction, issues);
        var (status, reasons) = Route(extraction.Data, adjusted, issues);
        return new PipelineResult(extraction with { Confidence = adjusted }, issues, status, reasons);
    }

    /// <summary>
    /// Model self-reported confidence is only loosely calibrated. Combine it with hard evidence:
    /// a field implicated in a failed rule cannot stay high-confidence, and a missing field has none.
    /// </summary>
    internal Dictionary<string, double> AdjustConfidence(ExtractionResult extraction, IReadOnlyList<ValidationIssue> issues)
    {
        var adjusted = InvoiceFields.All.ToDictionary(f => f, extraction.ConfidenceFor);

        foreach (var issue in issues.Where(i => i.Severity == IssueSeverity.Error))
        {
            foreach (var field in issue.Fields)
            {
                adjusted[field] = issue.Code == "MISSING_REQUIRED" ? 0 : Math.Min(adjusted[field], policy.ValidationErrorConfidenceCap);
            }
        }

        return adjusted;
    }

    internal (ReviewStatus Status, List<string> Reasons) Route(InvoiceData data, IReadOnlyDictionary<string, double> confidence, IReadOnlyList<ValidationIssue> issues)
    {
        var reasons = issues.Where(i => i.Severity == IssueSeverity.Error).Select(i => $"{i.Code}: {i.Message}").ToList();

        foreach (var field in InvoiceFields.All)
        {
            var c = confidence[field];
            if (InvoiceFields.IsRequired(field))
            {
                if (InvoiceFields.GetValue(data, field) is not null && c < policy.RequiredFieldThreshold)
                {
                    reasons.Add($"LOW_CONFIDENCE: required field '{field}' at {c:0.00} (threshold {policy.RequiredFieldThreshold:0.00}).");
                }
            }
            else if (InvoiceFields.GetValue(data, field) is not null && c < policy.OptionalFieldThreshold)
            {
                reasons.Add($"LOW_CONFIDENCE: '{field}' at {c:0.00} (threshold {policy.OptionalFieldThreshold:0.00}).");
            }
        }

        return (reasons.Count == 0 ? ReviewStatus.AutoApproved : ReviewStatus.NeedsReview, reasons);
    }
}
