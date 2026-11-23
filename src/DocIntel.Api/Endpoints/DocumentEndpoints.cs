using System.Text.Json;
using DocIntel.Api.Data;
using DocIntel.Core.Evaluation;
using DocIntel.Core.Extraction;
using DocIntel.Core.Models;
using DocIntel.Core.Pipeline;
using DocIntel.Core.Validation;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace DocIntel.Api.Endpoints;

public sealed record DocumentSummary(Guid Id, string FileName, DateTime UploadedAtUtc, ReviewStatus Status, string? SupplierName, decimal? Total, string? Currency, int ReviewReasonCount);

public sealed record DocumentDetail(
    Guid Id,
    string FileName,
    string ContentType,
    DateTime UploadedAtUtc,
    ReviewStatus Status,
    string Extractor,
    string? Model,
    string? Error,
    InvoiceData? Data,
    IReadOnlyDictionary<string, double>? Confidence,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<string> ReviewReasons,
    InvoiceData? Corrected,
    IReadOnlyList<string> CorrectedFields,
    string? ReviewedBy,
    DateTime? ReviewedAtUtc,
    long DurationMs,
    long? InputTokens,
    long? OutputTokens,
    decimal? CostUsd);

public sealed record ReviewRequest(InvoiceData Data, string? Reviewer, string? Note);

public sealed record RejectRequest(string Reason, string? Reviewer);

public sealed record FieldCorrectionRate(string Field, int Corrected, int Reviewed, double Rate);

public sealed record PipelineStats(
    int Total,
    IReadOnlyDictionary<string, int> ByStatus,
    double AutoApprovalRate,
    IReadOnlyList<FieldCorrectionRate> FieldCorrectionRates,
    decimal TotalCostUsd,
    string Extractor);

public sealed record LabelledExample(string FileName, string ContentType, string ContentBase64, InvoiceData Truth);

public static class DocumentEndpoints
{
    public const long MaxUploadBytes = 10 * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").WithTags("Documents");

        api.MapPost("/documents", Upload)
            .DisableAntiforgery()
            .WithSummary("Upload an invoice (PDF, image or text); it is extracted, validated and routed.");
        api.MapGet("/documents", List).WithSummary("List documents, optionally filtered by status.");
        api.MapGet("/documents/{id:guid}", Get).WithSummary("Full extraction result, confidence, issues and review state.");
        api.MapGet("/documents/{id:guid}/file", GetFile).WithSummary("Download the original document.");
        api.MapPost("/documents/{id:guid}/review", Review).WithSummary("Approve a document, with corrections if needed.");
        api.MapPost("/documents/{id:guid}/reject", Reject).WithSummary("Reject a document (not an invoice, duplicate, ...).");
        api.MapGet("/stats", Stats).WithSummary("Straight-through rate and which fields reviewers correct most.");
        api.MapGet("/labels/export", ExportLabels).WithSummary("Reviewed documents as labelled examples for the eval set.");

