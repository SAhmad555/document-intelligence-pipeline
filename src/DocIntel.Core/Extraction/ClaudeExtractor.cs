using System.Diagnostics;
using Anthropic;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using DocIntel.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocIntel.Core.Extraction;

public sealed class ClaudeExtractorOptions
{
    public const string SectionName = "Claude";

    /// <summary>Model id. Defaults to Claude Opus 5.5.</summary>
    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>low | medium | high | xhigh | max. Extraction is a well-specified task; medium is the model's default.</summary>
    public string Effort { get; set; } = "medium";

    public int MaxTokens { get; set; } = 16000;

    /// <summary>Price per million input tokens in USD, used only for the cost estimate in results and eval reports.</summary>
    public decimal InputPricePerMillion { get; set; } = 4.00m;

    /// <summary>Price per million output tokens in USD.</summary>
    public decimal OutputPricePerMillion { get; set; } = 20.00m;
}

/// <summary>
/// Extracts invoice data with Claude using structured outputs (a JSON schema the response must match).
/// Accepts PDFs and images natively, so there is no separate OCR step, and plain text.
/// Server-side refusal fallback is enabled so a policy decline is re-served by a fallback model in the same call.
/// </summary>
public sealed class ClaudeExtractor(AnthropicClient client, IOptions<ClaudeExtractorOptions> options, ILogger<ClaudeExtractor> logger)
    : IInvoiceExtractor
{
    private static readonly Dictionary<string, System.Text.Json.JsonElement> Schema = ExtractionContract.BuildSchema();
    private readonly ClaudeExtractorOptions _options = options.Value;

    public string Name => "claude";

    public bool CanHandle(DocumentInput input) => input.IsPdf || input.IsImage || input.IsText;

    public async Task<ExtractionResult> ExtractAsync(DocumentInput input, CancellationToken cancellationToken = default)
    {
        if (!CanHandle(input))
        {
            throw new UnsupportedDocumentException($"Unsupported document type '{input.NormalizedContentType}' for '{input.FileName}'.");
        }

        var stopwatch = Stopwatch.StartNew();
        var parameters = new MessageCreateParams
        {
            Model = _options.Model,
            MaxTokens = _options.MaxTokens,
            Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
            Fallbacks = new Default(),
            System = ExtractionContract.SystemPrompt,
            OutputConfig = new BetaOutputConfig
            {
                Effort = _options.Effort,
                Format = new BetaJsonOutputFormat { Schema = Schema },
            },
            Messages = [new BetaUserMessageParam { Content = BuildContent(input) }],
        };

        var response = await client.Beta.Messages.Create(parameters, cancellationToken);
        stopwatch.Stop();

        if (response.StopReason == "refusal")
        {
            throw new ExtractionFailedException($"The model declined to process '{input.FileName}'.");
        }

        if (response.StopReason == "max_tokens")
        {
            throw new ExtractionFailedException($"Output for '{input.FileName}' was cut off at {_options.MaxTokens} tokens.");
        }

        var json = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        var (data, confidence) = ExtractionContract.Parse(json);

        var usage = new TokenUsage(response.Usage.InputTokens, response.Usage.OutputTokens);
        logger.LogInformation(
            "Extracted {File} with {Model} in {Ms} ms ({In} in / {Out} out tokens)",
            input.FileName, response.Model, stopwatch.ElapsedMilliseconds, usage.InputTokens, usage.OutputTokens);

        return new ExtractionResult
        {
            Data = data,
            Confidence = confidence,
            Extractor = Name,
            Model = response.Model,
            Duration = stopwatch.Elapsed,
            Usage = usage,
            EstimatedCostUsd = EstimateCost(usage),
        };
    }

    private decimal EstimateCost(TokenUsage usage) =>
        decimal.Round(
            (usage.InputTokens * _options.InputPricePerMillion + usage.OutputTokens * _options.OutputPricePerMillion) / 1_000_000m,
            6);

    private static List<BetaContentBlockParam> BuildContent(DocumentInput input)
    {
        var content = new List<BetaContentBlockParam>();
        if (input.IsPdf)
        {
            content.Add(new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = Convert.ToBase64String(input.Content) } });
        }
        else if (input.IsImage)
        {
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = Convert.ToBase64String(input.Content), MediaType = input.NormalizedContentType },
            });
        }
        else
        {
            content.Add(new BetaTextBlockParam { Text = $"<document name=\"{input.FileName}\">\n{input.ReadText()}\n</document>" });
        }

        content.Add(new BetaTextBlockParam { Text = ExtractionContract.UserInstruction });
        return content;
    }
}
