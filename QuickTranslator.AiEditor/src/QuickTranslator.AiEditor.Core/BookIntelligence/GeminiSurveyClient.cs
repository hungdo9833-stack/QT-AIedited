using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>Configurable API limits. These apply to a Google AI project, not individual API keys.</summary>
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
/// The API key is never serialized, logged, or included in request URLs.
/// </summary>
public sealed class GeminiSurveyClient : IJsonSchemaOllamaClient, IDisposable
{
    private static readonly Regex ModelName = new(@"^gemini-[a-zA-Z0-9._-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly GeminiSurveyLimits _limits;
    private readonly SemaphoreSlim _inFlight;
    private readonly object _quotaSync = new();
    private readonly Queue<(DateTimeOffset Timestamp, int EstimatedTokens)> _recent = new();
    private readonly TimeZoneInfo _pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
    private DateOnly _usageDay;
    private int _requestsToday;
    private int _recentTokens;

    public GeminiSurveyClient(HttpClient httpClient, string apiKey, GeminiSurveyLimits limits)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKey = !string.IsNullOrWhiteSpace(apiKey) ? apiKey : throw new ArgumentException("Thiếu Gemini API key.");
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _limits.Validate();
        _inFlight = new SemaphoreSlim(_limits.MaxInFlight, _limits.MaxInFlight);
        _usageDay = PacificDate(DateTimeOffset.UtcNow);
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
            for (int attempt = 0; attempt < 3; attempt++)
            {
                // Conservative approximation; actual token usage is read from usageMetadata.
                await WaitForQuotaAsync(Math.Max(1, prompt.Length), token).ConfigureAwait(false);
                using var request = new HttpRequestMessage(HttpMethod.Post,
                    "https://generativelanguage.googleapis.com/v1beta/models/" + options.Model + ":generateContent");
                request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
                var generationConfig = new Dictionary<string, object?>
                {
                    ["temperature"] = options.Temperature,
                    ["maxOutputTokens"] = options.MaximumOutputTokens
                };
                if (schema is JsonElement responseSchema)
                {
                    generationConfig["responseMimeType"] = "application/json";
                    generationConfig["responseSchema"] = responseSchema;
                }
                string body = JsonSerializer.Serialize(new
                {
                    contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
                    generationConfig
                });
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using HttpResponseMessage response = await _http.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == 2) throw new InvalidOperationException(
                        "Gemini API trả 429 nhiều lần. Đã dừng gửi để tránh tiêu tốn quota; hãy thử lại sau.");
                    TimeSpan delay = response.Headers.RetryAfter?.Delta ??
                        TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                    await Task.Delay(delay < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : delay,
                        token).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"Gemini API trả HTTP {(int)response.StatusCode}. Kiểm tra model, quyền API và hạn mức.");

                string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("candidates", out JsonElement candidates) ||
                    candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0 ||
                    !candidates[0].TryGetProperty("content", out JsonElement content) ||
                    !content.TryGetProperty("parts", out JsonElement parts) ||
                    parts.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Gemini không trả nội dung khảo sát; có thể response bị chặn.");
                string result = string.Concat(parts.EnumerateArray()
                    .Where(p => p.TryGetProperty("text", out _))
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
            throw new InvalidOperationException("Gemini API không hoàn tất khảo sát.");
        }
        finally { _inFlight.Release(); }
    }

    private DateOnly PacificDate(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _pacific).DateTime);

    private async Task WaitForQuotaAsync(int estimatedTokens, CancellationToken token)
    {
        if (estimatedTokens > _limits.InputTokensPerMinute)
            throw new InvalidOperationException("Một batch khảo sát vượt giới hạn TPM cấu hình.");
        while (true)
        {
            TimeSpan? wait;
            lock (_quotaSync)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                DateOnly today = PacificDate(now);
                if (today != _usageDay) { _usageDay = today; _requestsToday = 0; }
                while (_recent.TryPeek(out var item) && now - item.Timestamp >= TimeSpan.FromMinutes(1))
                {
                    _recent.Dequeue();
                    _recentTokens -= item.EstimatedTokens;
                }
                if (_requestsToday >= _limits.RequestsPerDay)
                    throw new InvalidOperationException(
                        "Đã chạm giới hạn RPD Gemini của Project. Chờ reset lúc 00:00 giờ Pacific.");
                if (_recent.Count < _limits.RequestsPerMinute &&
                    _recentTokens + estimatedTokens <= _limits.InputTokensPerMinute)
                {
                    _recent.Enqueue((now, estimatedTokens));
                    _recentTokens += estimatedTokens;
                    _requestsToday++;
                    return;
                }
                wait = _recent.Count > 0
                    ? _recent.Peek().Timestamp.AddMinutes(1) - now
                    : TimeSpan.FromSeconds(2);
            }
            await Task.Delay(wait.Value > TimeSpan.FromMilliseconds(100)
                ? wait.Value : TimeSpan.FromMilliseconds(100), token).ConfigureAwait(false);
        }
    }

    public void Dispose() => _inFlight.Dispose();
}
