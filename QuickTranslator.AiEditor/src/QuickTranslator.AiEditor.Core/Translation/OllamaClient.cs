using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class OllamaClient : IOllamaClient, IDisposable
{
    public static Uri DefaultEndpoint { get; } = new("http://127.0.0.1:11434/api/chat");
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly bool _ownsHttpClient;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    public OllamaClient(HttpClient httpClient, Uri? endpoint = null, bool ownsHttpClient = false)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _endpoint = ValidateLocalEndpoint(endpoint ?? DefaultEndpoint);
        _ownsHttpClient = ownsHttpClient;
    }

    public static OllamaClient CreateLocal()
    {
        // Local traffic must never be sent to a system proxy or redirected to a remote server.
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        return new(new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan }, ownsHttpClient: true);
    }

    public static Uri ValidateLocalEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != "http" || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.AbsolutePath != "/api/chat")
            throw new ArgumentException("Ollama phải dùng HTTP localhost và đường dẫn /api/chat.");
        string host = endpoint.Host.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return new UriBuilder(endpoint) { Host = "127.0.0.1" }.Uri;
        if (!IPAddress.TryParse(host, out var address) || !IPAddress.IsLoopback(address))
            throw new ArgumentException("Chỉ cho phép Ollama trên máy này (127.0.0.1 hoặc ::1); không gửi chương lên cloud.");
        return endpoint;
    }

    public async Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
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
                // A localhost server can proxy cloud models. Check metadata before sending any chapter text.
                await VerifyInstalledLocalModelAsync(options.Model, timeout.Token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
                {
                    Content = JsonContent.Create(new
                    {
                        model = options.Model,
                        messages = new[] { new { role = "user", content = prompt } },
                        stream = false,
                        options = new
                        {
                            temperature = options.Temperature,
                            num_ctx = options.ContextSize,
                            num_predict = options.MaximumOutputTokens
                        }
                    })
                };
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    string detail = response.StatusCode == HttpStatusCode.NotFound
                        ? "Không tìm thấy model hoặc API. Kiểm tra tên model bằng ollama list."
                        : "Ollama không xử lý được yêu cầu. Kiểm tra Ollama và mức sử dụng bộ nhớ.";
                    throw new OllamaRequestException(OllamaErrorKind.Http, $"HTTP {(int)response.StatusCode}: {detail}");
                }
                var body = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: timeout.Token).ConfigureAwait(false);
                if (body is null || body.Message is null || body.Error is not null)
                    throw new OllamaRequestException(OllamaErrorKind.InvalidResponse, "Phản hồi Ollama thiếu message hoặc có lỗi; bản nháp hiện có vẫn được giữ.");
                return new(body.Message.Content ?? "", body.Done, body.DoneReason, new()
                {
                    InputTokens = body.PromptEvalCount,
                    OutputTokens = body.EvalCount,
                    TotalDurationNanoseconds = body.TotalDuration,
                    LoadDurationNanoseconds = body.LoadDuration,
                    PromptEvaluationDurationNanoseconds = body.PromptEvalDuration,
                    EvaluationDurationNanoseconds = body.EvalDuration,
                    ClientElapsed = stopwatch.Elapsed
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Timeout,
                    $"Ollama quá thời gian chờ ({options.RequestTimeout.TotalSeconds:0} giây). Có thể tăng timeout hoặc giảm kích thước đoạn/context.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Connection,
                    "Không kết nối được Ollama tại localhost:11434. Hãy mở Ollama hoặc chạy ollama serve; không có dịch vụ cloud thay thế.", ex);
            }
            catch (JsonException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.InvalidResponse, "Ollama trả dữ liệu sai định dạng JSON.", ex);
            }
        }
        finally { _requestGate.Release(); }
    }

    public async Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linked.CancelAfter(timeout);
            try { await VerifyInstalledLocalModelAsync(model.Trim(), linked.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Timeout, "Ollama không phản hồi kiểm tra kết nối đúng thời hạn.", ex);
            }
            catch (HttpRequestException ex)
            {
                throw new OllamaRequestException(OllamaErrorKind.Connection,
                    "Không kết nối được Ollama tại localhost:11434. Hãy mở Ollama hoặc chạy ollama serve.", ex);
            }
        }
        finally { _requestGate.Release(); }
    }

    private async Task VerifyInstalledLocalModelAsync(string model, CancellationToken cancellationToken)
    {
        var tagsEndpoint = new UriBuilder(_endpoint) { Path = "/api/tags" }.Uri;
        using var response = await _httpClient.GetAsync(tagsEndpoint, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new OllamaRequestException(OllamaErrorKind.Http,
                $"HTTP {(int)response.StatusCode}: không kiểm tra được model cục bộ qua /api/tags; chưa gửi nguyên văn.");
        var tags = await response.Content.ReadFromJsonAsync<ModelList>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (tags?.Models is null)
            throw new OllamaRequestException(OllamaErrorKind.InvalidResponse, "Ollama không trả danh sách model hợp lệ; chưa gửi nguyên văn.");
        var installed = tags.Models.FirstOrDefault(m => m.Name == model || m.Model == model);
        if (installed is null)
            throw new OllamaRequestException(OllamaErrorKind.Http, "Model chưa được cài trên máy này. Kiểm tra đúng tên đầy đủ bằng ollama list; chưa gửi nguyên văn.");
        if (!string.IsNullOrWhiteSpace(installed.RemoteHost) || !string.IsNullOrWhiteSpace(installed.RemoteModel) ||
            installed.Size is null or <= 0 || model.Contains("cloud", StringComparison.OrdinalIgnoreCase))
            throw new OllamaRequestException(OllamaErrorKind.InvalidResponse,
                "Chỉ dùng model có dữ liệu cục bộ; model cloud/từ xa hoặc thiếu thông tin bị từ chối trước khi gửi nguyên văn. Hãy chọn translategemma:12b và tắt cloud trong Ollama.");
    }

    public void Dispose()
    {
        if (_ownsHttpClient) _httpClient.Dispose();
        _requestGate.Dispose();
    }

    private sealed class ChatResponse
    {
        public ChatMessage? Message { get; set; }
        public bool? Done { get; set; }
        public string? Error { get; set; }
        [JsonPropertyName("done_reason")] public string? DoneReason { get; set; }
        [JsonPropertyName("prompt_eval_count")] public int? PromptEvalCount { get; set; }
        [JsonPropertyName("eval_count")] public int? EvalCount { get; set; }
        [JsonPropertyName("total_duration")] public long? TotalDuration { get; set; }
        [JsonPropertyName("load_duration")] public long? LoadDuration { get; set; }
        [JsonPropertyName("prompt_eval_duration")] public long? PromptEvalDuration { get; set; }
        [JsonPropertyName("eval_duration")] public long? EvalDuration { get; set; }
    }
    private sealed class ChatMessage { public string? Content { get; set; } }
    private sealed class ModelList { public List<InstalledModel>? Models { get; set; } }
    private sealed class InstalledModel
    {
        public string? Name { get; set; }
        public string? Model { get; set; }
        public long? Size { get; set; }
        [JsonPropertyName("remote_host")] public string? RemoteHost { get; set; }
        [JsonPropertyName("remote_model")] public string? RemoteModel { get; set; }
    }
}
