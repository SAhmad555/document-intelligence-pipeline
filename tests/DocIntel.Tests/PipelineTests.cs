using DocIntel.Core.Extraction;
using DocIntel.Core.Models;
using DocIntel.Core.Pipeline;
using DocIntel.Core.Validation;

namespace DocIntel.Tests;

public class PipelineTests
{
    /// <summary>Returns a fixed result, so routing can be tested without any model.</summary>
    internal sealed class StubExtractor(InvoiceData data, double confidence = 0.95) : IInvoiceExtractor
    {
        public string Name => "stub";

        public bool CanHandle(DocumentInput input) => true;

        public Task<ExtractionResult> ExtractAsync(DocumentInput input, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ExtractionResult
            {
                Data = data,
                Confidence = InvoiceFields.All.ToDictionary(f => f, _ => confidence),
                Extractor = Name,
            });
    }

    private static readonly DocumentInput AnyDoc = DocumentInput.FromText("x.txt", "x");

    private static Task<PipelineResult> Run(InvoiceData data, double confidence = 0.95) =>
        new DocumentPipeline(new StubExtractor(data, confidence), new InvoiceValidator(), new ReviewPolicy()).ProcessAsync(AnyDoc);

    [Fact]
    public async Task Clean_confident_invoice_is_auto_approved()
    {
        var result = await Run(ValidatorTests.ValidInvoice());
        Assert.Equal(ReviewStatus.AutoApproved, result.Status);
        Assert.Empty(result.ReviewReasons);
    }

    [Fact]
    public async Task Low_confidence_routes_to_review_even_when_valid()
    {
        var result = await Run(ValidatorTests.ValidInvoice(), confidence: 0.7);
        Assert.Equal(ReviewStatus.NeedsReview, result.Status);
        Assert.Contains(result.ReviewReasons, r => r.StartsWith("LOW_CONFIDENCE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Validation_error_caps_confidence_of_implicated_fields_and_routes_to_review()
    {
        var result = await Run(ValidatorTests.ValidInvoice() with { Total = 999m }, confidence: 1.0);

        Assert.Equal(ReviewStatus.NeedsReview, result.Status);
        Assert.Equal(0.5, result.Extraction.ConfidenceFor(InvoiceFields.Total));
        Assert.Equal(1.0, result.Extraction.ConfidenceFor(InvoiceFields.InvoiceNumber)); // untouched
    }

    [Fact]
    public async Task Missing_required_field_gets_zero_confidence()
    {
        var result = await Run(ValidatorTests.ValidInvoice() with { InvoiceNumber = null }, confidence: 1.0);
        Assert.Equal(0, result.Extraction.ConfidenceFor(InvoiceFields.InvoiceNumber));
        Assert.Equal(ReviewStatus.NeedsReview, result.Status);
    }

    [Fact]
    public async Task Absent_optional_field_does_not_block_auto_approval()
    {
        var result = await Run(ValidatorTests.ValidInvoice() with { SupplierVatNumber = null, DueDate = null });
        Assert.Equal(ReviewStatus.AutoApproved, result.Status);
    }
}
