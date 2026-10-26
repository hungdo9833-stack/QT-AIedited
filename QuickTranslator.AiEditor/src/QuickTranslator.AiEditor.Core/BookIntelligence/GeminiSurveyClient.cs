using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public enum GeminiQuotaExhaustionKind { Unknown, PerMinute, Daily }

public sealed class GeminiSurveyHttpException(HttpStatusCode statusCode, TimeSpan? retryAfter = null,
    GeminiQuotaExhaustionKind quotaKind = GeminiQuotaExhaustionKind.Unknown)
    : InvalidOperationException($"Gemini API trả HTTP {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public GeminiQuotaExhaustionKind QuotaKind { get; } = quotaKind;
}

/// <summary>
/// A provider-reported prompt/candidate block. Never retry the same content
/// automatically or pass it to another model to evade the provider decision.
/// </summary>
public sealed class GeminiSurveyBlockedException(string reasonCode)
    : Exception("Gemini đã từ chối batch khảo sát (" + reasonCode +
        "). Đã giữ nguyên chứng cứ để kiểm tra thủ công; không tự gửi lại nội dung bị chặn.")
{
    public string ReasonCode { get; } = reasonCode;
}

public sealed class GeminiDailyQuotaExceededException()
    : InvalidOperationException("Đã chạm giới hạn RPD Gemini của Project/model. Chờ reset lúc 00:00 giờ Pacific.");

public sealed class GeminiRequestBudgetExceededException(int maximumRequests)
    : InvalidOperationException($"Đã dùng hết ngân sách {maximumRequests} request Gemini cho phiên khảo sát. Batch còn lại chưa được gửi và có thể Resume sau.");

/// <summary>Configurable API limits. These apply to a Google AI project and model, not an API key.</summary>
public sealed record GeminiSurveyLimits(int RequestsPerMinute, int InputTokensPerMinute, int RequestsPerDay,
    int MaxInFlight = 2)
{
    public static GeminiSurveyLimits FromObservedFreeTier(string model) =>
        model.Contains("flash-lite", StringComparison.OrdinalIgnoreCase)
            ? new(15, 250_000, 500, 2)
            : new(5, 250_000, 20, 2);

    public void Validate()
    {
        if (RequestsPerMinute < 1 || InputTokensPerMinute < 1 || RequestsPerDay < 1 ||
            MaxInFlight is < 1 or > 16)
            throw new ArgumentOutOfRangeException(nameof(GeminiSurveyLimits), "Hạn mức Gemini không hợp lệ.");
    }
}

/// <summary>
/// Gemini API adapter for Book Intelligence only. Existing evidence validation and
/// Project persistence remain owned by AdaptiveBookIntelligenceAnalyzer.
/// The API key is never serialized, logged, read from a response, or included in request URLs.
/// </summary>
public sealed class GeminiSurveyClient : IJsonSchemaLlmClient, IDisposable
{
    private const int MaximumAttempts = 4;
    private static readonly Regex ModelName = new(@"^gemini-[a-zA-Z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly GeminiQuotaCoordinator SharedQuota = new();
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly Guid _projectId;
    private readonly GeminiSurveyLimits _limits;
    private readonly SemaphoreSlim _inFlight;
    private readonly bool _routeFailuresToPool;
    private readonly GeminiQuotaCoordinator _quota;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly GeminiRequestBudget? _requestBudget;

    public int MaxConcurrency => _limits.MaxInFlight;

    public GeminiSurveyClient(HttpClient httpClient, string apiKey, GeminiSurveyLimits limits, Guid projectId,
        bool routeFailuresToPool = false)
        : this(httpClient, apiKey, limits, projectId, SharedQuota, () => DateTimeOffset.UtcNow,
            static (delay, token) => Task.Delay(delay, token), routeFailuresToPool) { }

    internal GeminiSurveyClient(HttpClient httpClient, string apiKey, GeminiSurveyLimits limits, Guid projectId,
        bool routeFailuresToPool, GeminiRequestBudget? requestBudget)
        : this(httpClient, apiKey, limits, projectId, SharedQuota, () => DateTimeOffset.UtcNow,
            static (delay, token) => Task.Delay(delay, token), routeFailuresToPool, requestBudget) { }

    internal GeminiSurveyClient(HttpClient httpClient, string apiKey, GeminiSurveyLimits limits, Guid projectId,
        GeminiQuotaCoordinator quota, Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay, bool routeFailuresToPool = false,
        GeminiRequestBudget? requestBudget = null)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        if (projectId == Guid.Empty) throw new ArgumentException("Project Gemini không hợp lệ.", nameof(projectId));
        string secret = apiKey?.Trim() ?? "";
        if (secret.Length == 0 || secret.Contains('\r') || secret.Contains('\n'))
            throw new ArgumentException("Gemini API key không hợp lệ.", nameof(apiKey));
        _apiKey = secret;
        _projectId = projectId;
        _limits = limits;
        _routeFailuresToPool = routeFailuresToPool;
        _quota = quota ?? throw new ArgumentNullException(nameof(quota));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
        _requestBudget = requestBudget;
        _inFlight = new SemaphoreSlim(_limits.MaxInFlight, _limits.MaxInFlight);
    }

