using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class GeminiSurveyHttpException(HttpStatusCode statusCode, TimeSpan? retryAfter = null)
    : InvalidOperationException($"Gemini API trả HTTP {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

public sealed class GeminiDailyQuotaExceededException()
    : InvalidOperationException("Đã chạm giới hạn RPD Gemini của Project/model. Chờ reset lúc 00:00 giờ Pacific.");

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
public sealed class GeminiSurveyClient : IJsonSchemaOllamaClient, IDisposable
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

    public int MaxConcurrency => _limits.MaxInFlight;

    public GeminiSurveyClient(HttpClient httpClient, string apiKey, GeminiSurveyLimits limits, Guid projectId,
        bool routeFailuresToPool = false)
        : this(httpClient, apiKey, limits, projectId, SharedQuota, () => DateTimeOffset.UtcNow,
            static (delay, token) => Task.Delay(delay, token), routeFailuresToPool) { }

    internal GeminiSurveyClient(HttpClient httpClient, string apiKey, GeminiSurveyLimits limits, Guid projectId,
        GeminiQuotaCoordinator quota, Func<DateTimeOffset> utcNow,
        Func<TimeSpan, CancellationToken, Task> delay, bool routeFailuresToPool = false)
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
        _inFlight = new SemaphoreSlim(_limits.MaxInFlight, _limits.MaxInFlight);
    }

    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default) => GenerateAsync(prompt, options, null, cancellationToken);

    public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
        JsonElement responseSchema, CancellationToken cancellationToken = default)
    {
        if (responseSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Gemini JSON Schema phải là object.", nameof(responseSchema));
        return GenerateAsync(prompt, options, responseSchema.Clone(), cancellationToken);
    }

    private async Task<OllamaCompletion> GenerateAsync(string prompt, ModelOptions options,
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
                    throw new GeminiSurveyHttpException(response.StatusCode,
                        RetryDelay(response, attempt, _utcNow()));
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
                    if (!root.TryGetProperty("candidates", out JsonElement candidates) ||
                        candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0)
                        throw new InvalidDataException("Gemini không trả candidate khảo sát; prompt có thể đã bị chặn.");
                    JsonElement candidate = candidates[0];
                    if (candidate.TryGetProperty("finishReason", out JsonElement finish) &&
                        finish.ValueKind == JsonValueKind.String &&
                        !string.Equals(finish.GetString(), "STOP", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Gemini dừng khảo sát với lý do {finish.GetString()}.");
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
                    }
                    return new OllamaCompletion(result, true, "stop",
                        new GenerationMetrics { InputTokens = input, OutputTokens = output, ClientElapsed = stopwatch.Elapsed });
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Gemini trả response JSON không hợp lệ.", ex);
                }
                }
            }
            throw new InvalidOperationException("Gemini API không hoàn tất khảo sát.");
        }
        finally { _inFlight.Release(); }
    }

    private HttpRequestMessage CreateRequest(string prompt, ModelOptions options, JsonElement? schema)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://generativelanguage.googleapis.com/v1beta/models/" + options.Model + ":generateContent");
        request.Headers.Add("x-goog-api-key", _apiKey);
        var generationConfig = new Dictionary<string, object?>
        {
            ["temperature"] = options.Temperature,
            ["maxOutputTokens"] = options.MaximumOutputTokens
        };
        if (schema is JsonElement responseSchema)
        {
            generationConfig["responseMimeType"] = "application/json";
            generationConfig["responseJsonSchema"] = responseSchema;
        }
        string body = JsonSerializer.Serialize(new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig
        });
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
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

    public void Dispose() => _inFlight.Dispose();
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

    private static DateOnly PacificDate(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, Pacific).DateTime);

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
