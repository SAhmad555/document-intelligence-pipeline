using DocIntel.Core.Pipeline;
using Microsoft.EntityFrameworkCore;

namespace DocIntel.Api.Data;

/// <summary>
/// One processed document. Extraction output, the reviewer's corrections and run metadata are kept
/// side by side so that reviewed documents become labelled evaluation data (see the export endpoint).
/// </summary>
public sealed class DocumentRecord
{
    public Guid Id { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Content { get; set; }
    public DateTime UploadedAtUtc { get; set; }

    public ReviewStatus Status { get; set; }
    public required string Extractor { get; set; }
    public string? Model { get; set; }
    public string? Error { get; set; }

    public string? ExtractedJson { get; set; }
    public string? ConfidenceJson { get; set; }
    public string? IssuesJson { get; set; }
    public string? ReviewReasonsJson { get; set; }

    public string? CorrectedJson { get; set; }
    public string? CorrectedFieldsJson { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewNote { get; set; }

    public long DurationMs { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public decimal? CostUsd { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var doc = modelBuilder.Entity<DocumentRecord>();
        doc.HasKey(d => d.Id);
        doc.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        doc.HasIndex(d => d.Status);
        doc.HasIndex(d => d.UploadedAtUtc);
    }
}
