using System.Globalization;
using System.Text;
using DocIntel.Core.Models;

namespace DocIntel.Core.Evaluation;

public sealed record FieldMetrics(string Field, int Correct, int Wrong, int Missed, int Spurious, int CorrectlyEmpty)
{
    /// <summary>Of the values we output, how many were right.</summary>
    public double Precision => Ratio(Correct, Correct + Wrong + Spurious);

    /// <summary>Of the values that exist, how many we got right.</summary>
    public double Recall => Ratio(Correct, Correct + Wrong + Missed);

    public double F1 => Precision + Recall == 0 ? 0 : 2 * Precision * Recall / (Precision + Recall);

    public double Accuracy => Ratio(Correct + CorrectlyEmpty, Correct + Wrong + Missed + Spurious + CorrectlyEmpty);

    internal static double Ratio(int a, int b) => b == 0 ? 1 : (double)a / b;
}

public sealed record TemplateMetrics(string Template, int Documents, double DocumentAccuracy, double FieldAccuracy, double AutoApprovalRate);

/// <summary>
/// Scores that matter for an accounts-payable pipeline, not just model accuracy:
/// how much flows straight through, and how often straight-through output is wrong (the costly case).
/// </summary>
public sealed record EvaluationReport
{
    public required string Extractor { get; init; }
    public required DateTimeOffset RunAt { get; init; }
    public required int Documents { get; init; }
    public required int Failures { get; init; }
    public required IReadOnlyList<FieldMetrics> Fields { get; init; }
    public required IReadOnlyList<TemplateMetrics> Templates { get; init; }

    /// <summary>Micro-averaged F1 across all fields.</summary>
    public required double OverallF1 { get; init; }

    /// <summary>Documents with every field correct.</summary>
    public required double DocumentAccuracy { get; init; }

    /// <summary>Share of documents that needed no human (straight-through processing).</summary>
    public required double AutoApprovalRate { get; init; }

    /// <summary>Of auto-approved documents, the share with at least one wrong field. This should be ~0.</summary>
    public required double AutoApprovedErrorRate { get; init; }

    /// <summary>Of documents with a printed arithmetic error, the share routed to review.</summary>
    public required double ArithmeticErrorCatchRate { get; init; }

    public required double LatencyP50Ms { get; init; }
    public required double LatencyP95Ms { get; init; }
    public required long InputTokens { get; init; }
    public required long OutputTokens { get; init; }
    public required decimal EstimatedCostUsd { get; init; }
    public required double WallClockSeconds { get; init; }
    public required IReadOnlyList<string> SampleErrors { get; init; }

    public static EvaluationReport Build(string extractor, IReadOnlyList<CaseResult> results, TimeSpan wallClock)
    {
        var fields = InvoiceFields.All.Select(f =>
        {
            int Count(FieldOutcome o) => results.Count(r => r.Outcomes[f] == o);
            return new FieldMetrics(f, Count(FieldOutcome.Correct), Count(FieldOutcome.Wrong), Count(FieldOutcome.Missed), Count(FieldOutcome.Spurious), Count(FieldOutcome.CorrectlyEmpty));
        }).ToList();

        var tp = fields.Sum(f => f.Correct);
        var precision = FieldMetrics.Ratio(tp, fields.Sum(f => f.Correct + f.Wrong + f.Spurious));
        var recall = FieldMetrics.Ratio(tp, fields.Sum(f => f.Correct + f.Wrong + f.Missed));

        var autoApproved = results.Where(r => r.AutoApproved).ToList();
        var withErrors = results.Where(r => r.Case.HasArithmeticError).ToList();
        var latencies = results.Where(r => r.Result is not null).Select(r => r.Result!.Extraction.Duration.TotalMilliseconds).Order().ToList();

        var templates = results.GroupBy(r => r.Case.Template).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => new TemplateMetrics(
            g.Key,
            g.Count(),
            FieldMetrics.Ratio(g.Count(r => r.AllFieldsCorrect), g.Count()),
            FieldMetrics.Ratio(
                g.Sum(r => r.Outcomes.Values.Count(o => o is FieldOutcome.Correct or FieldOutcome.CorrectlyEmpty)),
                g.Sum(r => r.Outcomes.Count)),
            FieldMetrics.Ratio(g.Count(r => r.AutoApproved), g.Count()))).ToList();

