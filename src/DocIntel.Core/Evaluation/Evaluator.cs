using System.Collections.Concurrent;
using System.Diagnostics;
using DocIntel.Core.Models;
using DocIntel.Core.Pipeline;

namespace DocIntel.Core.Evaluation;

public sealed record CaseResult(
    EvalCase Case,
    PipelineResult? Result,
    IReadOnlyDictionary<string, FieldOutcome> Outcomes,
    string? Error)
{
    public bool AllFieldsCorrect => Error is null && Outcomes.Values.All(o => o is FieldOutcome.Correct or FieldOutcome.CorrectlyEmpty);

    public bool AutoApproved => Result?.Status == ReviewStatus.AutoApproved;
}

/// <summary>Runs the full pipeline (extract + validate + route) over a labelled dataset and scores it.</summary>
public sealed class Evaluator(DocumentPipeline pipeline)
{
    public async Task<EvaluationReport> RunAsync(
        IReadOnlyList<EvalCase> cases,
        int maxConcurrency = 4,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentBag<CaseResult>();
        var done = 0;
        var stopwatch = Stopwatch.StartNew();

        await Parallel.ForEachAsync(
            cases,
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
            async (evalCase, ct) =>
            {
                results.Add(await RunCaseAsync(evalCase, ct));
                progress?.Report(Interlocked.Increment(ref done));
            });

        return EvaluationReport.Build(pipeline.ExtractorName, results.OrderBy(r => r.Case.Id, StringComparer.Ordinal).ToList(), stopwatch.Elapsed);
    }

    private async Task<CaseResult> RunCaseAsync(EvalCase evalCase, CancellationToken ct)
    {
        try
        {
            var result = await pipeline.ProcessAsync(evalCase.Input, ct);
            var outcomes = InvoiceFields.All.ToDictionary(f => f, f => FieldComparer.Compare(f, evalCase.Truth, result.Extraction.Data));
            return new CaseResult(evalCase, result, outcomes, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An extraction failure is scored as every present field missed - it must hurt the metrics, not vanish.
            var empty = new InvoiceData();
            var outcomes = InvoiceFields.All.ToDictionary(f => f, f => FieldComparer.Compare(f, evalCase.Truth, empty));
            return new CaseResult(evalCase, null, outcomes, ex.Message);
        }
    }
}
