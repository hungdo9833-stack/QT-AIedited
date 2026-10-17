using System.Net;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GeminiSurveyPoolTests
{
    private static readonly string EmptyResponse =
        "{\"characters\":[],\"relationships\":[],\"glossary\":[]}";
    private static readonly JsonElement Schema =
        JsonSerializer.Deserialize<JsonElement>("""{"type":"object"}""");
    private static readonly GeminiSurveyModel[] Models =
    [
        new("gemini-3.6-flash", new(5, 250_000, 20, 2)),
        new("gemini-3.7-flash", new(5, 250_000, 20, 2)),
        new("gemini-3.8-flash", new(5, 250_000, 20, 2))
    ];

    [Fact]
    public void Only_explicit_Flash_models_can_enter_survey_pool()
    {
        var parsed = GeminiSurveyPoolClient.ModelsFromSettings(null);
        Assert.Equal(["gemini-3.8-flash", "gemini-3.6-flash"], parsed.Select(x => x.Model));
        Assert.All(parsed, x => Assert.True(GeminiSurveyPoolClient.IsAllowedFlash(x.Model)));
        Assert.Throws<ArgumentException>(() =>
            GeminiSurveyPoolClient.ModelsFromSettings("gemini-3.6-flash,gemini-3.5-flash-lite"));
        Assert.Throws<ArgumentException>(() =>
            GeminiSurveyPoolClient.ModelsFromSettings("gemini-3.7-flash,gemini-3.7-flash"));
    }

    [Fact]
    public void Optional_live_request_budget_is_strictly_validated()
    {
        Assert.Null(GeminiSurveyPoolClient.MaximumRequestsFromSettings(null));
        Assert.Equal(5, GeminiSurveyPoolClient.MaximumRequestsFromSettings("5"));
        Assert.Throws<ArgumentException>(() => GeminiSurveyPoolClient.MaximumRequestsFromSettings("0"));
        Assert.Throws<ArgumentException>(() => GeminiSurveyPoolClient.MaximumRequestsFromSettings("unlimited"));
    }

    [Fact]
    public async Task Six_concurrent_batches_are_spread_across_three_models()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var pool = new GeminiSurveyPoolClient(Models, _ => new DelegateClient(async options =>
        {
            seen.Add(options.Model);
            await Task.Delay(25);
            return Done();
        }), () => DateTimeOffset.UtcNow);
        var options = new ModelOptions { Model = "gemini-3.7-flash" };
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ =>
            pool.TranslateStructuredAsync("Đọc nguyên tác", options, Schema)));

        Assert.Equal(6, seen.Count);
        Assert.Equal(3, seen.Distinct().Count());
        Assert.Equal(6, pool.RecommendedConcurrency);
    }

    [Fact]
    public async Task Stress_load_never_exceeds_per_model_or_pool_in_flight_limits()
    {
        var concurrency = new System.Collections.Concurrent.ConcurrentDictionary<string, ModelConcurrency>();
        using var pool = new GeminiSurveyPoolClient(Models, model => new DelegateClient(async options =>
        {
            ModelConcurrency counter = concurrency.GetOrAdd(model.Model, _ => new());
            counter.Enter();
            try
            {
                await Task.Delay(15);
                return Done();
            }
            finally { counter.Exit(); }
        }), () => DateTimeOffset.UtcNow);

        var options = new ModelOptions { Model = "gemini-3.8-flash" };
        await Task.WhenAll(Enumerable.Range(0, 60).Select(index =>
            pool.TranslateStructuredAsync("Đọc nguyên tác " + index, options, Schema)));

        Assert.Equal(3, concurrency.Count);
        Assert.All(concurrency.Values, counter => Assert.InRange(counter.Maximum, 1, 2));
        Assert.InRange(concurrency.Values.Sum(counter => counter.Maximum), 3, pool.RecommendedConcurrency);
    }

    [Fact]
    public async Task Rpd_on_one_model_routes_unfinished_batch_to_another()
    {
        var seen = new List<string>();
        using var pool = new GeminiSurveyPoolClient(Models[..2], _ => new DelegateClient(options =>
        {
            seen.Add(options.Model);
            if (options.Model == "gemini-3.6-flash")
                throw new GeminiDailyQuotaExceededException();
            return Task.FromResult(Done());
        }), () => DateTimeOffset.UtcNow);
        OllamaCompletion result = await pool.TranslateStructuredAsync(
            "Đọc nguyên tác", new ModelOptions { Model = "gemini-3.7-flash" }, Schema);
        Assert.Equal(EmptyResponse, result.Text);
        Assert.Contains("gemini-3.6-flash", seen);
        Assert.Contains("gemini-3.7-flash", seen);
        seen.Clear();
        await pool.TranslateAsync("Đọc tiếp", new ModelOptions { Model = "gemini-3.7-flash" });
        Assert.DoesNotContain("gemini-3.6-flash", seen);
    }

    [Fact]
    public async Task Rate_limit_cools_one_model_without_using_Flash_Lite()
    {
        var seen = new List<string>();
        var now = new DateTimeOffset(2026, 10, 17, 0, 0, 0, TimeSpan.Zero);
        using var pool = new GeminiSurveyPoolClient(Models[..2], _ => new DelegateClient(options =>
        {
            seen.Add(options.Model);
            if (options.Model == "gemini-3.6-flash")
                throw new GeminiSurveyHttpException(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(2));
            return Task.FromResult(Done());
        }), () => now);
        await pool.TranslateAsync("Khảo sát", new ModelOptions { Model = "gemini-3.7-flash" });
        Assert.Contains("gemini-3.7-flash", seen);
        seen.Clear();
        await pool.TranslateAsync("Khảo sát nữa", new ModelOptions { Model = "gemini-3.7-flash" });
        Assert.DoesNotContain("gemini-3.6-flash", seen);
        Assert.All(seen, name => Assert.DoesNotContain("lite", name, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task All_models_exhausted_leaves_batch_uncommitted()
    {
        using var pool = new GeminiSurveyPoolClient(Models[..2], _ =>
            new DelegateClient(_ => throw new GeminiDailyQuotaExceededException()), () => DateTimeOffset.UtcNow);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pool.TranslateAsync("Khảo sát", new ModelOptions { Model = "gemini-3.7-flash" }));
        Assert.Contains("Không còn model Flash", failure.Message);
    }

    [Fact]
    public void Batch_policy_packs_simple_context_but_is_conservative_for_relationships()
    {
        var options = new ModelOptions
        {
            ContextSize = 65_536,
            MaximumOutputTokens = 8192,
            Model = "gemini-3.7-flash"
        };
        var easy = Enumerable.Range(0, 45).Select(i => new CorpusEvidence
        {
            Excerpt = "风吹过山谷，弟子前往青云宗。" + i,
            Kind = IntelligenceEvidenceKind.Term,
            Priority = EvidencePriority.Medium,
            SemanticGroup = "glossary"
        }).ToArray();
        var sensitive = easy.Select(e => new CorpusEvidence
        {
            Excerpt = e.Excerpt,
            Kind = IntelligenceEvidenceKind.Relationship,
            Priority = EvidencePriority.High,
            SemanticGroup = "relationship"
        }).ToArray();
        var large = AdaptiveBookIntelligenceAnalyzer.TakeAdaptiveBatch(easy, options, 24_000);
        var careful = AdaptiveBookIntelligenceAnalyzer.TakeAdaptiveBatch(sensitive, options, 24_000);
        Assert.True(large.Length > 12);
        Assert.True(careful.Length <= 12);
        Assert.True(large.Length <= 36);
    }

    [Fact]
    public void Quota_identity_is_shared_by_the_actual_google_project()
    {
        Guid same = GeminiSurveyProjectScope.Resolve("secret-a", "my-google-project");
        Assert.Equal(same, GeminiSurveyProjectScope.Resolve("secret-b", "my-google-project"));
        Assert.NotEqual(same, GeminiSurveyProjectScope.Resolve("secret-a", "another-project"));
        Assert.Equal(GeminiSurveyProjectScope.Resolve("secret-a", null),
                     GeminiSurveyProjectScope.Resolve("secret-a", ""));
    }

    private static OllamaCompletion Done() => new(EmptyResponse, true, "stop",
        new GenerationMetrics { InputTokens = 150, OutputTokens = 12 });

    private sealed class DelegateClient(Func<ModelOptions, Task<OllamaCompletion>> run) : IJsonSchemaOllamaClient
    {
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default) => run(options);

        public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
            JsonElement responseSchema, CancellationToken cancellationToken = default) => run(options);
    }

    private sealed class ModelConcurrency
    {
        private int _active;
        private int _maximum;
        public int Maximum => Volatile.Read(ref _maximum);

        public void Enter()
        {
            int active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = _maximum; }
            while (active > observed && Interlocked.CompareExchange(ref _maximum, active, observed) != observed);
        }

        public void Exit() => Interlocked.Decrement(ref _active);
    }
}