    public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default) => GenerateAsync(prompt, options, null, cancellationToken);

    public Task<LlmCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
        JsonElement responseSchema, CancellationToken cancellationToken = default)
    {
        if (responseSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Gemini JSON Schema phải là object.", nameof(responseSchema));
        return GenerateAsync(prompt, options, responseSchema.Clone(), cancellationToken);
    }

    private async Task<LlmCompletion> GenerateAsync(string prompt, ModelOptions options,
        JsonElement? schema, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        options.Validate();
        if (!ModelName.IsMatch(options.Model))
            throw new ArgumentException("Model Gemini phải là ID hợp lệ (ví dụ gemini-3.5-flash-lite).");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(options.RequestTimeout);
        CancellationToken token = linked.Token;
        await _inFlight.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            int estimatedInputTokens = checked(PromptTokenBudget.Estimate(prompt) + 32);
            for (int attempt = 0; attempt < MaximumAttempts; attempt++)
            {
                await _quota.AcquireAsync(_projectId, options.Model, estimatedInputTokens, _limits,
                    _utcNow, _delay, token).ConfigureAwait(false);
                using var request = CreateRequest(prompt, options, schema);
                _requestBudget?.Reserve(options.Model);
                HttpResponseMessage response;
                try
                {
                    response = await _http.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    if (attempt == MaximumAttempts - 1)
                        throw new InvalidOperationException(
                            $"Không kết nối được Gemini API sau {MaximumAttempts} lần thử. Đã dừng retry.", ex);
                    await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), token).ConfigureAwait(false);
                    continue;
                }
                using (response)
                {
                if (_routeFailuresToPool && response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    string quotaPayload = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                    GeminiQuotaExhaustionKind quotaKind = ClassifyQuotaExhaustion(quotaPayload,
                        response.Headers.RetryAfter is not null);
                    TimeSpan? retryAfter = quotaKind == GeminiQuotaExhaustionKind.Daily
                        ? null : RetryDelay(response, attempt, _utcNow());
                    throw new GeminiSurveyHttpException(response.StatusCode, retryAfter, quotaKind);
                }
                if (IsTransient(response.StatusCode))
                {
                    if (attempt == MaximumAttempts - 1)
                        throw new InvalidOperationException(
                            $"Gemini API trả HTTP {(int)response.StatusCode} sau {MaximumAttempts} lần thử. Đã dừng retry.");
                    await _delay(RetryDelay(response, attempt, _utcNow()), token).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new GeminiSurveyHttpException(response.StatusCode);

                string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                try
                {
                    using JsonDocument doc = JsonDocument.Parse(json);
                    JsonElement root = doc.RootElement;
                    string? promptBlock = ProviderCode(root, "promptFeedback", "blockReason");
                    if (!root.TryGetProperty("candidates", out JsonElement candidates) ||
                        candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                    {
                        if (IsExplicitBlock(promptBlock))
                            throw new GeminiSurveyBlockedException(promptBlock!);
                        throw new InvalidDataException(
                            "Gemini trả HTTP 200 nhưng không có candidate và không xác nhận bị chặn " +
                            "(promptFeedback.blockReason=" + (promptBlock ?? "không có") + "). " +
                            "Giữ checkpoint; kiểm tra log phản hồi đã ẩn dữ liệu nhạy cảm.");
                    }
                    JsonElement candidate = candidates[0];
                    if (candidate.TryGetProperty("finishReason", out JsonElement finish) &&
                        finish.ValueKind == JsonValueKind.String &&
                        !string.Equals(finish.GetString(), "STOP", StringComparison.OrdinalIgnoreCase))
                    {
                        string code = SafeProviderCode(finish.GetString());
                        if (IsExplicitBlock(code))
                            throw new GeminiSurveyBlockedException(code);
                        throw new InvalidDataException("Gemini dừng khảo sát với finishReason=" +
                            code + ". Batch chưa hoàn thành vẫn được giữ để tiếp tục.");
                    }
                    if (!candidate.TryGetProperty("content", out JsonElement content) ||
                        !content.TryGetProperty("parts", out JsonElement parts) ||
                        parts.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("Gemini không trả nội dung khảo sát; prompt có thể đã bị chặn.");
                    string result = string.Concat(parts.EnumerateArray()
                        .Where(p => p.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                        .Select(p => p.GetProperty("text").GetString()));
                    if (string.IsNullOrWhiteSpace(result))
                        throw new InvalidDataException("Gemini trả kết quả khảo sát rỗng.");
                    stopwatch.Stop();
                    int? input = null, output = null;
                    if (root.TryGetProperty("usageMetadata", out JsonElement usage))
                    {
                        if (usage.TryGetProperty("promptTokenCount", out JsonElement a) && a.TryGetInt32(out int n)) input = n;
                        if (usage.TryGetProperty("candidatesTokenCount", out JsonElement b) && b.TryGetInt32(out int m)) output = m;
                        if (usage.TryGetProperty("thoughtsTokenCount", out JsonElement thoughts) &&
                            thoughts.TryGetInt32(out int thinking)) output = (output ?? 0) + thinking;
                    }
                    return new LlmCompletion(result, true, "stop",
                        new GenerationMetrics { InputTokens = input, OutputTokens = output, ClientElapsed = stopwatch.Elapsed });
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Gemini trả response JSON không hợp lệ.", ex);
                }
                catch (InvalidOperationException ex)
                {
                    throw new InvalidDataException("Gemini trả envelope JSON sai cấu trúc.", ex);
                }
                }
            }
            throw new InvalidOperationException("Gemini API không hoàn tất khảo sát.");
        }
        finally { _inFlight.Release(); }
    }

    private static string? ProviderCode(JsonElement root, string objectKey, string codeKey)
    {
        if (!root.TryGetProperty(objectKey, out JsonElement obj) ||
            obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty(codeKey, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String)
            return null;
        return SafeProviderCode(value.GetString());
    }

    private static string SafeProviderCode(string? value) => value?.ToUpperInvariant() switch
    {
        "SAFETY" => "SAFETY",
        "PROHIBITED_CONTENT" => "PROHIBITED_CONTENT",
        "BLOCKLIST" => "BLOCKLIST",
        "SPII" => "SPII",
        "MODEL_ARMOR" => "MODEL_ARMOR",
        "OTHER" => "OTHER",
        "RECITATION" => "RECITATION",
        "MAX_TOKENS" => "MAX_TOKENS",
        "BLOCK_REASON_UNSPECIFIED" => "BLOCK_REASON_UNSPECIFIED",
        "STOP" => "STOP",
        _ => "UNKNOWN"
    };

    private static bool IsExplicitBlock(string? value) => value is
        "SAFETY" or "PROHIBITED_CONTENT" or "BLOCKLIST" or "SPII" or
        "MODEL_ARMOR" or "OTHER" or "RECITATION";

    private HttpRequestMessage CreateRequest(string prompt, ModelOptions options, JsonElement? schema)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://generativelanguage.googleapis.com/v1beta/models/" + options.Model + ":generateContent");
        request.Headers.Add("x-goog-api-key", _apiKey);
        var generationConfig = new Dictionary<string, object?>
        {
            ["maxOutputTokens"] = options.MaximumOutputTokens
        };
        if (UsesLegacySampling(options.Model)) generationConfig["temperature"] = options.Temperature;
        else generationConfig["thinkingConfig"] = new Dictionary<string, string>
        {
            ["thinkingLevel"] = "low"
        };
        if (schema is JsonElement responseSchema)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseJsonSchema"] = responseSchema;
        }
        // Gemini 2.5+/3 generally defaults these adjustable filters to OFF.
        // Keep the official per-request setting explicit for QT Studio surveys.
        // This does NOT change Google's non-configurable core protections.
        string[] adjustableCategories =
        [
            "HARM_CATEGORY_HARASSMENT",
            "HARM_CATEGORY_HATE_SPEECH",
            "HARM_CATEGORY_SEXUALLY_EXPLICIT",
            "HARM_CATEGORY_DANGEROUS_CONTENT"
        ];
        var safetySettings = adjustableCategories.Select(category =>
            new { category, threshold = "OFF" }).ToArray();
        string body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig,
            safetySettings
        });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static bool UsesLegacySampling(string model)
    {
        string versionText = model["gemini-".Length..].Split('-', 2)[0];
        return Version.TryParse(versionText, out Version? version) && version < new Version(3, 6);
    }

    private static bool IsTransient(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests ||
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static TimeSpan RetryDelay(HttpResponseMessage response, int attempt, DateTimeOffset now)
    {
        TimeSpan delay = response.Headers.RetryAfter?.Delta ??
            (response.Headers.RetryAfter?.Date is DateTimeOffset retryAt ? retryAt - now : TimeSpan.Zero);
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
        return TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 100, TimeSpan.FromMinutes(2).TotalMilliseconds));
    }

    private static GeminiQuotaExhaustionKind ClassifyQuotaExhaustion(string payload, bool hasRetryAfter)
    {
        // Provider error details contain quotaId/quotaMetric only. Never retain or surface the payload.
        if (payload.Contains("PerDay", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("daily", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("RequestsPerDay", StringComparison.OrdinalIgnoreCase))
            return GeminiQuotaExhaustionKind.Daily;
        if (payload.Contains("PerMinute", StringComparison.OrdinalIgnoreCase) ||
            payload.Contains("minute", StringComparison.OrdinalIgnoreCase) || hasRetryAfter)
            return GeminiQuotaExhaustionKind.PerMinute;
        return GeminiQuotaExhaustionKind.Unknown;
    }

    public void Dispose() => _inFlight.Dispose();
}

internal sealed class GeminiRequestBudget
{
    private readonly int _maximumRequests;
    private int _reserved;
    private readonly ConcurrentDictionary<string, int> _attemptsByModel =
        new(StringComparer.OrdinalIgnoreCase);

    public int UsedRequests => Volatile.Read(ref _reserved);
    public IReadOnlyDictionary<string, int> AttemptsByModel =>
        new Dictionary<string, int>(_attemptsByModel, StringComparer.OrdinalIgnoreCase);

    public GeminiRequestBudget(int maximumRequests)
    {
        if (maximumRequests < 1) throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        _maximumRequests = maximumRequests;
    }

    public void Reserve(string model)
    {
        while (true)
        {
            int current = Volatile.Read(ref _reserved);
            if (current >= _maximumRequests)
                throw new GeminiRequestBudgetExceededException(_maximumRequests);
            if (Interlocked.CompareExchange(ref _reserved, current + 1, current) == current)
            {
                _attemptsByModel.AddOrUpdate(model, 1, static (_, count) => count + 1);
                return;
            }
        }
    }
}

internal sealed class GeminiQuotaCoordinator
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    private readonly ConcurrentDictionary<(Guid ProjectId, string Model), Bucket> _buckets = new();

    public async Task AcquireAsync(Guid projectId, string model, int estimatedTokens, GeminiSurveyLimits limits,
        Func<DateTimeOffset> utcNow, Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        if (estimatedTokens > limits.InputTokensPerMinute)
            throw new InvalidOperationException("Một batch khảo sát vượt giới hạn TPM cấu hình.");
        Bucket bucket = _buckets.GetOrAdd((projectId, model.ToLowerInvariant()),
            _ => new Bucket(PacificDate(utcNow()), limits));
        while (true)
        {
            TimeSpan wait;
            lock (bucket.Sync)
            {
                DateTimeOffset now = utcNow();
                bucket.RequestsPerMinute = Math.Min(bucket.RequestsPerMinute, limits.RequestsPerMinute);
                bucket.InputTokensPerMinute = Math.Min(bucket.InputTokensPerMinute, limits.InputTokensPerMinute);
                bucket.RequestsPerDay = Math.Min(bucket.RequestsPerDay, limits.RequestsPerDay);
                if (estimatedTokens > bucket.InputTokensPerMinute)
                    throw new InvalidOperationException("Một batch khảo sát vượt giới hạn TPM cấu hình.");
                DateOnly today = PacificDate(now);
                if (today != bucket.UsageDay) { bucket.UsageDay = today; bucket.RequestsToday = 0; }
                while (bucket.Recent.TryPeek(out Usage item) && now - item.Timestamp >= TimeSpan.FromMinutes(1))
                {
                    bucket.Recent.Dequeue();
                    bucket.RecentTokens -= item.EstimatedTokens;
                }
                if (bucket.RequestsToday >= bucket.RequestsPerDay)
                    throw new GeminiDailyQuotaExceededException();
                if (bucket.Recent.Count < bucket.RequestsPerMinute &&
                    bucket.RecentTokens + estimatedTokens <= bucket.InputTokensPerMinute)
                {
                    bucket.Recent.Enqueue(new(now, estimatedTokens));
                    bucket.RecentTokens += estimatedTokens;
                    bucket.RequestsToday++;
                    return;
                }
                wait = bucket.Recent.Count > 0
                    ? bucket.Recent.Peek().Timestamp.AddMinutes(1) - now
                    : TimeSpan.FromSeconds(1);
            }
            await delay(wait > TimeSpan.FromMilliseconds(10) ? wait : TimeSpan.FromMilliseconds(10),
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal static DateOnly QuotaDay(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Pacific).DateTime);

    private static DateOnly PacificDate(DateTimeOffset now) => QuotaDay(now);

    private sealed class Bucket(DateOnly usageDay, GeminiSurveyLimits limits)
    {
        public object Sync { get; } = new();
        public Queue<Usage> Recent { get; } = new();
        public DateOnly UsageDay { get; set; } = usageDay;
        public int RequestsToday { get; set; }
        public int RecentTokens { get; set; }
        public int RequestsPerMinute { get; set; } = limits.RequestsPerMinute;
        public int InputTokensPerMinute { get; set; } = limits.InputTokensPerMinute;
        public int RequestsPerDay { get; set; } = limits.RequestsPerDay;
    }

    private readonly record struct Usage(DateTimeOffset Timestamp, int EstimatedTokens);
}
