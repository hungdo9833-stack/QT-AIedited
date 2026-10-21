using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class CorpusIntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-integrity-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();



    [Fact]
    public async Task Inactive_retirement_checkpoints_even_without_any_rescan()
    {
        var (session, store, state) = await Scenario(2);
        session.Manifest.Chapters[1].DeletedAt = DateTimeOffset.UtcNow;
        var saved = await new ProjectCorpusIndexer(_repository).IndexAsync(session, state, store, new());
        Assert.False((await store.LoadAsync()).Chapters[1].Evidence.Single().IsCurrent);
        Assert.True(saved.Revision > state.Revision);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("gap")]
    [InlineData("overlap")]
    [InlineData("excerpt")]
    [InlineData("identity")]
    [InlineData("range")]
    public void Auditor_reports_exact_invalid_ranges(string defect)
    {
        Guid id = Guid.NewGuid(); const string source = "沈夕颜说：你好。";
        string hash = CorpusIntegrityAuditor.SourceHash(source);
        var index = new CorpusChapterIndex { ChapterId = id, ChapterNumber = 1, SourceSha256 = hash, SourceLengthUtf16 = source.Length,
            Evidence = ProjectCorpusIndexer.ScanChapter(id, source, hash, []) };
        var item = index.Evidence.Single();
        switch (defect)
        {
            case "hash": index.SourceSha256 = "other"; break;
            case "gap": item.IsCurrent = false; break;
            case "overlap": index.Evidence.Add(new() { ChapterId = id, SourceSha256 = hash, LengthUtf16 = 1, Excerpt = "沈" }); break;
            case "excerpt": item.Excerpt = "bad"; break;
            case "identity": item.ChapterId = Guid.NewGuid(); break;
            case "range": item.StartUtf16 = int.MaxValue; break;
        }
        var result = CorpusIntegrityAuditor.Inspect(id, 1, source, index);
        Assert.False(result.Indexed); Assert.NotEmpty(result.Issues);
        Assert.All(result.Issues, i => Assert.NotEmpty(i.Reason));
    }

    [Fact]
    public void Non_bmp_windows_cover_source_without_splitting_surrogates()
    {
        string source = new string('风', 899) + "𠀀" + new string('月', 920);
        Guid id = Guid.NewGuid(); string hash = CorpusIntegrityAuditor.SourceHash(source);
        var windows = ProjectCorpusIndexer.ScanChapter(id, source, hash, []);
        Assert.Equal(source, string.Concat(windows.Select(e => e.Excerpt)));
        Assert.True(CorpusIntegrityAuditor.Inspect(id, 1, source,
            new() { ChapterId = id, ChapterNumber = 1, SourceSha256 = hash, SourceLengthUtf16 = source.Length, Evidence = windows }).Indexed);
    }











    private sealed class EmptyFindings : ILlmClient
    {
        public int Calls;
        public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new LlmCompletion("{\"characters\":[],\"relationships\":[],\"glossary\":[]}", true, "stop", new())); }
    }

    private async Task<(ProjectSession, BookIntelligenceStore, BookIntelligenceState)> Scenario(int count)
    {
        var session = await _repository.CreateAsync(_root, "Synthetic integrity");
        for (int i = 0; i < count; i++)
            await _repository.AddChapterAsync(session, TranslationService.CreateChapter($"Chương {i + 1}", "沈夕颜说道：你好。"), i + 1, "章节");
        var store = new BookIntelligenceStore(session);
        var state = await new ProjectCorpusIndexer(_repository).IndexAsync(session, await store.LoadAsync(), store, new());
        return (session, store, state);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class ImmediateProgress<T>(Action<T> action) : IProgress<T> { public void Report(T value) => action(value); }
    private sealed class NeverCall : ILlmClient
    {
        public int Calls;
        public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Provider must not be called."); }
    }
}
