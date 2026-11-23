using System.Text.Json;
using Anthropic;
using DocIntel.Core.Evaluation;
using DocIntel.Core.Extraction;
using DocIntel.Core.Pipeline;
using DocIntel.Core.Synthetic;
using DocIntel.Core.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// docintel-eval generate [--out data/eval] [--count 80] [--seed 42]
// docintel-eval run [--data data/eval] [--extractor rule-based|claude] [--out reports] [--limit N] [--concurrency 4] [--min-f1 0.0]
var command = args.FirstOrDefault() ?? "help";
var options = ParseOptions(args.Skip(1).ToArray());

switch (command)
{
    case "generate":
    {
        var outDir = options.GetValueOrDefault("out", "data/eval");
        var count = int.Parse(options.GetValueOrDefault("count", "80"));
        var seed = int.Parse(options.GetValueOrDefault("seed", "42"));
        EvalDataset.Write(outDir, new InvoiceGenerator(seed).Generate(count));
        Console.WriteLine($"Wrote {count} labelled invoices to {Path.GetFullPath(outDir)} (seed {seed}).");
        return 0;
    }

    case "run":
    {
        var cases = EvalDataset.Load(options.GetValueOrDefault("data", "data/eval"));
        if (options.TryGetValue("limit", out var limit))
        {
            cases = cases.Take(int.Parse(limit)).ToList();
        }

        var extractorName = options.GetValueOrDefault("extractor", "rule-based");
        IInvoiceExtractor extractor = extractorName switch
        {
            "rule-based" => new RuleBasedExtractor(),
            "claude" => new ClaudeExtractor(
                new AnthropicClient(),
                Options.Create(new ClaudeExtractorOptions
                {
                    Model = options.GetValueOrDefault("model", "claude-opus-5-5"),
                    Effort = options.GetValueOrDefault("effort", "medium"),
                }),
                NullLogger<ClaudeExtractor>.Instance),
            _ => throw new ArgumentException($"Unknown extractor '{extractorName}'. Use rule-based or claude."),
        };

        var pipeline = new DocumentPipeline(extractor, new InvoiceValidator(), new ReviewPolicy());
        var concurrency = int.Parse(options.GetValueOrDefault("concurrency", extractorName == "claude" ? "4" : "8"));

        Console.WriteLine($"Evaluating '{extractor.Name}' on {cases.Count} documents...");
        var progress = new Progress<int>(n => { if (n % 10 == 0 || n == cases.Count) Console.WriteLine($"  {n}/{cases.Count}"); });
        var report = await new Evaluator(pipeline).RunAsync(cases, concurrency, progress);

        var outDir = options.GetValueOrDefault("out", "reports");
        Directory.CreateDirectory(outDir);
        var stem = Path.Combine(outDir, $"eval-{extractor.Name}");
        await File.WriteAllTextAsync(stem + ".md", report.ToMarkdown());
        await File.WriteAllTextAsync(stem + ".json", JsonSerializer.Serialize(report, EvalDataset.JsonOptions));

        Console.WriteLine();
        Console.WriteLine($"Field F1:                 {report.OverallF1:P1}");
        Console.WriteLine($"Documents fully correct:  {report.DocumentAccuracy:P1}");
        Console.WriteLine($"Auto-approved:            {report.AutoApprovalRate:P1}");
        Console.WriteLine($"Auto-approved but wrong:  {report.AutoApprovedErrorRate:P1}");
        Console.WriteLine($"Arithmetic errors caught: {report.ArithmeticErrorCatchRate:P0}");
        if (report.EstimatedCostUsd > 0) Console.WriteLine($"Estimated cost:           ${report.EstimatedCostUsd:0.0000}");
        Console.WriteLine($"Report: {Path.GetFullPath(stem)}.md");

        // Quality gate for CI: fail the build if accuracy regresses below the agreed floor.
        var minF1 = double.Parse(options.GetValueOrDefault("min-f1", "0"), System.Globalization.CultureInfo.InvariantCulture);
        if (report.OverallF1 < minF1)
        {
            Console.Error.WriteLine($"FAIL: field F1 {report.OverallF1:P1} is below the gate of {minF1:P1}.");
            return 1;
        }

        return 0;
    }

    default:
        Console.WriteLine("""
            docintel-eval - dataset generation and evaluation for the document intelligence pipeline

              generate  [--out data/eval] [--count 80] [--seed 42]
              run       [--data data/eval] [--extractor rule-based|claude] [--model claude-opus-5-5]
                        [--effort medium] [--out reports] [--limit N] [--concurrency 4] [--min-f1 0.0]

            The claude extractor reads ANTHROPIC_API_KEY (or an `ant auth login` profile).
            """);
        return command == "help" ? 0 : 1;
}

static Dictionary<string, string> ParseOptions(string[] args)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            result[args[i][2..]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true";
        }
    }

    return result;
}
