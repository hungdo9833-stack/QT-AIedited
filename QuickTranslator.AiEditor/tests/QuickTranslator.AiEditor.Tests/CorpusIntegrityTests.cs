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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Nineteen_chapters_distinguish_missing_current_active_from_legitimately_inactive(bool inactive)
    {
        var (session, store, state) = await Scenario(19);
        foreach (var chapter in state.Chapters.Skip(8))
        {
            foreach (var evidence in chapter.Evidence) evidence.IsCurrent = false;
            if (inactive) session.Manifest.Chapters.Single(x => x.ChapterId == chapter.ChapterId).DeletedAt = DateTimeOffset.UtcNow;
        }
        foreach (var evidence in state.Evidence.Where(x => x.IsCurrent)) evidence.AiAnalyzed = true;
        state.LastError = ""; state.NextEvidenceIndex = 87;
        state = await store.SaveAsync(state);
        var auditor = new CorpusIntegrityAuditor(_repository);
        var before = await auditor.AuditAsync(session, state);
        Assert.Equal(inactive ? 8 : 19, before.ActiveChapters);
        Assert.Equal(!inactive, before.NeedsReindex);
        Assert.Equal(inactive, before.Complete);
        if (!inactive) Assert.Equal(11, before.Missing);
        string beforeDecisions = JsonSerializer.Serialize(state.Characters);
        var indexer = new ProjectCorpusIndexer(_repository);
        var reconciled = await indexer.IndexAsync(session, state, store, new());
        var after = await auditor.AuditAsync(session, reconciled);
        Assert.False(after.NeedsReindex);
        Assert.Equal(inactive ? 8 : 19, after.IndexedChapters);
        Assert.Equal(beforeDecisions, JsonSerializer.Serialize(reconciled.Characters));
        var again = await indexer.IndexAsync(session, reconciled, store, new());
        Assert.Equal(reconciled.Revision, again.Revision);
        Assert.Equal(reconciled.Evidence.Select(e => e.Id), again.Evidence.Select(e => e.Id));
    }

    [Fact]
    public async Task Cancel_restart_backup_and_rollback_keep_provenance_approvals_and_source()
    {
        var (session, store, state) = await Scenario(3);
        foreach (var chapter in state.Chapters) foreach (var evidence in chapter.Evidence) evidence.IsCurrent = false;
        var retained = state.Evidence.First();
        state.Characters.Add(new() { ChineseName = "沈夕颜", Status = IntelligenceReviewStatus.Accepted, EvidenceIds = [retained.Id] });
        state.Characters.Add(new() { ChineseName = "风景", Status = IntelligenceReviewStatus.Rejected, EvidenceIds = [retained.Id] });
        retained.AiBlocked = true; retained.AiBlockReason = "SAFETY";
        state.CompletedAnalysisKeys.Add("old-source-only-cache");
        state = await store.SaveAsync(state);
        byte[] manifest = await File.ReadAllBytesAsync(session.ManifestPath);
        var protectedFiles = session.Manifest.Chapters.ToDictionary(e => e.RelativePath, e => File.ReadAllBytes(session.Resolve(e.RelativePath)));
        var resources = await new ProjectResourceStore(session).LoadAsync();
        var locked = new CharacterProfile { ChineseName = "沈夕颜", VietnameseName = "Thẩm Tịch Nhan", Gender = CharacterGender.Female, NarrativePronounLocked = true };
        locked.ApplyGenderDefaults();
        resources.Characters.Profiles.Add(locked);
        resources = await new ProjectResourceStore(session).SaveAsync(resources);
        string resourcesBefore = JsonSerializer.Serialize(resources);
        using var cancel = new CancellationTokenSource();
        var indexer = new ProjectCorpusIndexer(_repository);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => indexer.IndexAsync(session, state, store, resources.Characters,
            new ImmediateProgress<CorpusIndexProgress>(_ => cancel.Cancel()), cancel.Token));
        Assert.NotNull(indexer.LastBackupPath);
        Assert.True(File.Exists(indexer.LastBackupPath));
        var checkpoint = await store.LoadAsync();
        Assert.Single(checkpoint.Chapters, c => c.Evidence.Any(e => e.IsCurrent));
        Assert.DoesNotContain(state.Evidence, e => e.IsCurrent); // caller state not partially mutated
        var resumed = await indexer.IndexAsync(session, checkpoint, store, resources.Characters);
        Assert.Equal(3, (await new CorpusIntegrityAuditor(_repository).AuditAsync(session, resumed)).IndexedChapters);
        Assert.True(resumed.Evidence.Single(e => e.Id == retained.Id).AiBlocked);
        Assert.Equal(state.Characters.Select(c => c.Status), resumed.Characters.Select(c => c.Status));
        Assert.Equal(resourcesBefore, JsonSerializer.Serialize(await new ProjectResourceStore(session).LoadAsync()));
        Assert.Equal(manifest, await File.ReadAllBytesAsync(session.ManifestPath));
        foreach (var file in protectedFiles) Assert.Equal(file.Value, await File.ReadAllBytesAsync(session.Resolve(file.Key)));
        var restored = await CorpusReconciliation.RollbackAsync(store, indexer.LastBackupPath!, resumed.Revision);
        Assert.Equal(checkpoint.Evidence.Count(e => e.IsCurrent), restored.Evidence.Count(e => e.IsCurrent));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CorpusReconciliation.RollbackAsync(store,
            indexer.LastBackupPath!, resumed.Revision));
    }

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
        var index = new CorpusChapterIndex { ChapterId = id, SourceSha256 = hash, SourceLengthUtf16 = source.Length,
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
            new() { ChapterId = id, SourceSha256 = hash, SourceLengthUtf16 = source.Length, Evidence = windows }).Indexed);
    }

    [Fact]
    public async Task Reordering_is_safe_duplicate_identity_is_rejected_and_no_false_complete()
    {
        var (session, store, state) = await Scenario(2);
        session.Manifest.Chapters.Reverse();
        var auditor = new CorpusIntegrityAuditor(_repository);
        Assert.False((await auditor.AuditAsync(session, state)).Complete);
        var ev = state.Evidence.First(); ev.AiBlocked = true; ev.AiAnalyzed = true;
        Assert.True((await auditor.AuditAsync(session, state)).NeedsReindex);
        ev.AiAnalyzed = false;
        Assert.Equal(CorpusCompletion.Blocked, (await auditor.AuditAsync(session, state)).Status);
        session.Manifest.Chapters.Add(session.Manifest.Chapters[0]);
        Assert.True((await auditor.AuditAsync(session, state)).NeedsReindex);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectCorpusIndexer(_repository).IndexAsync(session, state, store, new()));
    }

    [Fact]
    public void Cache_identity_and_only_unapproved_retired_dependencies_are_invalidated()
    {
        var state = new BookIntelligenceState { ProjectId = Guid.NewGuid() };
        var old = new CorpusEvidence { ChapterId = Guid.NewGuid(), SourceSha256 = "hash", IsCurrent = false };
        state.Chapters.Add(new() { ChapterId = old.ChapterId, Evidence = [old] });
        var approved = new KnowledgeRelationship { Status = KnowledgeRelationshipStatus.Confirmed, EvidenceIds = [old.Id] };
        var proposed = new KnowledgeRelationship { Status = KnowledgeRelationshipStatus.Proposed, EvidenceIds = [old.Id] };
        state.RelationshipGraph.AddRange([approved, proposed]);
        state.SceneContexts.Add(new() { ChapterId = old.ChapterId, EvidenceIds = [old.Id] });
        CorpusReconciliation.InvalidateRetiredDependencies(state);
        Assert.Equal(KnowledgeRelationshipStatus.Confirmed, approved.Status);
        Assert.Equal(KnowledgeRelationshipStatus.Stale, proposed.Status);
        Assert.Empty(state.SceneContexts);
        string key = AdaptiveBookIntelligenceAnalyzer.AnalysisKey([old], "model", "v", "context");
        old.Id = Guid.NewGuid();
        Assert.NotEqual(key, AdaptiveBookIntelligenceAnalyzer.AnalysisKey([old], "model", "v", "context"));
        key = AdaptiveBookIntelligenceAnalyzer.AnalysisKey([old], "model", "v", "context"); old.ChapterId = Guid.NewGuid();
        Assert.NotEqual(key, AdaptiveBookIntelligenceAnalyzer.AnalysisKey([old], "model", "v", "context"));
    }

    [Fact]
    public async Task Analyzer_refuses_invalid_corpus_before_calling_any_provider()
    {
        var (session, store, state) = await Scenario(1);
        state.Evidence.Single().IsCurrent = false;
        var client = new NeverCall();
        var resources = await new ProjectResourceStore(session).LoadAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new AdaptiveBookIntelligenceAnalyzer(client).AnalyzeAsync(
            session, state, store, resources, new() { Model = "mock" }));
        Assert.Equal(0, client.Calls);
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
    private sealed class NeverCall : IOllamaClient
    {
        public int Calls;
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Provider must not be called."); }
    }
}
