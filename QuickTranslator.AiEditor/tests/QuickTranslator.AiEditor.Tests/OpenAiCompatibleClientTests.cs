using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class OpenAiCompatibleClientTests
{
    [Fact]
    public async Task Translate_sends_llama_server_shape_and_maps_reply_metrics()
    {
        string? body = null;
        using var http = new HttpClient(new FakeHandler(async (request, cancellationToken) =>
        {
            Assert.Equal("https://gpu.example.com/v1/chat/completions", request.RequestUri!.ToString());
            Assert.Equal("Bearer secret", request.Headers.Authorization?.ToString());
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""
                {"choices":[{"message":{"content":"Bản dịch."},"finish_reason":"length"}],
                 "usage":{"prompt_tokens":123,"completion_tokens":45},
                 "timings":{"prompt_ms":12.5,"predicted_ms":34.25}}
                """);
        })) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OpenAiCompatibleClient(http, new("https://gpu.example.com/api/chat"), 2, "secret");

        OllamaCompletion result = await client.TranslateAsync("private prompt", new ModelOptions
        {
            Model = "alias", Temperature = 0.1, ContextSize = 32768, MaximumOutputTokens = 4096
        });

        using JsonDocument json = JsonDocument.Parse(body!);
        JsonElement root = json.RootElement;
        Assert.Equal("alias", root.GetProperty("model").GetString());
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(0.1, root.GetProperty("temperature").GetDouble(), 5);
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Equal("private prompt", root.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(root.TryGetProperty("num_ctx", out _));
        Assert.False(root.TryGetProperty("options", out _));
        Assert.Equal("Bản dịch.", result.Text);
        Assert.True(result.Done);
        Assert.Equal("length", result.DoneReason);
        Assert.Equal(123, result.Metrics.InputTokens);
        Assert.Equal(45, result.Metrics.OutputTokens);
        Assert.Equal(12_500_000, result.Metrics.PromptEvaluationDurationNanoseconds);
        Assert.Equal(34_250_000, result.Metrics.EvaluationDurationNanoseconds);
    }

    [Fact]
    public async Task Structured_translation_sends_json_schema_response_format()
    {
        string? body = null;
        using var http = new HttpClient(new FakeHandler(async (request, cancellationToken) =>
        {
            body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return JsonResponse("""{"choices":[{"message":{"content":"{}"},"finish_reason":"stop"}]}""");
        }));
        using var client = new OpenAiCompatibleClient(http, new("http://127.0.0.1:11436/api/chat"), 1);
        using JsonDocument schema = JsonDocument.Parse("""{"type":"object","required":["answer"]}""");

        await client.TranslateStructuredAsync("prompt", new(), schema.RootElement);

        using JsonDocument json = JsonDocument.Parse(body!);
        JsonElement format = json.RootElement.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("reply", format.GetProperty("json_schema").GetProperty("name").GetString());
        Assert.Equal("object", format.GetProperty("json_schema").GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Health_and_models_are_checked_without_requiring_profile_name_match()
    {
        var paths = new List<string>();
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(request.RequestUri.AbsolutePath == "/health"
                ? JsonResponse("""{"status":"ok"}""")
                : JsonResponse("""{"models":[{"name":"served-alias","model":"C:\\models\\book.gguf"}],"data":[{"id":"served-alias"}]}"""));
        }));
        using var client = new OpenAiCompatibleClient(http, new("http://10.0.0.5:11436/api/chat"), 1);

        await client.CheckConnectionAsync("different-profile-name", TimeSpan.FromSeconds(1));
        IReadOnlyList<string> models = await client.ListModelsAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(["/health", "/v1/models", "/v1/models"], paths);
        Assert.Equal(["served-alias"], models);
    }

    [Fact]
    public async Task Http_error_is_reported_as_ollama_request_exception()
    {
        using var http = new HttpClient(new FakeHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway))));
        using var client = new OpenAiCompatibleClient(http, new("https://gpu.example.com"), 1);

        OllamaRequestException error = await Assert.ThrowsAsync<OllamaRequestException>(() =>
            client.TranslateAsync("prompt", new()));
        Assert.Equal(OllamaErrorKind.Http, error.Kind);
        Assert.Contains("llama-server", error.Message);
    }

    [Fact]
    public async Task Timeout_is_distinct_from_user_cancellation()
    {
        using var http = new HttpClient(new FakeHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        })) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OpenAiCompatibleClient(http, new("https://gpu.example.com"), 1);

        OllamaRequestException timeout = await Assert.ThrowsAsync<OllamaRequestException>(() =>
            client.TranslateAsync("prompt", new() { RequestTimeout = TimeSpan.FromMilliseconds(50) }));
        Assert.Equal(OllamaErrorKind.Timeout, timeout.Kind);

        using var cancellation = new CancellationTokenSource();
        Task<OllamaCompletion> pending = client.TranslateAsync("prompt", new(), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public async Task Parallel_request_limit_controls_real_http_overlap(int parallelRequests, int expectedMaximum)
    {
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0, maximum = 0, calls = 0;
        using var http = new HttpClient(new FakeHandler(async (_, cancellationToken) =>
        {
            int current = Interlocked.Increment(ref active);
            Interlocked.Increment(ref calls);
            UpdateMaximum(ref maximum, current);
            if (Volatile.Read(ref calls) >= expectedMaximum) allStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            Interlocked.Decrement(ref active);
            return JsonResponse("""{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}]}""");
        })) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OpenAiCompatibleClient(http, new("https://gpu.example.com"), parallelRequests);

        Task<OllamaCompletion>[] requests = Enumerable.Range(0, 3)
            .Select(index => client.TranslateAsync("prompt " + index, new())).ToArray();
        await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(expectedMaximum, Volatile.Read(ref calls));
        release.SetResult();
        await Task.WhenAll(requests);
        Assert.Equal(expectedMaximum, maximum);
        Assert.Equal(3, calls);
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        int current;
        do
        {
            current = Volatile.Read(ref maximum);
            if (current >= candidate) return;
        } while (Interlocked.CompareExchange(ref maximum, candidate, current) != current);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}
