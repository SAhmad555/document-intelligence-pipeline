using DocIntel.Core.Models;

namespace DocIntel.Core.Extraction;

public interface IInvoiceExtractor
{
    /// <summary>Short identifier stored with every result, e.g. "claude" or "rule-based".</summary>
    string Name { get; }

    /// <summary>Whether this extractor can read the given document type at all.</summary>
    bool CanHandle(DocumentInput input);

    Task<ExtractionResult> ExtractAsync(DocumentInput input, CancellationToken cancellationToken = default);
}

/// <summary>Raised when no configured extractor can read a document (e.g. a PDF without an LLM configured).</summary>
public sealed class UnsupportedDocumentException(string message) : Exception(message);

/// <summary>Raised when the model declines or returns output that cannot be used.</summary>
public sealed class ExtractionFailedException(string message, Exception? inner = null) : Exception(message, inner);
