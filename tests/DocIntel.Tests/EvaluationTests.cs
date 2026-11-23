using DocIntel.Core.Evaluation;
using DocIntel.Core.Extraction;
using DocIntel.Core.Models;
using DocIntel.Core.Pipeline;
using DocIntel.Core.Synthetic;
using DocIntel.Core.Validation;

namespace DocIntel.Tests;

public class EvaluationTests
{
    private static readonly InvoiceData Truth = ValidatorTests.ValidInvoice();

    [Theory]
    [InlineData("Northwind Analytics Limited")]
    [InlineData("  northwind   analytics ltd. ")]
    public void Supplier_names_compare_by_meaning_not_formatting(string predicted) =>
        Assert.Equal(FieldOutcome.Correct, FieldComparer.Compare(InvoiceFields.SupplierName, Truth, Truth with { SupplierName = predicted }));

    [Fact]
    public void Field_outcomes_cover_every_case()
    {
        Assert.Equal(FieldOutcome.Wrong, FieldComparer.Compare(InvoiceFields.Total, Truth, Truth with { Total = 121m }));
        Assert.Equal(FieldOutcome.Missed, FieldComparer.Compare(InvoiceFields.Total, Truth, Truth with { Total = null }));
        Assert.Equal(FieldOutcome.Spurious, FieldComparer.Compare(InvoiceFields.DueDate, Truth with { DueDate = null }, Truth));
        Assert.Equal(FieldOutcome.CorrectlyEmpty, FieldComparer.Compare(InvoiceFields.DueDate, Truth with { DueDate = null }, Truth with { DueDate = null }));
    }

    [Fact]
    public void Metrics_follow_standard_definitions()
    {
        var m = new FieldMetrics("f", Correct: 8, Wrong: 1, Missed: 1, Spurious: 0, CorrectlyEmpty: 0);
        Assert.Equal(8.0 / 9, m.Precision, 6);
        Assert.Equal(8.0 / 10, m.Recall, 6);
    }

    [Fact]
    public async Task A_perfect_extractor_scores_100_percent_and_catches_every_printed_error()
    {
        var cases = new InvoiceGenerator(5).Generate(40)
            .Select(i => new EvalCase(i.Id, i.Template, DocumentInput.FromText(i.Id + ".txt", i.Text), i.Truth, i.HasArithmeticError))
            .ToList();
        var oracle = new OracleExtractor(cases.ToDictionary(c => c.Input.FileName, c => c.Truth));

        var report = await new Evaluator(new DocumentPipeline(oracle, new InvoiceValidator(), new ReviewPolicy())).RunAsync(cases);

        Assert.Equal(1.0, report.OverallF1);
        Assert.Equal(1.0, report.DocumentAccuracy);
        Assert.Equal(0.0, report.AutoApprovedErrorRate);
        Assert.Equal(1.0, report.ArithmeticErrorCatchRate);
    }

    [Fact]
    public async Task Rule_based_baseline_stays_above_its_floor_on_the_synthetic_set()
    {
        var cases = new InvoiceGenerator(42).Generate(80)
            .Select(i => new EvalCase(i.Id, i.Template, DocumentInput.FromText(i.Id + ".txt", i.Text), i.Truth, i.HasArithmeticError))
            .ToList();

        var report = await new Evaluator(new DocumentPipeline(new RuleBasedExtractor(), new InvoiceValidator(), new ReviewPolicy())).RunAsync(cases);

        Assert.True(report.OverallF1 > 0.75, $"F1 regressed to {report.OverallF1:P1}");
        Assert.Equal(1.0, report.ArithmeticErrorCatchRate);
        Assert.Contains("# Evaluation report", report.ToMarkdown(), StringComparison.Ordinal);
    }

    /// <summary>Returns ground truth: shows the scoring and routing are right when extraction is perfect.</summary>
    private sealed class OracleExtractor(Dictionary<string, InvoiceData> truth) : IInvoiceExtractor
    {
        public string Name => "oracle";

        public bool CanHandle(DocumentInput input) => true;

        public Task<ExtractionResult> ExtractAsync(DocumentInput input, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExtractionResult
            {
                Data = truth[input.FileName],
                Confidence = InvoiceFields.All.ToDictionary(f => f, _ => 1.0),
                Extractor = Name,
            });
    }
}