        return app;
    }

    private static async Task<Results<Created<DocumentDetail>, BadRequest<string>, UnprocessableEntity<string>>> Upload(
        IFormFile file, DocumentPipeline pipeline, AppDbContext db, ILoggerFactory loggers, CancellationToken ct)
    {
        if (file.Length == 0) return TypedResults.BadRequest("The file is empty.");
        if (file.Length > MaxUploadBytes) return TypedResults.BadRequest($"Files are limited to {MaxUploadBytes / 1024 / 1024} MB.");

        var extension = Path.GetExtension(file.FileName);
        if (!DocumentInput.SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return TypedResults.BadRequest($"Unsupported file type '{extension}'. Supported: {string.Join(", ", DocumentInput.SupportedExtensions)}.");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var input = new DocumentInput(Path.GetFileName(file.FileName), file.ContentType, buffer.ToArray());

        var record = new DocumentRecord
        {
            Id = Guid.NewGuid(),
            FileName = input.FileName,
            ContentType = input.NormalizedContentType,
            Content = input.Content,
            UploadedAtUtc = DateTime.UtcNow,
            Extractor = pipeline.ExtractorName,
        };

        try
        {
            var result = await pipeline.ProcessAsync(input, ct);
            record.Status = result.Status;
            record.Model = result.Extraction.Model;
            record.ExtractedJson = JsonSerializer.Serialize(result.Extraction.Data, Json);
            record.ConfidenceJson = JsonSerializer.Serialize(result.Extraction.Confidence, Json);
            record.IssuesJson = JsonSerializer.Serialize(result.Issues, Json);
            record.ReviewReasonsJson = JsonSerializer.Serialize(result.ReviewReasons, Json);
            record.DurationMs = (long)result.Extraction.Duration.TotalMilliseconds;
            record.InputTokens = result.Extraction.Usage?.InputTokens;
            record.OutputTokens = result.Extraction.Usage?.OutputTokens;
            record.CostUsd = result.Extraction.EstimatedCostUsd;
        }
        catch (UnsupportedDocumentException ex)
        {
            return TypedResults.UnprocessableEntity(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the document: a failed extraction still needs a human to key it in.
            loggers.CreateLogger("DocIntel.Upload").LogError(ex, "Extraction failed for {File}", input.FileName);
            record.Status = ReviewStatus.Failed;
            record.Error = ex.Message;
        }

        db.Documents.Add(record);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/documents/{record.Id}", ToDetail(record));
    }

    private static async Task<Ok<List<DocumentSummary>>> List(AppDbContext db, ReviewStatus? status, CancellationToken ct)
    {
        var query = db.Documents.AsNoTracking();
        if (status is not null) query = query.Where(d => d.Status == status);

        var rows = await query.OrderByDescending(d => d.UploadedAtUtc).Take(200)
            .Select(d => new { d.Id, d.FileName, d.UploadedAtUtc, d.Status, d.ExtractedJson, d.CorrectedJson, d.ReviewReasonsJson })
            .ToListAsync(ct);

        return TypedResults.Ok(rows.Select(r =>
        {
            var data = Deserialize<InvoiceData>(r.CorrectedJson ?? r.ExtractedJson);
            var reasons = Deserialize<List<string>>(r.ReviewReasonsJson) ?? [];
            return new DocumentSummary(r.Id, r.FileName, r.UploadedAtUtc, r.Status, data?.SupplierName, data?.Total, data?.Currency, reasons.Count);
        }).ToList());
    }

    private static async Task<Results<Ok<DocumentDetail>, NotFound>> Get(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) is { } record
            ? TypedResults.Ok(ToDetail(record))
            : TypedResults.NotFound();

    private static async Task<Results<FileContentHttpResult, NotFound>> GetFile(Guid id, AppDbContext db, CancellationToken ct) =>
        await db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct) is { } record
            ? TypedResults.File(record.Content, record.ContentType, record.FileName)
            : TypedResults.NotFound();

    private static async Task<Results<Ok<DocumentDetail>, NotFound, Conflict<string>, BadRequest<string>>> Review(
        Guid id, ReviewRequest request, AppDbContext db, InvoiceValidator validator, CancellationToken ct)
    {
        var record = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (record is null) return TypedResults.NotFound();
        if (record.Status is ReviewStatus.Approved or ReviewStatus.Rejected) return TypedResults.Conflict($"Document is already {record.Status}.");

        // A human can override arithmetic warnings (the supplier really did print it), but cannot post an invoice with no number or total.
        var missing = InvoiceFields.Required.Where(f => InvoiceFields.GetValue(request.Data, f) is null).ToList();
        if (missing.Count > 0) return TypedResults.BadRequest($"Cannot approve without required fields: {string.Join(", ", missing)}.");

        // Record exactly which fields the human changed: this is the feedback signal for prompt and threshold tuning.
        var extracted = Deserialize<InvoiceData>(record.ExtractedJson) ?? new InvoiceData();
        var correctedFields = InvoiceFields.All
            .Where(f => FieldComparer.Compare(f, request.Data, extracted) is not (FieldOutcome.Correct or FieldOutcome.CorrectlyEmpty))
            .ToList();

        record.Status = ReviewStatus.Approved;
        record.CorrectedJson = JsonSerializer.Serialize(request.Data, Json);
        record.CorrectedFieldsJson = JsonSerializer.Serialize(correctedFields, Json);
        record.IssuesJson = JsonSerializer.Serialize(validator.Validate(request.Data), Json);
        record.ReviewedBy = string.IsNullOrWhiteSpace(request.Reviewer) ? "reviewer" : request.Reviewer.Trim();
        record.ReviewNote = request.Note;
        record.ReviewedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDetail(record));
    }

    private static async Task<Results<Ok<DocumentDetail>, NotFound, Conflict<string>>> Reject(Guid id, RejectRequest request, AppDbContext db, CancellationToken ct)
    {
        var record = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (record is null) return TypedResults.NotFound();
        if (record.Status is ReviewStatus.Approved or ReviewStatus.Rejected) return TypedResults.Conflict($"Document is already {record.Status}.");

        record.Status = ReviewStatus.Rejected;
        record.ReviewNote = request.Reason;
        record.ReviewedBy = string.IsNullOrWhiteSpace(request.Reviewer) ? "reviewer" : request.Reviewer.Trim();
        record.ReviewedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ToDetail(record));
    }

    private static async Task<Ok<PipelineStats>> Stats(AppDbContext db, DocumentPipeline pipeline, CancellationToken ct)
    {
        var rows = await db.Documents.AsNoTracking()
            .Select(d => new { d.Status, d.CorrectedFieldsJson, d.CostUsd })
            .ToListAsync(ct);

        var byStatus = Enum.GetValues<ReviewStatus>().ToDictionary(s => s.ToString(), s => rows.Count(r => r.Status == s));
        var reviewed = rows.Where(r => r.Status == ReviewStatus.Approved && r.CorrectedFieldsJson is not null)
            .Select(r => Deserialize<List<string>>(r.CorrectedFieldsJson) ?? [])
            .ToList();

        var rates = InvoiceFields.All.Select(f =>
        {
            var corrected = reviewed.Count(c => c.Contains(f));
            return new FieldCorrectionRate(f, corrected, reviewed.Count, reviewed.Count == 0 ? 0 : (double)corrected / reviewed.Count);
        }).OrderByDescending(r => r.Rate).ToList();

        return TypedResults.Ok(new PipelineStats(
            rows.Count,
            byStatus,
            rows.Count == 0 ? 0 : (double)rows.Count(r => r.Status == ReviewStatus.AutoApproved) / rows.Count,
            rates,
            rows.Sum(r => r.CostUsd ?? 0),
            pipeline.ExtractorName));
    }

    private static async Task<Ok<List<LabelledExample>>> ExportLabels(AppDbContext db, CancellationToken ct)
    {
        var approved = await db.Documents.AsNoTracking()
            .Where(d => d.Status == ReviewStatus.Approved && d.CorrectedJson != null)
            .ToListAsync(ct);

        return TypedResults.Ok(approved
            .Select(d => new LabelledExample(d.FileName, d.ContentType, Convert.ToBase64String(d.Content), Deserialize<InvoiceData>(d.CorrectedJson)!))
            .ToList());
    }

    private static DocumentDetail ToDetail(DocumentRecord r) => new(
        r.Id,
        r.FileName,
        r.ContentType,
        r.UploadedAtUtc,
        r.Status,
        r.Extractor,
        r.Model,
        r.Error,
        Deserialize<InvoiceData>(r.ExtractedJson),
        Deserialize<Dictionary<string, double>>(r.ConfidenceJson),
        Deserialize<List<ValidationIssue>>(r.IssuesJson) ?? [],
        Deserialize<List<string>>(r.ReviewReasonsJson) ?? [],
        Deserialize<InvoiceData>(r.CorrectedJson),
        Deserialize<List<string>>(r.CorrectedFieldsJson) ?? [],
        r.ReviewedBy,
        r.ReviewedAtUtc,
        r.DurationMs,
        r.InputTokens,
        r.OutputTokens,
        r.CostUsd);

    private static T? Deserialize<T>(string? json) where T : class =>
        json is null ? null : JsonSerializer.Deserialize<T>(json, Json);
}
