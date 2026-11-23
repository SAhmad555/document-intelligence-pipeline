using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocIntel.Api.Endpoints;
using DocIntel.Core.Pipeline;
using DocIntel.Core.Synthetic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DocIntel.Tests;

public sealed class ApiTests : IClassFixture<ApiTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _db = Path.Combine(Path.GetTempPath(), $"docintel-test-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:Documents", $"Data Source={_db}");
            builder.UseSetting("Extraction:Provider", "rule-based");
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private readonly HttpClient _client;

    public ApiTests(Factory factory) => _client = factory.CreateClient();

    private async Task<DocumentDetail> Upload(string fileName, string text)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "file", fileName);
        var response = await _client.PostAsync(new Uri("/api/documents", UriKind.Relative), form);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<DocumentDetail>(Json))!;
    }

    [Fact]
    public async Task Upload_extracts_validates_and_persists()
    {
        var invoice = new InvoiceGenerator(11).Generate(2).Last(); // modern layout
        var doc = await Upload("modern.txt", invoice.Text);

        Assert.Equal(invoice.Truth.Total, doc.Data!.Total);
        Assert.Equal("rule-based", doc.Extractor);

        var fetched = await _client.GetFromJsonAsync<DocumentDetail>($"/api/documents/{doc.Id}", Json);
        Assert.Equal(doc.Status, fetched!.Status);
    }

    [Fact]
    public async Task Review_records_corrected_fields_and_feeds_stats_and_label_export()
    {
        var invoice = new InvoiceGenerator(3).Generate(3).Last(); // email layout - the baseline misses most of it
        var doc = await Upload("email.txt", invoice.Text);
        Assert.Equal(ReviewStatus.NeedsReview, doc.Status);

        var review = await _client.PostAsJsonAsync($"/api/documents/{doc.Id}/review", new ReviewRequest(invoice.Truth, "tester", null), Json);
        review.EnsureSuccessStatusCode();
        var reviewed = (await review.Content.ReadFromJsonAsync<DocumentDetail>(Json))!;

        Assert.Equal(ReviewStatus.Approved, reviewed.Status);
        Assert.Contains("total", reviewed.CorrectedFields);

        var stats = await _client.GetFromJsonAsync<PipelineStats>("/api/stats", Json);
        Assert.Contains(stats!.FieldCorrectionRates, r => r is { Field: "total", Corrected: > 0 });

        var labels = await _client.GetFromJsonAsync<List<LabelledExample>>("/api/labels/export", Json);
        Assert.Contains(labels!, l => l.FileName == "email.txt" && l.Truth.Total == invoice.Truth.Total);
    }

    [Fact]
    public async Task Approving_without_required_fields_is_refused()
    {
        var doc = await Upload("blank.txt", "nothing useful here");
        var response = await _client.PostAsJsonAsync($"/api/documents/{doc.Id}/review", new ReviewRequest(doc.Data!, null, null), Json);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pdf_without_llm_configured_returns_422()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([0x25, 0x50, 0x44, 0x46]), "file", "scan.pdf");
        var response = await _client.PostAsync(new Uri("/api/documents", UriKind.Relative), form);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Unsupported_extension_returns_400()
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([1, 2, 3]), "file", "macro.xlsm");
        var response = await _client.PostAsync(new Uri("/api/documents", UriKind.Relative), form);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_is_up() =>
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync(new Uri("/health", UriKind.Relative))).StatusCode);
}
