using System.Text.Json.Serialization;
using Anthropic;
using DocIntel.Api.Data;
using DocIntel.Api.Endpoints;
using DocIntel.Core.Extraction;
using DocIntel.Core.Pipeline;
using DocIntel.Core.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>();

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Documents") ?? "Data Source=docintel.db"));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<InvoiceValidator>();
builder.Services.AddSingleton(builder.Configuration.GetSection(ReviewPolicy.SectionName).Get<ReviewPolicy>() ?? new ReviewPolicy());
builder.Services.Configure<ClaudeExtractorOptions>(builder.Configuration.GetSection(ClaudeExtractorOptions.SectionName));

// Extractor selection: "claude", "rule-based", or "auto" (Claude when an API key is configured, otherwise the offline baseline).
var provider = builder.Configuration["Extraction:Provider"] ?? "auto";
var hasApiKey = !string.IsNullOrWhiteSpace(builder.Configuration["ANTHROPIC_API_KEY"]);
var useClaude = provider == "claude" || (provider == "auto" && hasApiKey);

if (useClaude)
{
    builder.Services.AddSingleton(_ => new AnthropicClient { ApiKey = builder.Configuration["ANTHROPIC_API_KEY"] });
    builder.Services.AddSingleton<IInvoiceExtractor, ClaudeExtractor>();
}
else
{
    builder.Services.AddSingleton<IInvoiceExtractor, RuleBasedExtractor>();
}

builder.Services.AddSingleton<DocumentPipeline>();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("Document Intelligence API"));
app.MapHealthChecks("/health");
app.MapDocumentEndpoints();

app.Logger.LogInformation(
    "Extractor: {Extractor}{Hint}",
    app.Services.GetRequiredService<IInvoiceExtractor>().Name,
    useClaude ? $" ({app.Services.GetRequiredService<IOptions<ClaudeExtractorOptions>>().Value.Model})" : " (set ANTHROPIC_API_KEY to use Claude)");

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory in integration tests.</summary>
public partial class Program;
