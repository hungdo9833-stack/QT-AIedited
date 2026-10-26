using System.Net;
using System.Net.Http.Headers;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GeminiTranslationClientTests
{
    [Fact]
    public async Task Rotates_preferred_then_fallback_then_next_key()
    {
        var routes = new List<(string Key, string Model)>();
        using var http = new HttpClient(new TestHandler(request =>
        {
            routes.Add(Route(request));
            return Task.FromResult(routes.Count <= 2 ? Limited(TimeSpan.FromMinutes(1)) : Success("bản dịch"));
        }));
        using var client = new GeminiTranslationClient(http,
            ["AIza-rotation-key-one-1111", "AIza-rotation-key-two-2222"]);

        LlmCompletion result = await client.TranslateAsync("dịch", Options());

        Assert.Equal("bản dịch", result.Text);
        Assert.Equal([
            ("AIza-rotation-key-one-1111", GeminiTranslationClient.PreferredModel),
            ("AIza-rotation-key-one-1111", GeminiTranslationClient.FallbackModel),
            ("AIza-rotation-key-two-2222", GeminiTranslationClient.PreferredModel)], routes);
        Assert.Equal(1, client.CurrentRoute.KeyIndex);
    }

    [Fact]
    public async Task Per_minute_retry_after_expiry_restores_the_preferred_route()
    {
        DateTimeOffset now = new(2026, 10, 26, 1, 0, 0, TimeSpan.Zero);
        var routes = new List<(string Key, string Model)>();
        using var http = new HttpClient(new TestHandler(request =>
        {
            routes.Add(Route(request));
            return Task.FromResult(routes.Count == 1 ? Limited(TimeSpan.FromSeconds(5)) : Success("ok"));
        }));
        using var client = new GeminiTranslationClient(http, ["AIza-retry-key-1111"], () => now);

        await client.TranslateAsync("một", Options());
        Assert.Equal(GeminiTranslationClient.FallbackModel, client.CurrentRoute.Model);
        now = now.AddSeconds(6);
        await client.TranslateAsync("hai", Options());

        Assert.Equal(GeminiTranslationClient.PreferredModel, routes[^1].Model);
        Assert.Equal(GeminiTranslationClient.PreferredModel, client.CurrentRoute.Model);
    }

    [Fact]
    public async Task All_daily_exhausted_routes_stop_with_a_clear_safe_error()
    {
        int calls = 0;
        using var http = new HttpClient(new TestHandler(_ =>
        {
            calls++;
            return Task.FromResult(LimitedDaily());
        }));
        using var client = new GeminiTranslationClient(http,
            ["AIza-daily-key-one-1111", "AIza-daily-key-two-2222"]);

        GeminiTranslationQuotaException error = await Assert.ThrowsAsync<GeminiTranslationQuotaException>(
            () => client.TranslateAsync("dịch", Options()));

        Assert.Equal(4, calls);
        Assert.Contains("Tất cả Gemini API key", error.Message, StringComparison.Ordinal);
        Assert.Contains("dừng an toàn", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AIza-", error.ToString(), StringComparison.Ordinal);
    }

    private static ModelOptions Options() => new()
    {
        Model = "ignored-by-router", RequestTimeout = TimeSpan.FromSeconds(30), MaximumOutputTokens = 256
    };

    private static (string Key, string Model) Route(HttpRequestMessage request)
    {
        string key = request.Headers.GetValues("x-goog-api-key").Single();
        string path = request.RequestUri!.AbsolutePath;
        string model = path.Split("/models/", StringSplitOptions.None)[1].Split(':')[0];
        return (key, model);
    }

    private static HttpResponseMessage Limited(TimeSpan retryAfter)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"PerMinute\"}}")
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(retryAfter);
        return response;
    }

    private static HttpResponseMessage LimitedDaily() => new(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent(
            "{\"error\":{\"status\":\"RESOURCE_EXHAUSTED\",\"details\":[{\"quotaId\":\"GenerateRequestsPerDayPerProjectPerModel-FreeTier\"}]}}",
            Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Success(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + text +
            "\"}]},\"finishReason\":\"STOP\"}]}", Encoding.UTF8, "application/json")
    };

    private sealed class TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}
