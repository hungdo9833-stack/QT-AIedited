using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class BookSurveyConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "qtaie-survey-concurrency-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Batches_run_in_parallel_with_bounded_concurrency_and_serialized_checkpoints()
    {
        (ProjectSession session, BookIntelligenceStore store, ProjectResources resources, BookIntelligenceState state) =
            await ScenarioAsync(32, concurrency: 3);
        var client = new ParallelSurveyClient(TimeSpan.FromMilliseconds(35));

        BookIntelligenceState completed = await new AdaptiveBookIntelligenceAnalyzer(client)
            .AnalyzeAsync(session, state, store, resources, Options());

        Assert.Equal(4, client.Calls);
        Assert.Equal(3, client.MaximumConcurrency);
        Assert.Equal(32, completed.Evidence.Count(x => x.AiAnalyzed));
        Assert.Equal(4, completed.CompletedAnalysisKeys.Count);
        Assert.Equal(4, completed.Metrics.Requests);
        Assert.Equal(completed.Revision, (await store.LoadAsync()).Revision);
    }

    [Fact]
    public async Task Successful_sibling_batches_are_checkpointed_and_resume_skips_them_after_failure()
    {
        (ProjectSession session, BookIntelligenceStore store, ProjectResources resources, BookIntelligenceState state) =
            await ScenarioAsync(16, concurrency: 2);
        Guid failingEvidence = state.Evidence.First().Id;
        var partial = new ParallelSurveyClient(TimeSpan.FromMilliseconds(20), failingEvidence);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new AdaptiveBookIntelligenceAnalyzer(partial)
            .AnalyzeAsync(session, state, store, resources, Options()));

        BookIntelligenceState checkpoint = await store.LoadAsync();
        Assert.Equal(8, checkpoint.Evidence.Count(x => x.AiAnalyzed));
        Assert.Single(checkpoint.CompletedAnalysisKeys);
        Assert.Contains("synthetic batch failure", checkpoint.LastError);

        var recovered = new ParallelSurveyClient(TimeSpan.Zero);
        BookIntelligenceState completed = await new AdaptiveBookIntelligenceAnalyzer(recovered)
            .AnalyzeAsync(session, checkpoint, store, resources, Options());

        Assert.Equal(1, recovered.Calls);
        Assert.All(completed.Evidence, evidence => Assert.True(evidence.AiAnalyzed));
        Assert.Equal(2, completed.CompletedAnalysisKeys.Count);
        Assert.Empty(completed.LastError);
    }

    private async Task<(ProjectSession, BookIntelligenceStore, ProjectResources, BookIntelligenceState)> ScenarioAsync(
        int evidenceCount, int concurrency)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Synthetic survey");
        var store = new BookIntelligenceStore(session);
        BookIntelligenceState state = await store.LoadAsync();
        state.ScanMode = IntelligenceScanMode.FullSemantic;
        state.AnalysisConcurrency = concurrency;
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("synthetic")));
        Guid chapterId = Guid.NewGuid();
        var evidence = Enumerable.Range(0, evidenceCount).Select(index => new CorpusEvidence
        {
            ChapterId = chapterId,
            StartUtf16 = index * 10,
            LengthUtf16 = 8,
            SourceSha256 = hash,
            Excerpt = $"人物{index:00}说道。",
            Priority = EvidencePriority.High
        }).ToList();
        state.Chapters.Add(new CorpusChapterIndex
        {
            ChapterId = chapterId,
            ChapterNumber = 1,
            SourceSha256 = hash,
            SourceLengthUtf16 = evidenceCount * 10 + 8,
            Evidence = evidence
        });
        state = await store.SaveAsync(state);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        return (session, store, resources, state);
    }

    private static ModelOptions Options() => new()
    {
        Model = "survey-model", ContextSize = 4096, MaximumOutputTokens = 1024,
        RequestTimeout = TimeSpan.FromSeconds(10)
    };

    public void Dispose()
    {
        string path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("qtaie-survey-concurrency-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid synthetic root.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private sealed class ParallelSurveyClient(TimeSpan delay, Guid? failingEvidence = null) : IOllamaClient
    {
        private int _calls;
        private int _active;
        private int _maximum;
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrency => Volatile.Read(ref _maximum);

        public async Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = _maximum; }
            while (active > observed && Interlocked.CompareExchange(ref _maximum, active, observed) != observed);
            try
            {
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
                if (failingEvidence is Guid id && prompt.Contains(id.ToString("D"), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("synthetic batch failure");
                return new OllamaCompletion(
                    "{\"characters\":[],\"relationships\":[],\"glossary\":[]}", true, "stop",
                    new GenerationMetrics { InputTokens = 100, OutputTokens = 10, ClientElapsed = delay });
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
