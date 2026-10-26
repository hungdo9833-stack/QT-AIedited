using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed record GeminiTranslationRoute(string Model, int KeyIndex, int KeyCount)
{
    public string DisplayText => $"API · {Model switch
    {
        GeminiTranslationClient.PreferredModel => "Gemini 3.5 Flash-Lite",
        GeminiTranslationClient.FallbackModel => "Gemini 3.1 Flash-Lite",
        _ => Model
    }} (key {KeyIndex + 1}/{KeyCount})";
}

public sealed class GeminiTranslationQuotaException(string message) : InvalidOperationException(message);

/// <summary>
/// Gemini transport for the normal translation pipeline. Prompt construction, segmentation,
/// validation and checkpointing remain owned by the existing translation coordinator.
/// </summary>
public sealed class GeminiTranslationClient : ILlmBackendClient, IJsonSchemaLlmClient
{
    public const string PreferredModel = "gemini-3.5-flash-lite";
    public const string FallbackModel = "gemini-3.1-flash-lite";
    private static readonly string[] Models = [PreferredModel, FallbackModel];
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string[] _keys;
    private readonly RouteState[,] _states;
    private readonly GeminiSurveyClient[,] _clients;
    private readonly SemaphoreSlim _inFlight = new(1, 1);
    private readonly Func<DateTimeOffset> _utcNow;
    private bool _disposed;

    public GeminiTranslationRoute CurrentRoute { get; private set; }
    public event EventHandler<GeminiTranslationRoute>? RouteChanged;

    public GeminiTranslationClient(HttpClient httpClient, IReadOnlyList<string> apiKeys, bool ownsHttpClient = false)
        : this(httpClient, apiKeys, () => DateTimeOffset.UtcNow, ownsHttpClient) { }

