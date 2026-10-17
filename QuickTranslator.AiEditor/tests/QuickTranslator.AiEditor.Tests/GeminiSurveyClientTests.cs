using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
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
                    """{"candidates":[{"content":{"parts":[{"text":"{\"characters\":[]}"}]}}],"usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":25,"thoughtsTokenCount":7}}""",
                    Encoding.UTF8, "application/json")
            };
        });
        using var http = new HttpClient(handler);
        using var client = new GeminiSurveyClient(http, "test-secret", new(15, 250_000, 500), Guid.NewGuid());
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","properties":{"characters":{"type":"array","items":{"type":"string"}}}}""");
        var response = await client.TranslateStructuredAsync("Đọc và trích xuất", new ModelOptions
        {
            Model = "gemini-3.5-flash-lite"
        }, schema.RootElement);
        Assert.Equal("""{"characters":[]}""", response.Text);
        Assert.Equal(100, response.Metrics.InputTokens);
        Assert.Equal(32, response.Metrics.OutputTokens);
        Assert.Contains("gemini-3.5-flash-lite:generateContent", requestUrl!);
        Assert.DoesNotContain("test-secret", requestUrl!);
        Assert.DoesNotContain("test-secret", requestBody!);
        using JsonDocument requestJson = JsonDocument.Parse(requestBody!);
        Assert.Equal("application/json",
            requestJson.RootElement.GetProperty("generationConfig").GetProperty("responseMimeType").GetString());
        Assert.Equal("object",
            requestJson.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema")
                .GetProperty("type").GetString());
        Assert.False(requestJson.RootElement.GetProperty("generationConfig").TryGetProperty("responseSchema", out _));
    }

    [Fact]
    public async Task Gemini_36_and_newer_use_low_thinking_without_deprecated_sampling_fields()
    {
        string? requestBody = null;
        using var http = new HttpClient(new TestHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return Success();
        }));
        using var client = new GeminiSurveyClient(http, "test-secret", new(15, 250_000, 500), Guid.NewGuid());

        await client.TranslateAsync("Khảo sát", Options() with { Model = "gemini-3.8-flash" });

        using JsonDocument requestJson = JsonDocument.Parse(requestBody!);
        JsonElement config = requestJson.RootElement.GetProperty("generationConfig");
        Assert.False(config.TryGetProperty("temperature", out _));
        Assert.Equal("low", config.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
    }

    [Fact]
    public async Task Auth_error_does_not_return_secret_or_raw_provider_payload()
    {
        var handler = new TestHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("test-secret api key invalid")
        }));
        using var http = new HttpClient(handler);
        using var client = new GeminiSurveyClient(http, "test-secret", new(15, 250_000, 500), Guid.NewGuid());
        var failure = await Assert.ThrowsAsync<GeminiSurveyHttpException>(
            () => client.TranslateAsync("Xin chào", new ModelOptions { Model = "gemini-3.5-flash-lite" }));
        Assert.DoesNotContain("test-secret", failure.ToString());
        Assert.Contains("HTTP 401", failure.Message);
    }

    [Theory]
    [InlineData("""{"promptFeedback":{"blockReason":"SAFETY"}}""", "SAFETY")]
    [InlineData("""{"promptFeedback":{"blockReason":"PROHIBITED_CONTENT"},"candidates":[]}""", "PROHIBITED_CONTENT")]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""", "SAFETY")]
    public async Task Explicit_provider_block_is_classified_without_retry_or_raw_response_leak(
        string payload, string reason)
    {
        int calls = 0;
        using var http = new HttpClient(new TestHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            });
        }));
        using var client = new GeminiSurveyClient(http, "test-secret", new(15, 250_000, 500), Guid.NewGuid());
        var error = await Assert.ThrowsAsync<GeminiSurveyBlockedException>(
            () => client.TranslateAsync("Trích xuất", Options()));
        Assert.Equal(reason, error.ReasonCode);
        Assert.Equal(1, calls);
        Assert.DoesNotContain("test-secret", error.ToString());
        Assert.DoesNotContain(payload, error.ToString());
    }

    [Fact]
    public async Task Empty_candidates_without_block_reason_are_diagnosed_not_assumed_blocked()
    {
        int calls = 0;
        using var http = new HttpClient(new TestHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"candidates":[]}""",
                    Encoding.UTF8, "application/json")
            });
        }));
        using var client = new GeminiSurveyClient(http, "test-secret", new(15, 250_000, 500), Guid.NewGuid());
        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => client.TranslateAsync("Trích xuất", Options()));
        Assert.IsNotType<GeminiSurveyBlockedException>(error);
        Assert.Contains("không xác nhận bị chặn", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Invalid_model_cannot_change_api_host()
    {
        var handler = new TestHandler(_ => throw new InvalidOperationException("Unexpected HTTP request."));
        using var http = new HttpClient(handler);
        using var client = new GeminiSurveyClient(http, "test-secret", new(15, 250_000, 500), Guid.NewGuid());
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.TranslateAsync("Xin chào", new ModelOptions { Model = "../../another-host" }));
    }

    [Fact]
    public async Task Retries_429_with_retry_after_then_stops_at_a_bounded_attempt_count()
    {
        int calls = 0;
        var delays = new List<TimeSpan>();
        var handler = new TestHandler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3));
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var client = Client(http, new GeminiQuotaCoordinator(), (_, _) =>
        {
            delays.Add(TimeSpan.FromSeconds(3));
            return Task.CompletedTask;
        });

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TranslateAsync("Khảo sát", Options()));

        Assert.Equal(4, calls);
        Assert.Equal(3, delays.Count);
        Assert.Contains("Đã dừng retry", failure.Message);
    }

    [Fact]
    public async Task Hard_request_budget_counts_retries_and_stops_before_the_next_http_attempt()
    {
        int calls = 0;
        using var http = new HttpClient(new TestHandler(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        var clientRequestBudget = new GeminiRequestBudget(2);
        using var client = new GeminiSurveyClient(http, "test-secret", new(20, 250_000, 500),
            Guid.NewGuid(), new GeminiQuotaCoordinator(),
            () => new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero),
            static (_, _) => Task.CompletedTask, requestBudget: clientRequestBudget);

        var failure = await Assert.ThrowsAsync<GeminiRequestBudgetExceededException>(() =>
            client.TranslateAsync("Khảo sát", Options()));

        Assert.Equal(2, calls);
        Assert.Contains("2 request", failure.Message);
        Assert.Equal(2, clientRequestBudget.UsedRequests);
    }

    [Fact]
    public async Task Honors_retry_after_date_before_a_successful_retry()
    {
        DateTimeOffset now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        var delays = new List<TimeSpan>();
        int calls = 0;
        var handler = new TestHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new RetryConditionHeaderValue(now.AddSeconds(7));
                return Task.FromResult(limited);
            }
            return Task.FromResult(Success());
        });
        using var http = new HttpClient(handler);
        using var client = new GeminiSurveyClient(http, "test-secret", new(20, 250_000, 500), Guid.NewGuid(),
            new GeminiQuotaCoordinator(), () => now, (delay, _) =>
            {
                delays.Add(delay); now = now.Add(delay); return Task.CompletedTask;
            });

        OllamaCompletion result = await client.TranslateAsync("Khảo sát", Options());

        Assert.Equal(2, calls);
        Assert.Contains(delays, delay => delay == TimeSpan.FromSeconds(7));
        Assert.Equal("{\"characters\":[],\"relationships\":[],\"glossary\":[]}", result.Text);
    }

    [Fact]
    public async Task Quota_is_shared_by_project_and_model_but_isolated_for_another_project()
    {
        DateTimeOffset now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        var waits = new List<TimeSpan>();
        var quota = new GeminiQuotaCoordinator();
        Guid project = Guid.NewGuid();
        Task Delay(TimeSpan delay, CancellationToken _)
        {
            waits.Add(delay); now = now.Add(delay); return Task.CompletedTask;
        }
        var handler = new TestHandler(_ => Task.FromResult(Success()));
        using var http = new HttpClient(handler);
        using var first = new GeminiSurveyClient(http, "test-secret", new(1, 250_000, 10), project,
            quota, () => now, Delay);
        using var second = new GeminiSurveyClient(http, "test-secret", new(1, 250_000, 10), project,
            quota, () => now, Delay);
        using var isolated = new GeminiSurveyClient(http, "test-secret", new(1, 250_000, 10), Guid.NewGuid(),
            quota, () => now, Delay);

        await first.TranslateAsync("Một", Options());
        await second.TranslateAsync("Hai", Options());
        int waitsAfterSharedProject = waits.Count;
        await isolated.TranslateAsync("Ba", Options());

        Assert.True(waitsAfterSharedProject >= 1);
        Assert.Equal(waitsAfterSharedProject, waits.Count);
    }

    [Fact]
    public async Task Tpm_rejects_an_oversized_batch_before_any_http_request()
    {
        int calls = 0;
        using var http = new HttpClient(new TestHandler(_ =>
        {
            calls++;
            return Task.FromResult(Success());
        }));
        using var client = new GeminiSurveyClient(http, "test-secret", new(100, 10, 100), Guid.NewGuid(),
            new GeminiQuotaCoordinator(), () => DateTimeOffset.UtcNow,
            static (_, _) => Task.CompletedTask);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.TranslateAsync(new string('汉', 100), Options()));

        Assert.Equal(0, calls);
        Assert.Contains("TPM", failure.Message);
    }

    [Fact]
    public async Task Rpd_is_enforced_per_project_and_model()
    {
        DateTimeOffset now = new(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        var quota = new GeminiQuotaCoordinator();
        Guid project = Guid.NewGuid();
        int calls = 0;
        using var http = new HttpClient(new TestHandler(_ =>
        {
            calls++;
            return Task.FromResult(Success());
        }));
        using var limited = new GeminiSurveyClient(http, "test-secret", new(100, 250_000, 1), project,
            quota, () => now, static (_, _) => Task.CompletedTask);
        using var anotherModel = new GeminiSurveyClient(http, "test-secret", new(100, 250_000, 1), project,
            quota, () => now, static (_, _) => Task.CompletedTask);

        await limited.TranslateAsync("Một", Options());
        var failure = await Assert.ThrowsAsync<GeminiDailyQuotaExceededException>(() =>
            limited.TranslateAsync("Hai", Options()));
        await anotherModel.TranslateAsync("Ba", Options() with { Model = "gemini-3.5-flash" });

        Assert.Equal(2, calls);
        Assert.Contains("RPD", failure.Message);
    }

    [Fact]
    public void Rejects_header_injection_in_api_key()
    {
        using var http = new HttpClient(new TestHandler(_ => Task.FromResult(Success())));
        Assert.Throws<ArgumentException>(() => new GeminiSurveyClient(http, "secret\r\nx-evil: yes",
            new(15, 250_000, 500), Guid.NewGuid()));
    }

    private static GeminiSurveyClient Client(HttpClient http, GeminiQuotaCoordinator quota,
        Func<TimeSpan, CancellationToken, Task> delay) =>
        new(http, "test-secret", new(20, 250_000, 500), Guid.NewGuid(), quota,
            () => new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero), delay);

    private static ModelOptions Options() => new()
    {
        Model = "gemini-3.5-flash-lite", RequestTimeout = TimeSpan.FromSeconds(30)
    };

    private static HttpResponseMessage Success() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"characters\\\":[],\\\"relationships\\\":[],\\\"glossary\\\":[]}\"}]},\"finishReason\":\"STOP\"}]}",
            Encoding.UTF8, "application/json")
    };

    private sealed class TestHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }
}
