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

    [Fact]
    public async Task Changed_source_retires_old_provenance_and_only_invalidates_dependent_unapproved_facts()
    {
        var (session, store, state) = await Scenario(2);
        var old = state.Chapters[0].Evidence.Single();
        var unaffected = state.Chapters[1].Evidence.Single();
        var trusted = new KnowledgeRelationship { SubjectCharacterId = Guid.NewGuid(), TargetCharacterId = Guid.NewGuid(),
            Status = KnowledgeRelationshipStatus.Confirmed, EvidenceIds = [old.Id] };
        var pending = new KnowledgeRelationship { SubjectCharacterId = trusted.SubjectCharacterId, TargetCharacterId = trusted.TargetCharacterId,
            Status = KnowledgeRelationshipStatus.Proposed, EvidenceIds = [old.Id] };
        state.RelationshipGraph.AddRange([trusted, pending]);
        state.Characters.Add(new() { ChineseName = "沈夕颜", Status = IntelligenceReviewStatus.Accepted, EvidenceIds = [old.Id] });
        state = await store.SaveAsync(state);
        var chapter = (await _repository.LoadChapterAsync(session, old.ChapterId)).Chapter;
        await _repository.UpdateChapterAsync(session, SourceEditPlanner.Apply(chapter,
            SourceEditPlanner.Preview(chapter, "沈夕颜说道：新的原文。"), true, DateTimeOffset.UtcNow));
        var before = await new CorpusIntegrityAuditor(_repository).AuditAsync(session, state);
        Assert.Contains(before.Chapters[0].Issues, x => x.Code == "SourceChangedOrHashMismatch");
        var after = await new ProjectCorpusIndexer(_repository).IndexAsync(session, state, store, new());
        Assert.False(after.Evidence.Single(x => x.Id == old.Id).IsCurrent);
        Assert.True(after.Evidence.Single(x => x.Id == unaffected.Id).IsCurrent);
        Assert.Equal(KnowledgeRelationshipStatus.Confirmed, after.RelationshipGraph.Single(x => x.Id == trusted.Id).Status);
        Assert.Equal(KnowledgeRelationshipStatus.Stale, after.RelationshipGraph.Single(x => x.Id == pending.Id).Status);
        Assert.Equal(IntelligenceReviewStatus.Accepted, after.Characters.Single().Status);
        Assert.False((await new CorpusIntegrityAuditor(_repository).AuditAsync(session, after)).NeedsReindex);
    }

    [Fact]
    public async Task Empty_ai_findings_are_not_proof_of_verified_identity_coverage()
    {
        var (session, store, state) = await Scenario(1);
        var chapter = (await _repository.LoadChapterAsync(session, state.Chapters[0].ChapterId)).Chapter;
        await _repository.UpdateChapterAsync(session, SourceEditPlanner.Apply(chapter,
            SourceEditPlanner.Preview(chapter, "沈夕颜说道：好。沈夕颜走来。沈夕颜点头。"), true, DateTimeOffset.UtcNow));
        state = await new ProjectCorpusIndexer(_repository).IndexAsync(session, state, store, new());
        state.ScanMode = IntelligenceScanMode.FullSemantic;
        var resources = await new ProjectResourceStore(session).LoadAsync();
        var client = new EmptyFindings();
        state = await new AdaptiveBookIntelligenceAnalyzer(client).AnalyzeAsync(session, state, store, resources, new() { Model = "mock" });
        var integrity = await new CorpusIntegrityAuditor(_repository).AuditAsync(session, state);
        var census = await CharacterMentionCensus.ScanAsync(session, _repository, resources, state);
        Assert.Empty(state.Characters); Assert.True(integrity.Complete);
        Assert.False(CorpusReviewAudit.Build(integrity, census, state).Complete);
        Assert.Contains("NeedsReview", CorpusReviewAudit.Build(integrity, census, state).Summary);
        int requests = client.Calls;
        await new AdaptiveBookIntelligenceAnalyzer(client).AnalyzeAsync(session, state, store, resources, new() { Model = "mock" });
        Assert.Equal(requests, client.Calls);
    }

    [Fact]
    public void Skipped_low_evidence_is_not_analyzed_or_complete_and_thousand_chapter_integrity_smoke()
    {
        string source = new string('风', 1700) + "𠀀" + new string('月', 1700);
        for (int i = 1; i <= 1000; i++)
        {
            Guid id = Guid.NewGuid(); string hash = CorpusIntegrityAuditor.SourceHash(source);
            var chapter = new CorpusChapterIndex { ChapterId = id, ChapterNumber = i,
                SourceSha256 = hash, SourceLengthUtf16 = source.Length, Evidence = ProjectCorpusIndexer.ScanChapter(id, source, hash, [], 0) };
            var audit = CorpusIntegrityAuditor.Inspect(id, i, source, chapter, IntelligenceScanMode.FastAdaptive);
            Assert.True(audit.Indexed); Assert.Equal(CorpusCompletion.Skipped, audit.Status);
            Assert.Equal(0, audit.Analyzed); Assert.True(audit.Skipped > 0);
            Assert.False(new CorpusIntegrityReport(Guid.NewGuid(), 0, [audit], 0).Complete);
        }
    }

    [Fact]
    public async Task Rollback_refuses_to_remove_review_decisions_made_after_backup()
    {
        var (session, store, state) = await Scenario(1);
        string backup = await CorpusReconciliation.BackupAsync(store, state.Revision);
        state.Characters.Add(new() { ChineseName = "沈夕颜", Status = IntelligenceReviewStatus.Accepted, EvidenceIds = [state.Evidence.Single().Id] });
        state = await store.SaveAsync(state);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CorpusReconciliation.RollbackAsync(store, backup, state.Revision));
        Assert.Equal(IntelligenceReviewStatus.Accepted, (await store.LoadAsync()).Characters.Single().Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Review_refuses_mismatched_excerpt_or_inactive_chapter_without_writing_resources(bool inactive)
    {
        var (session, store, state) = await Scenario(1);
        var evidence = state.Evidence.Single();
        if (inactive) session.Manifest.Chapters[0].DeletedAt = DateTimeOffset.UtcNow;
        else evidence.Excerpt = "错误的证据";
        var suggestion = new IntelligenceCharacterSuggestion { ChineseName = "沈夕颜", EvidenceIds = [evidence.Id] };
        state.Characters.Add(suggestion); state = await store.SaveAsync(state);
        var resources = await new ProjectResourceStore(session).LoadAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new BookIntelligenceReviewService(session).AcceptCharacterAsync(
            state, suggestion.Id, new() { ChineseName = "沈夕颜", VietnameseName = "Thẩm Tịch Nhan" }, resources.Revision));
        Assert.False(File.Exists(session.Resolve(session.Manifest.Files.Resources)));
        Assert.Equal(IntelligenceReviewStatus.Pending, (await store.LoadAsync()).Characters.Single().Status);
    }

    [Fact]
    public async Task Retire_then_reactivate_same_hash_repairs_current_coverage_and_keeps_provider_block()
    {
        var (session, store, state) = await Scenario(2);
        var evidence = state.Chapters[1].Evidence.Single();
        evidence.AiBlocked = true; evidence.AiBlockReason = "SAFETY";
        state = await store.SaveAsync(state);
        session.Manifest.Chapters[1].DeletedAt = DateTimeOffset.UtcNow;
        state = await new ProjectCorpusIndexer(_repository).IndexAsync(session, state, store, new());
        Assert.False(state.Evidence.Single(x => x.Id == evidence.Id).IsCurrent);
        session.Manifest.Chapters[1].DeletedAt = null;
        Assert.True((await new CorpusIntegrityAuditor(_repository).AuditAsync(session, state)).NeedsReindex);
        state = await new ProjectCorpusIndexer(_repository).IndexAsync(session, state, store, new());
        var repaired = state.Evidence.Single(x => x.Id == evidence.Id);
        Assert.True(repaired.IsCurrent); Assert.True(repaired.AiBlocked); Assert.False(repaired.AiAnalyzed);
        Assert.Equal("SAFETY", repaired.AiBlockReason);
    }

    [Fact]
    public async Task Exact_199_evidence_fixture_repairs_eleven_active_chapters_without_reclassifying_12_blocks()
    {
        var (session, store, state) = await Scenario(19);
        var corpus = new List<CorpusChapterIndex>();
        int ordinal = 0;
        for (int chapterIndex = 0; chapterIndex < 19; chapterIndex++)
        {
            var entry = session.Manifest.Chapters[chapterIndex];
            int windows = chapterIndex < 7 ? 11 : chapterIndex == 7 ? 10 : chapterIndex == 8 ? 12 : 10;
            string source = new('风', windows * 900);
            var document = (await _repository.LoadChapterAsync(session, entry.ChapterId)).Chapter;
            await _repository.UpdateChapterAsync(session, SourceEditPlanner.Apply(document,
                SourceEditPlanner.Preview(document, source), true, DateTimeOffset.UtcNow));
            string hash = CorpusIntegrityAuditor.SourceHash(source);
            var chapter = new CorpusChapterIndex { ChapterId = entry.ChapterId, ChapterNumber = entry.Number,
                SourceSha256 = hash, SourceLengthUtf16 = source.Length };
            for (int i = 0; i < windows; i++, ordinal++)
                chapter.Evidence.Add(new() { ChapterId = entry.ChapterId, StartUtf16 = i * 900, LengthUtf16 = 900,
                    SourceSha256 = hash, Excerpt = source.Substring(i * 900, 900), IsCurrent = chapterIndex < 8,
                    AiAnalyzed = ordinal < 75, AiBlocked = ordinal is >= 75 and < 87,
                    AiBlockReason = ordinal is >= 75 and < 87 ? "SAFETY" : "" });
            corpus.Add(chapter);
        }
        state.Chapters = corpus; state.ScanMode = IntelligenceScanMode.FullSemantic;
        state.LastError = ""; state.NextEvidenceIndex = 87;
        state = await store.SaveAsync(state);
        Guid[] ids = state.Evidence.Select(x => x.Id).Order().ToArray();
        var auditor = new CorpusIntegrityAuditor(_repository);
        var before = await auditor.AuditAsync(session, state);
        Assert.Equal(199, state.Evidence.Count()); Assert.Equal(87, before.Current);
        Assert.Equal(75, before.Analyzed); Assert.Equal(12, before.Blocked); Assert.Equal(112, before.Stale);
        Assert.Equal(11, before.Missing); Assert.False(before.Complete);
        var repaired = await new ProjectCorpusIndexer(_repository).IndexAsync(session, state, store, new());
        var after = await auditor.AuditAsync(session, repaired);
        Assert.Equal(19, after.IndexedChapters); Assert.Equal(199, after.Current);
        Assert.Equal(75, after.Analyzed); Assert.Equal(12, after.Blocked); Assert.Equal(112, after.Unverified);
        Assert.Equal(0, after.Missing); Assert.Equal(0, after.Stale); Assert.False(after.Complete);
        Assert.Equal(ids, repaired.Evidence.Select(x => x.Id).Order());
    }

    private sealed class EmptyFindings : IOllamaClient
    {
        public int Calls;
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult(new OllamaCompletion("{\"characters\":[],\"relationships\":[],\"glossary\":[]}", true, "stop", new())); }
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