    internal GeminiTranslationClient(HttpClient httpClient, IReadOnlyList<string> apiKeys,
        Func<DateTimeOffset> utcNow, bool ownsHttpClient = false)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentNullException.ThrowIfNull(apiKeys);
        _keys = apiKeys.Select(value => value?.Trim() ?? "")
            .Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (_keys.Length == 0) throw new InvalidOperationException(
            "Chưa có Gemini API key. Hãy thêm key trong AI & hiệu năng hoặc Cài đặt → Gemini.");
        if (_keys.Any(value => value.Contains('\r') || value.Contains('\n')))
            throw new ArgumentException("Gemini API key không hợp lệ.", nameof(apiKeys));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _ownsHttpClient = ownsHttpClient;
        _states = new RouteState[_keys.Length, Models.Length];
        _clients = new GeminiSurveyClient[_keys.Length, Models.Length];
        for (int key = 0; key < _keys.Length; key++)
        for (int model = 0; model < Models.Length; model++)
        {
            _states[key, model] = new RouteState();
            _clients[key, model] = new GeminiSurveyClient(_http, _keys[key],
                GeminiSurveyLimits.FromObservedFreeTier(Models[model]), AnonymousProjectId(_keys[key]),
                routeFailuresToPool: true);
        }
        CurrentRoute = new(PreferredModel, 0, _keys.Length);
    }

    public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(prompt, options, null, cancellationToken);

    public Task<LlmCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
        JsonElement responseSchema, CancellationToken cancellationToken = default)
    {
        if (responseSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Gemini JSON Schema phải là object.", nameof(responseSchema));
        return ExecuteAsync(prompt, options, responseSchema.Clone(), cancellationToken);
    }

    private async Task<LlmCompletion> ExecuteAsync(string prompt, ModelOptions options, JsonElement? schema,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _inFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (TrySelectRoute(out int keyIndex, out int modelIndex))
            {
                string model = Models[modelIndex];
                SetCurrentRoute(new(model, keyIndex, _keys.Length));
                ModelOptions routedOptions = options with { Model = model };
                try
                {
                    return schema is JsonElement responseSchema
                        ? await _clients[keyIndex, modelIndex].TranslateStructuredAsync(prompt, routedOptions,
                            responseSchema, cancellationToken).ConfigureAwait(false)
                        : await _clients[keyIndex, modelIndex].TranslateAsync(prompt, routedOptions,
                            cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidDataException error) when (
                    error.Message.Contains("finishReason=MAX_TOKENS", StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Gemini đã cắt bản dịch vì chạm giới hạn token (MAX_TOKENS). " +
                        "Chương chưa được đánh dấu hoàn tất hoặc lưu như bản dịch hợp lệ.", error);
                }
                catch (GeminiSurveyHttpException error) when (error.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    MarkExhausted(keyIndex, modelIndex, error);
                }
                catch (GeminiDailyQuotaExceededException)
                {
                    _states[keyIndex, modelIndex].DailyQuotaDay = GeminiQuotaCoordinator.QuotaDay(_utcNow());
                }
            }
            throw BuildExhaustedError();
        }
        finally { _inFlight.Release(); }
    }

    private bool TrySelectRoute(out int keyIndex, out int modelIndex)
    {
        DateTimeOffset now = _utcNow();
        DateOnly today = GeminiQuotaCoordinator.QuotaDay(now);
        for (int key = 0; key < _keys.Length; key++)
        for (int model = 0; model < Models.Length; model++)
        {
            RouteState state = _states[key, model];
            if (state.DailyQuotaDay is DateOnly exhaustedDay && exhaustedDay != today)
                state.DailyQuotaDay = null;
            if (state.CooldownUntil is DateTimeOffset retryAt && retryAt <= now)
                state.CooldownUntil = null;
            if (state.DailyQuotaDay is null && state.CooldownUntil is null)
            {
                keyIndex = key;
                modelIndex = model;
                return true;
            }
        }
        keyIndex = modelIndex = -1;
        return false;
    }

    private void MarkExhausted(int keyIndex, int modelIndex, GeminiSurveyHttpException error)
    {
        RouteState state = _states[keyIndex, modelIndex];
        if (error.QuotaKind == GeminiQuotaExhaustionKind.Daily ||
            error.QuotaKind == GeminiQuotaExhaustionKind.Unknown && error.RetryAfter is null)
            state.DailyQuotaDay = GeminiQuotaCoordinator.QuotaDay(_utcNow());
        else
            state.CooldownUntil = _utcNow().Add(error.RetryAfter ?? TimeSpan.FromMinutes(1));
    }

    private GeminiTranslationQuotaException BuildExhaustedError()
    {
        DateTimeOffset? retry = _states.Cast<RouteState>()
            .Where(state => state.CooldownUntil is not null)
            .Select(state => state.CooldownUntil).Min();
        string suffix = retry is DateTimeOffset retryAt
            ? $" Có thể tiếp tục sau {retryAt.ToLocalTime():HH:mm:ss} khi hạn mức theo phút được mở lại."
            : " Hạn mức ngày sẽ tự mở lại khi ngày quota Pacific đổi.";
        return new("Tất cả Gemini API key và cả hai model Flash-Lite đều đã hết hạn mức." + suffix +
            " Hàng đợi đã dừng an toàn; không có đoạn rỗng nào được đánh dấu hoàn tất.");
    }

    private void SetCurrentRoute(GeminiTranslationRoute route)
    {
        if (route == CurrentRoute) return;
        CurrentRoute = route;
        RouteChanged?.Invoke(this, route);
    }

    private static Guid AnonymousProjectId(string key)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return new Guid(hash.AsSpan(0, 16));
    }

    public Task CheckConnectionAsync(string model, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(Models);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (GeminiSurveyClient client in _clients) client.Dispose();
        _inFlight.Dispose();
        if (_ownsHttpClient) _http.Dispose();
    }

    private sealed class RouteState
    {
        public DateTimeOffset? CooldownUntil { get; set; }
        public DateOnly? DailyQuotaDay { get; set; }
    }
}
