using System.Text.Json;
using DocIntel.Core.Models;
using DocIntel.Core.Synthetic;

namespace DocIntel.Core.Evaluation;

/// <summary>One labelled document: the input plus what a perfect extractor would return.</summary>
public sealed record EvalCase(string Id, string Template, DocumentInput Input, InvoiceData Truth, bool HasArithmeticError);

/// <summary>
/// On disk a case is a document file plus a sibling <c>&lt;id&gt;.truth.json</c> label, so real
/// documents (PDF, PNG) can be dropped in next to the synthetic text ones.
/// </summary>
public static class EvalDataset
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private const string LabelSuffix = ".truth.json";

    public static void Write(string directory, IEnumerable<SyntheticInvoice> invoices)
    {
        Directory.CreateDirectory(directory);
        foreach (var inv in invoices)
        {
            File.WriteAllText(Path.Combine(directory, inv.Id + ".txt"), inv.Text.ReplaceLineEndings("\n"));
            var label = new Label(inv.Template, inv.HasArithmeticError, inv.Truth);
            File.WriteAllText(Path.Combine(directory, inv.Id + LabelSuffix), JsonSerializer.Serialize(label, JsonOptions));
        }
    }

    public static IReadOnlyList<EvalCase> Load(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Eval dataset folder '{directory}' not found. Run the 'generate' command first.");
        }

        var cases = new List<EvalCase>();
        foreach (var labelPath in Directory.EnumerateFiles(directory, "*" + LabelSuffix).Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileName(labelPath)[..^LabelSuffix.Length];
            var documentPath = Directory.EnumerateFiles(directory, id + ".*")
                .FirstOrDefault(p => !p.EndsWith(LabelSuffix, StringComparison.Ordinal))
                ?? throw new FileNotFoundException($"Label '{labelPath}' has no matching document file.");

            var label = JsonSerializer.Deserialize<Label>(File.ReadAllText(labelPath), JsonOptions)
                ?? throw new InvalidDataException($"Could not read label '{labelPath}'.");

            cases.Add(new EvalCase(id, label.Template ?? "real", DocumentInput.FromFile(documentPath), label.Truth, label.HasArithmeticError));
        }

        return cases;
    }

    private sealed record Label(string? Template, bool HasArithmeticError, InvoiceData Truth);
}
