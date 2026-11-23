using System.Text;

namespace DocIntel.Core.Models;

/// <summary>A document as received from an upload or read from disk.</summary>
public sealed record DocumentInput(string FileName, string ContentType, byte[] Content)
{
    private static readonly Dictionary<string, string> ContentTypesByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif",
        [".txt"] = "text/plain",
        [".md"] = "text/plain",
        [".eml"] = "text/plain",
    };

    public static IReadOnlyCollection<string> SupportedExtensions => ContentTypesByExtension.Keys;

    public bool IsPdf => NormalizedContentType == "application/pdf";

    public bool IsImage => NormalizedContentType is "image/png" or "image/jpeg" or "image/webp" or "image/gif";

    public bool IsText => NormalizedContentType.StartsWith("text/", StringComparison.Ordinal);

    /// <summary>Content type, trusting the file extension over a generic browser-supplied type.</summary>
    public string NormalizedContentType =>
        ContentTypesByExtension.TryGetValue(Path.GetExtension(FileName), out var byExtension)
            ? byExtension
            : ContentType.ToLowerInvariant();

    public string ReadText() => Encoding.UTF8.GetString(Content);

    public static DocumentInput FromFile(string path)
    {
        var contentType = ContentTypesByExtension.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
        return new DocumentInput(Path.GetFileName(path), contentType, File.ReadAllBytes(path));
    }

    public static DocumentInput FromText(string fileName, string text) =>
        new(fileName, "text/plain", Encoding.UTF8.GetBytes(text));
}
