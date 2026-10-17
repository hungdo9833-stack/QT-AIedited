using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>Marker for the survey-only, complexity-aware batching path.</summary>
public interface IAdaptiveSurveyBatchClient
{
    string CacheSignature { get; }
    int TargetInputTokens { get; }
}

/// <summary>One verified Gemini Flash model. Flash Lite is intentionally excluded from surveying.</summary>
public sealed record GeminiSurveyModel(string Model, GeminiSurveyLimits Limits);

/// <summary>
/// A pool of independent Flash models sharing the same Google AI project quota coordinator.
/// Each request goes to ONE model. Results remain detached until the analyzer commits them
/// sequentially, so worker requests never mutate BookIntelligenceState.
/// </summary>
public sealed class GeminiSurveyPoolClient : IJsonSchemaOllamaClient, IAdaptiveSurveyBatchClient, IDisposable
{
    private readonly List<Slot> _slots;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _availability = new(0);
    private GeminiRequestBudget? _requestBudget;
    private readonly Func<DateTimeOffset> _utcNow;
    private int _next;
    private bool _disposed;

    public string CacheSignature { get; }
    // Higher token budget applies only to simple batches; relationship/identity batches
    // are capped far lower by AdaptiveBookIntelligenceAnalyzer.
    public int TargetInputTokens { get; } = 40_000;
    public int RecommendedConcurrency => Math.Min(8, _slots.Sum(s => s.Model.Limits.MaxInFlight));
    public int RequestsUsed => _requestBudget?.UsedRequests ?? 0;
    public IReadOnlyDictionary<string, int> RequestAttemptsByModel =>
        _requestBudget?.AttemptsByModel ?? new Dictionary<string, int>();

    public GeminiSurveyPoolClient(HttpClient http, string apiKey, Guid googleProjectScope,
        IReadOnlyList<GeminiSurveyModel> models, int? maximumRequests = null)
        : this(http, apiKey, googleProjectScope, models,
            maximumRequests is int value ? new GeminiRequestBudget(value) : null) { }

    private GeminiSurveyPoolClient(HttpClient http, string apiKey, Guid googleProjectScope,
        IReadOnlyList<GeminiSurveyModel> models, GeminiRequestBudget? requestBudget)
        : this(models, model => new GeminiSurveyClient(http, apiKey, model.Limits,
            googleProjectScope, routeFailuresToPool: true, requestBudget: requestBudget),
            () => DateTimeOffset.UtcNow)
    {
        _requestBudget = requestBudget;
    }

    internal GeminiSurveyPoolClient(IReadOnlyList<GeminiSurveyModel> models,
        Func<GeminiSurveyModel, IJsonSchemaOllamaClient> factory, Func<DateTimeOffset> utcNow)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(factory);
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        if (models.Count is < 1 or > 8 || models.Any(x => !IsAllowedFlash(x.Model)) ||
            models.Select(x => x.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count() != models.Count)
            throw new ArgumentException("Pool chỉ chấp nhận 1–8 model Gemini Flash khác nhau; không dùng Flash Lite.");
        foreach (GeminiSurveyModel model in models) model.Limits.Validate();
        _slots = models.Select(m => new Slot(m, factory(m))).ToList();
        // Logical cache identity: independent of assignment order and the model that
        // happened to handle a batch; changes if the pool composition/policy changes.
        CacheSignature = "gemini-survey-pool-v1:" + string.Join(",",
            models.Select(x => x.Model.ToLowerInvariant()).Order(StringComparer.Ordinal));
    }

