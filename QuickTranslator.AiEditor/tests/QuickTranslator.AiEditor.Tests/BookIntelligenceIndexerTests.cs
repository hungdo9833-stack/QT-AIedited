using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class BookIntelligenceIndexerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-intelligence-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Corpus_index_reads_original_text_records_utf16_evidence_and_does_not_call_ai()
    {
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync("林朝看向小朝说道：师父来了。\r\n普通正文。", 1);
        var fake = new SurveyClient(_ => "{}");
        var store = new BookIntelligenceStore(session);
        BookIntelligenceState state = await store.LoadAsync();
        state = await new ProjectCorpusIndexer(repository).IndexAsync(session, state, store,
            (await new ProjectResourceStore(session).LoadAsync()).Characters);

        CorpusEvidence evidence = Assert.Single(state.Evidence, x => x.IsCurrent);
        Assert.Equal(evidence.Excerpt, (await repository.LoadChapterAsync(session, evidence.ChapterId)).Chapter.OriginalText
            .Substring(evidence.StartUtf16, evidence.LengthUtf16));
        Assert.Equal(0, fake.Calls);
        Assert.False(state.AutoApplyVerified);
    }

    [Fact]
    public async Task Incremental_rescan_skips_unchanged_chapters_and_retires_old_evidence()
    {
        (ProjectRepository repository, ProjectSession session) = await ProjectAsync("林朝说道：你好。", 1);
        var store = new BookIntelligenceStore(session);
        var resources = await new ProjectResourceStore(session).LoadAsync();
        BookIntelligenceState first = await new ProjectCorpusIndexer(repository).IndexAsync(session,
            await store.LoadAsync(), store, resources.Characters);
        DateTimeOffset scanned = first.Chapters.Single().ScannedAt;
        BookIntelligenceState unchanged = await new ProjectCorpusIndexer(repository).IndexAsync(session, first, store, resources.Characters);
        Assert.Equal(scanned, unchanged.Chapters.Single().ScannedAt);

        ChapterDocument chapter = (await repository.LoadChapterAsync(session, session.Manifest.Chapters.Single().ChapterId)).Chapter;
        SourceEditPreview preview = SourceEditPlanner.Preview(chapter, "林朝说道：你坏。");
        chapter = SourceEditPlanner.Apply(chapter, preview, confirmed: true, DateTimeOffset.UtcNow);
        await repository.UpdateChapterAsync(session, chapter);
        BookIntelligenceState changed = await new ProjectCorpusIndexer(repository).IndexAsync(session, unchanged, store, resources.Characters);
        Assert.Contains(changed.Chapters.Single().Evidence, x => !x.IsCurrent);
        Assert.Contains(changed.Chapters.Single().Evidence, x => x.IsCurrent && x.Excerpt.Contains("你坏"));
    }

    private async Task<(ProjectRepository Repository, ProjectSession Session)> ProjectAsync(string source, int number)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Chapter", source);
        await repository.AddChapterAsync(session, chapter, number, "Chapter");
        return (repository, session);
    }

    public void Dispose()
    {
        string path = Path.GetFullPath(_root);
        if (!path.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("qtaie-intelligence-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid synthetic root.");
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    private sealed class SurveyClient(Func<string, string> response) : IOllamaClient
    {
        public int Calls { get; private set; }
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new OllamaCompletion(response(prompt), true, "stop", new()));
        }
    }
}
