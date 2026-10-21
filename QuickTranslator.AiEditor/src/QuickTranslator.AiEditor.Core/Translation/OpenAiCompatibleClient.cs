using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class OpenAiCompatibleClient : IOllamaBackendClient, IJsonSchemaOllamaClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _chatEndpoint;
    private readonly Uri _modelsEndpoint;
    private readonly Uri _healthEndpoint;
    private readonly string? _bearerToken;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _requestGate;

    public OpenAiCompatibleClient(HttpClient httpClient, Uri endpoint, int parallelRequests,
        string? bearerToken = null, bool ownsHttpClient = false)
    {
        if (parallelRequests is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(parallelRequests));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Uri normalized = OllamaClient.ValidateRemoteEndpoint(endpoint);
        _chatEndpoint = WithPath(normalized, "/v1/chat/completions");
        _modelsEndpoint = WithPath(normalized, "/v1/models");
        _healthEndpoint = WithPath(normalized, "/health");
        if (bearerToken?.Any(char.IsControl) == true)
            throw new ArgumentException("Token chứa ký tự không hợp lệ.", nameof(bearerToken));
        _bearerToken = string.IsNullOrEmpty(bearerToken) ? null : bearerToken;
        _ownsHttpClient = ownsHttpClient;
        _requestGate = new SemaphoreSlim(parallelRequests, parallelRequests);
    }

    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default) => SendAsync(prompt, options, null, cancellationToken);

    public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
        JsonElement responseSchema, CancellationToken cancellationToken = default)
    {
        if (responseSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("JSON Schema của llama-server phải là object.", nameof(responseSchema));
        return SendAsync(prompt, options, responseSchema.Clone(), cancellationToken);
    }

    private async Task<OllamaCompletion> SendAsync(string prompt, ModelOptions options,
        JsonElement? responseSchema, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        options.Validate();
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.RequestTimeout);
            var stopwatch = Stopwatch.StartNew();
            try
            {
                object body = responseSchema is JsonElement schema
                    ? new
                    {
                        model = options.Model, stream = false, temperature = options.Temperature,
                        max_tokens = options.MaximumOutputTokens,
                        chat_template_kwargs = new { enable_thinking = false },
                        messages = new[] { new { role = "user", content = prompt } },
                        response_format = new { type = "json_schema", json_schema = new { name = "reply", schema } }
                    }
                    : new
                    {
                        model = options.Model, stream = false, temperature = options.Temperature,
                        max_tokens = options.MaximumOutputTokens,
                        chat_template_kwargs = new { enable_thinking = false },
                        messages = new[] { new { role = "user", content = prompt } }
                    };
                using var request = new HttpRequestMessage(HttpMethod.Post, _chatEndpoint)
                { Content = JsonContent.Create(body) };
                AddAuthorization(request);
                using HttpResponseMessage response = await _httpClient.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new OllamaRequestException(OllamaErrorKind.Http,
                        $"HTTP {(int)response.StatusCode}: llama-server không xử lý được yêu cầu. Kiểm tra server, model và bộ nhớ.");
                ChatResponse? reply = await response.Content.ReadFromJsonAsync<ChatResponse>(
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                Choice? choice = reply?.Choices?.FirstOrDefault();
                if (choice?.Message is null)
                    throw new OllamaRequestException(OllamaErrorKind.InvalidResponse,
                        "Phản hồi llama-server thiếu choices[0].message; bản nháp hiện có vẫn được giữ.");
                return new OllamaCompletion(choice.Message.Content ?? "", true, choice.FinishReason, new GenerationMetrics
                {
                    InputTokens = reply?.Usage?.PromptTokens,
                    OutputTokens = reply?.Usage?.CompletionTokens,
                    PromptEvaluationDurationNanoseconds = MillisecondsToNanoseconds(reply?.Timings?.PromptMilliseconds),
                    EvaluationDurationNanoseconds = MillisecondsToNanoseconds(reply?.Timings?.PredictedMilliseconds),
                    ClientElapsed = stopwatch.Elapsed
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Timeout,
                    $"llama-server quá thời gian chờ ({options.RequestTimeout.TotalSeconds:0} giây). Có thể tăng timeout hoặc giảm kích thước đoạn.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Connection,
                    "Không kết nối được llama-server. Kiểm tra URL, TLS, firewall, SSH tunnel và trạng thái máy GPU; không tự chuyển provider.", ex);
            }
            catch (JsonException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.InvalidResponse,
                    "llama-server trả dữ liệu sai định dạng JSON.", ex);
            }
        }
        finally { _requestGate.Release(); }
    }

    public async Task CheckConnectionAsync(string model, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try
            {
                await EnsureSuccessAsync(_healthEndpoint, "/health", linked.Token).ConfigureAwait(false);
                IReadOnlyList<string> found = await ReadModelsAsync(linked.Token).ConfigureAwait(false);
                if (found.Count == 0)
                    throw new OllamaRequestException(OllamaErrorKind.InvalidResponse,
                        $"llama-server phản hồi nhưng /v1/models không cho biết model nào (profile đang chọn: {model.Trim()}).");
                // llama-server commonly exposes a GGUF path or alias, so a name mismatch is intentionally accepted.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            { throw new OllamaRequestException(OllamaErrorKind.Timeout, "llama-server không phản hồi kiểm tra kết nối đúng thời hạn.", ex); }
            catch (HttpRequestException ex)
            { throw new OllamaRequestException(OllamaErrorKind.Connection, "Không kết nối được llama-server để kiểm tra /health và /v1/models.", ex); }
            catch (JsonException ex)
            { throw new OllamaRequestException(OllamaErrorKind.InvalidResponse, "llama-server trả danh sách model sai định dạng JSON.", ex); }
        }
        finally { _requestGate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try { return await ReadModelsAsync(linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            { throw new OllamaRequestException(OllamaErrorKind.Timeout, "llama-server không trả danh sách model đúng thời hạn.", ex); }
            catch (HttpRequestException ex)
            { throw new OllamaRequestException(OllamaErrorKind.Connection, "Không kết nối được llama-server để lấy danh sách model.", ex); }
            catch (JsonException ex)
            { throw new OllamaRequestException(OllamaErrorKind.InvalidResponse, "llama-server trả danh sách model sai định dạng JSON.", ex); }
        }
        finally { _requestGate.Release(); }
    }

    private async Task<IReadOnlyList<string>> ReadModelsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _modelsEndpoint);
        AddAuthorization(request);
        using HttpResponseMessage response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OllamaRequestException(OllamaErrorKind.Http,
                $"HTTP {(int)response.StatusCode}: llama-server không trả được /v1/models.");
        ModelList? list = await response.Content.ReadFromJsonAsync<ModelList>(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (list is null || list.Models is null && list.Data is null)
            throw new OllamaRequestException(OllamaErrorKind.InvalidResponse,
                "llama-server không trả danh sách model hợp lệ qua /v1/models.");
        return (list.Models ?? []).Select(item => item.Name ?? item.Model)
            .Concat((list.Data ?? []).Select(item => item.Id))
            .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private async Task EnsureSuccessAsync(Uri endpoint, string name, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        AddAuthorization(request);
        using HttpResponseMessage response = await _httpClient.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OllamaRequestException(OllamaErrorKind.Http,
                $"HTTP {(int)response.StatusCode}: llama-server không sẵn sàng tại {name}.");
    }

    private void AddAuthorization(HttpRequestMessage request)
    {
        if (_bearerToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _bearerToken);
    }

    private static Uri WithPath(Uri endpoint, string path) => new UriBuilder(endpoint)
    { Path = path, Query = "", Fragment = "" }.Uri;

    private static long? MillisecondsToNanoseconds(double? milliseconds) => milliseconds is null
        ? null : checked((long)(milliseconds.Value * 1_000_000d));

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
        _requestGate.Dispose();
    }

    private sealed class ChatResponse
    {
        public List<Choice>? Choices { get; set; }
        public UsageInfo? Usage { get; set; }
        public TimingInfo? Timings { get; set; }
    }
    private sealed class Choice
    {
        public ChatMessage? Message { get; set; }
        [JsonPropertyName("finish_reason")] public string? FinishReason { get; set; }
    }
    private sealed class ChatMessage { public string? Content { get; set; } }
    private sealed class UsageInfo
    {
        [JsonPropertyName("prompt_tokens")] public int? PromptTokens { get; set; }
        [JsonPropertyName("completion_tokens")] public int? CompletionTokens { get; set; }
    }
    private sealed class TimingInfo
    {
        [JsonPropertyName("prompt_ms")] public double? PromptMilliseconds { get; set; }
        [JsonPropertyName("predicted_ms")] public double? PredictedMilliseconds { get; set; }
    }
    private sealed class ModelList
    {
        public List<ModelEntry>? Models { get; set; }
        public List<ModelData>? Data { get; set; }
    }
    private sealed class ModelEntry { public string? Name { get; set; } public string? Model { get; set; } }
    private sealed class ModelData { public string? Id { get; set; } }
}
