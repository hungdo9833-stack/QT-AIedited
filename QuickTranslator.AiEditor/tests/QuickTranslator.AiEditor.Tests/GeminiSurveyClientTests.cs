using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using Xunit;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GeminiSurveyClientTests
{
    [Fact]
    public async Task Sends_schema_and_secret_in_header_only_and_reports_tokens()
    {
        string? requestUrl = null;
        string? requestBody = null;
        var handler = new TestHandler(async request =>
        {
            requestUrl = request.RequestUri!.ToString();
            requestBody = await request.Content!.ReadAsStringAsync();
            Assert.Equal("test-secret", request.Headers.GetValues("x-goog-api-key").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"candidates":[{"content":{"parts":[{"text":"{\\\"characters\\\":[]}"}]}}],"usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":25}}""",
                    Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        using var client = new GeminiSurveyClient(http, "test-secret", new(15, 250_000, 500));
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"characters":{"type":"array","items":{"type":"string"}}}}""");
        var response = await client.TranslateStructuredAsync("Đọc và trích xuất", new ModelOptions
        {
            Model = "gemini-3.5-flash-lite"
        }, schema.RootElement);
        Assert.Equal("""{"characters":[]}""", response.Text);
        Assert.Equal(100, response.Metrics.InputTokens);
        Assert.Equal(25, response.Metrics.OutputTokens);
        Assert.Contains("gemini-3.5-flash-lite:generateContent", requestUrl!);
        Assert.DoesNotContain("test-secret", requestUrl!);
        Assert.DoesNotContain("test-secret", requestBody!);
        using JsonDocument requestJson = JsonDocument.Parse(requestBody!);
        Assert.Equal("application/json",
            requestJson.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
        Assert.Equal("object",
            requestJson.RootElement.GetProperty("generationConfig").GetProperty("responseSchema")
                .GetProperty("type").GetString());
    }

    [Fact]
    public async Task Auth_error_does_not_return_secret_or_raw_provider_payload()
    {
        var handler = new TestHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("test-secret api key invalid")
        }));
        using var http = new HttpClient(handler);
        using var client = new GeminiSurveyClient(http, "test-secret", new(15, 250_000, 500));
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.TranslateAsync("Xin chào", new ModelOptions { Model = "gemini-3.5-flash-lite" }));
        Assert.DoesNotContain("test-secret", failure.ToString());
        Assert.Contains("HTTP 401", failure.Message);
    }

    [Fact]
    public async Task Invalid_model_cannot_change_api_host()
    {
        var handler = new TestHandler(_ => throw new InvalidOperationException("Unexpected HTTP request."));
        using var http = new HttpClient(handler);
        using var client = new GeminiSurveyClient(http, "test-secret", new(15, 250_000, 500));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.TranslateAsync("Xin chào", new ModelOptions { Model = "../../another-host" }));
    }

    private sealed class TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}