        var sampleErrors = results
            .Where(r => !r.AllFieldsCorrect)
            .SelectMany(r => r.Error is not null
                ? [$"{r.Case.Id} ({r.Case.Template}): extraction failed - {r.Error}"]
                : r.Outcomes.Where(o => o.Value is FieldOutcome.Wrong or FieldOutcome.Missed or FieldOutcome.Spurious)
                    .Select(o => $"{r.Case.Id} ({r.Case.Template}): {o.Key} {o.Value.ToString().ToLowerInvariant()} - expected '{Show(InvoiceFields.GetValue(r.Case.Truth, o.Key))}', got '{Show(InvoiceFields.GetValue(r.Result!.Extraction.Data, o.Key))}'"))
            .Take(25)
            .ToList();

        return new EvaluationReport
        {
            Extractor = extractor,
            RunAt = DateTimeOffset.UtcNow,
            Documents = results.Count,
            Failures = results.Count(r => r.Error is not null),
            Fields = fields,
            Templates = templates,
            OverallF1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall),
            DocumentAccuracy = FieldMetrics.Ratio(results.Count(r => r.AllFieldsCorrect), results.Count),
            AutoApprovalRate = FieldMetrics.Ratio(autoApproved.Count, results.Count),
            AutoApprovedErrorRate = autoApproved.Count == 0 ? 0 : (double)autoApproved.Count(r => !r.AllFieldsCorrect) / autoApproved.Count,
            ArithmeticErrorCatchRate = FieldMetrics.Ratio(withErrors.Count(r => !r.AutoApproved), withErrors.Count),
            LatencyP50Ms = Percentile(latencies, 0.50),
            LatencyP95Ms = Percentile(latencies, 0.95),
            InputTokens = results.Sum(r => r.Result?.Extraction.Usage?.InputTokens ?? 0),
            OutputTokens = results.Sum(r => r.Result?.Extraction.Usage?.OutputTokens ?? 0),
            EstimatedCostUsd = results.Sum(r => r.Result?.Extraction.EstimatedCostUsd ?? 0),
            WallClockSeconds = wallClock.TotalSeconds,
            SampleErrors = sampleErrors,
        };
    }

    public string ToMarkdown()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(c, $"# Evaluation report - `{Extractor}`");
        sb.AppendLine();
        sb.AppendLine(c, $"Run {RunAt:yyyy-MM-dd HH:mm} UTC over **{Documents} documents** ({Failures} extraction failures).");
        sb.AppendLine();
        sb.AppendLine("## Headline");
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine(c, $"| Field F1 (micro) | **{OverallF1:P1}** |");
        sb.AppendLine(c, $"| Documents fully correct | {DocumentAccuracy:P1} |");
        sb.AppendLine(c, $"| Straight-through (auto-approved) | {AutoApprovalRate:P1} |");
        sb.AppendLine(c, $"| Auto-approved but wrong | **{AutoApprovedErrorRate:P1}** |");
        sb.AppendLine(c, $"| Printed arithmetic errors caught | {ArithmeticErrorCatchRate:P0} |");
        sb.AppendLine(c, $"| Latency p50 / p95 | {LatencyP50Ms:0} ms / {LatencyP95Ms:0} ms |");
        if (InputTokens > 0)
        {
            sb.AppendLine(c, $"| Tokens (in / out) | {InputTokens:N0} / {OutputTokens:N0} |");
            sb.AppendLine(c, $"| Estimated cost | ${EstimatedCostUsd:0.0000} total, ${(Documents == 0 ? 0 : EstimatedCostUsd / Documents):0.00000} per document |");
        }

        sb.AppendLine();
        sb.AppendLine("## Per field");
        sb.AppendLine();
        sb.AppendLine("| Field | Precision | Recall | F1 | Correct | Wrong | Missed | Spurious |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var f in Fields)
        {
            sb.AppendLine(c, $"| {f.Field} | {f.Precision:P1} | {f.Recall:P1} | {f.F1:P1} | {f.Correct} | {f.Wrong} | {f.Missed} | {f.Spurious} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Per layout");
        sb.AppendLine();
        sb.AppendLine("| Layout | Docs | Fully correct | Field accuracy | Auto-approved |");
        sb.AppendLine("|---|---|---|---|---|");
        foreach (var t in Templates)
        {
            sb.AppendLine(c, $"| {t.Template} | {t.Documents} | {t.DocumentAccuracy:P1} | {t.FieldAccuracy:P1} | {t.AutoApprovalRate:P1} |");
        }

        if (SampleErrors.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Sample errors");
            sb.AppendLine();
            foreach (var e in SampleErrors)
            {
                sb.AppendLine(c, $"- {e}");
            }
        }

        return sb.ToString();
    }

    private static string Show(object? value) => value switch
    {
        null => "null",
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IReadOnlyList<LineItem> lines => $"{lines.Count} lines",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static double Percentile(IReadOnlyList<double> sorted, double p) =>
        sorted.Count == 0 ? 0 : sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];
}