    public static bool IsAllowedFlash(string model) =>
        !string.IsNullOrWhiteSpace(model) &&
        model.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase) &&
        model.Contains("flash", StringComparison.OrdinalIgnoreCase) &&
        !model.Contains("lite", StringComparison.OrdinalIgnoreCase) &&
        model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static IReadOnlyList<GeminiSurveyModel> ModelsFromSettings(string? configuredModels)
    {
        string source = string.IsNullOrWhiteSpace(configuredModels)
            ? "gemini-3.6-flash,gemini-3.7-flash,gemini-3.8-flash"
            : configuredModels;
        string[] names = source.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length is < 1 or > 8 || names.Any(x => !IsAllowedFlash(x)) ||
            names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new ArgumentException("QTSTUDIO_GEMINI_SURVEY_MODELS phải chứa 1–8 model Flash hợp lệ, không có Flash Lite.");
        return names.Select(x => new GeminiSurveyModel(x, GeminiSurveyLimits.FromObservedFreeTier(x))).ToArray();
    }

    public static int? MaximumRequestsFromSettings(string? configuredMaximum)
    {
        if (string.IsNullOrWhiteSpace(configuredMaximum)) return null;
        if (!int.TryParse(configuredMaximum, out int maximum) || maximum is < 1 or > 1_000)
            throw new ArgumentException("QTSTUDIO_GEMINI_SURVEY_MAX_REQUESTS phải là số nguyên từ 1 đến 1000.");
        return maximum;
    }

    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(prompt, options, null, cancellationToken);

    public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
        JsonElement responseSchema, CancellationToken cancellationToken = default)
    {
        if (responseSchema.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Gemini Survey cần JSON schema kiểu object.");
        return ExecuteAsync(prompt, options, responseSchema.Clone(), cancellationToken);
    }

    private async Task<OllamaCompletion> ExecuteAsync(string prompt, ModelOptions options,
        JsonElement? schema, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        ArgumentNullException.ThrowIfNull(options);
        var attempted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Slot? selected = await ReserveAsync(attempted, cancellationToken).ConfigureAwait(false);
            if (selected is null)
                throw new InvalidOperationException(
                    "Không còn model Flash khả dụng trong Pool. Giữ nguyên batch chưa hoàn thành để tiếp tục sau; kiểm tra model/quota.");
            attempted.Add(selected.Model.Model);
            try
            {
                ModelOptions modelOptions = options with { Model = selected.Model.Model };
                return schema is JsonElement responseSchema
                    ? await selected.Client.TranslateStructuredAsync(prompt, modelOptions, responseSchema, cancellationToken)
                        .ConfigureAwait(false)
                    : await selected.Client.TranslateAsync(prompt, modelOptions, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (GeminiDailyQuotaExceededException)
            {
                lock (_sync) selected.Disabled = true;
            }
            catch (GeminiSurveyHttpException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                lock (_sync)
                    selected.CooldownUntil = _utcNow() +
                        (ex.RetryAfter is TimeSpan retry && retry > TimeSpan.FromMinutes(1)
                            ? retry : TimeSpan.FromMinutes(1));
            }
            catch (GeminiSurveyHttpException ex) when (ex.StatusCode is
                HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
            {
                lock (_sync) selected.Disabled = true;
            }
            finally
            {
                lock (_sync) selected.Active--;
                _availability.Release();
            }
        }
    }

    private async Task<Slot?> ReserveAsync(HashSet<string> attempted, CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(GeminiSurveyPoolClient));
                DateTimeOffset now = _utcNow();
                Slot[] viable = _slots.Where(x => !x.Disabled && x.CooldownUntil <= now &&
                    !attempted.Contains(x.Model.Model)).ToArray();
                if (viable.Length == 0) return null;
                Slot[] available = viable.Where(x => x.Active < x.Model.Limits.MaxInFlight).ToArray();
                if (available.Length > 0)
                {
                    int fewest = available.Min(x => x.Active);
                    Slot[] candidates = available.Where(x => x.Active == fewest).ToArray();
                    Slot chosen = candidates[(_next++ & int.MaxValue) % candidates.Length];
                    chosen.Active++;
                    return chosen;
                }
            }
            await _availability.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (Slot slot in _slots)
                (slot.Client as IDisposable)?.Dispose();
            _availability.Dispose();
        }
    }

    private sealed class Slot(GeminiSurveyModel model, IJsonSchemaOllamaClient client)
    {
        public GeminiSurveyModel Model { get; } = model;
        public IJsonSchemaOllamaClient Client { get; } = client;
        public int Active { get; set; }
        public bool Disabled { get; set; }
        public DateTimeOffset CooldownUntil { get; set; }
    }
}

/// <summary>
/// Quota scope must represent the Google AI project rather than a QT Studio novel.
/// Explicit Google project ID is preferred. With only one API key, its deterministic
/// fingerprint keeps quota shared across novels without storing the secret.
/// Separate keys belonging to the same Google project require explicit project ID.
/// </summary>
public static class GeminiSurveyProjectScope
{
    public static Guid Resolve(string apiKey, string? googleProjectId)
    {
        string identity = !string.IsNullOrWhiteSpace(googleProjectId)
            ? "google-project:" + googleProjectId.Trim()
            : "google-api-key:" + apiKey;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(digest.AsSpan(0, 16));
    }
}
