using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class MissingIdentityReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-p01-identity-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();
    private ProjectSession _session = null!;
    private BookIntelligenceStore _survey = null!;
    private MissingIdentityReviewStore _reviews = null!;
    private ProjectResources _resources = null!;

    [Fact]
    public async Task Repeated_false_positive_stays_resolved_after_reopen_with_history_and_no_quality_claim()
    {
        await Scenario("自称普通人。自称普通人。自称普通人。");
        var before = Capture(); var census = await Census();
        Assert.Equal("普通人", Assert.Single(census.MissingIdentities).ChineseName);
        var saved = await _reviews.DecideAsync(census, "普通人", MissingIdentityDecisionKind.NotCharacter, 0, _resources.Revision, note: "Không phải tên người");
        _session = (await _repository.OpenAsync(_session.ManifestPath)).Session;
        _reviews = new(_session);
        var reopened = await _reviews.LoadAsync();
        Assert.Equal(saved.History.Single().Id, reopened.History.Single().Id);
        Assert.Equal(3, reopened.History.Single().Mentions.Count);
        Assert.Equal("Không phải tên người", reopened.History.Single().Note);
        var audit = await Audit(reopened);
        Assert.DoesNotContain(audit.Items, x => x.Group == "Missing identity");
        Assert.Contains(audit.Items, x => x.Group == "Identity đã xử lý" && x.MissingIdentityName == "普通人");
        Assert.True(audit.Complete); Assert.True(audit.ManualReviewComplete);
        Assert.False(audit.AccuracyVerified); Assert.False(audit.Integrity.Verified);
        Assert.Contains("Accuracy: chưa được kiểm chứng", audit.Summary);
        Assert.DoesNotContain("/ Verified", audit.Summary);
        AssertPreserved(before);
    }

    [Theory]
    [InlineData(MissingIdentityLinkKind.Identity)]
    [InlineData(MissingIdentityLinkKind.AliasOrTitle)]
    public async Task Ambiguous_title_requires_explicit_target_and_link_kind_without_adding_project_alias(MissingIdentityLinkKind linkKind)
    {
        await Scenario("王小姐走来。王小姐点头。王小姐说道：好。");
        var before = Capture(); var census = await Census();
        await Assert.ThrowsAsync<ArgumentException>(() => _reviews.DecideAsync(census, "王小姐",
            MissingIdentityDecisionKind.LinkExisting, 0, _resources.Revision));
        Guid target = _resources.Characters.Profiles[1].Id;
        var saved = await _reviews.DecideAsync(census, "王小姐", MissingIdentityDecisionKind.LinkExisting, 0,
            _resources.Revision, target, linkKind);
        Assert.Equal(target, saved.History.Single().TargetCharacterId);
        Assert.Equal(linkKind, saved.History.Single().LinkKind);
        var audit = await Audit(await new MissingIdentityReviewStore(_session).LoadAsync());
        Assert.Equal(0, audit.IdentityReviewsPending); Assert.True(audit.ManualReviewComplete);
        Assert.DoesNotContain(_resources.Characters.Profiles.SelectMany(x => x.Aliases), x => x == "王小姐");
        AssertPreserved(before);
    }

    [Fact]
    public async Task New_character_queue_survives_restart_and_requires_explicit_resolution()
    {
        await Scenario("沈夕颜说道：好。沈夕颜走来。沈夕颜点头。");
        var before = Capture(); var census = await Census();
        var queued = await _reviews.DecideAsync(census, "沈夕颜", MissingIdentityDecisionKind.VerifyNewCharacter, 0, _resources.Revision);
        var reopened = await new MissingIdentityReviewStore(_session).LoadAsync();
        var pending = await Audit(reopened);
        Assert.True(pending.Integrity.CorpusProcessed); Assert.True(pending.Integrity.AiSurveyComplete);
        Assert.False(pending.ManualReviewComplete); Assert.False(pending.Complete);
        Assert.Contains(pending.Items, x => x.Group == "Unverified" && x.Title.Contains("Chờ xác minh nhân vật mới"));
        var resolved = await _reviews.DecideAsync(await Census(), "沈夕颜", MissingIdentityDecisionKind.LinkExisting,
            queued.Revision, _resources.Revision, _resources.Characters.Profiles[0].Id, MissingIdentityLinkKind.Identity);
        Assert.Equal(2, resolved.History.Count);
        Assert.Equal(queued.History.Single().Id, resolved.History[1].SupersedesId);
        Assert.Equal(MissingIdentityDecisionKind.VerifyNewCharacter, resolved.History[0].Kind);
        Assert.True((await Audit(resolved)).ManualReviewComplete);
        AssertPreserved(before);
    }

    [Fact]
    public async Task Changed_source_invalidates_affected_decision_rejects_stale_submit_and_keeps_audit_history()
    {
        await Scenario("沈夕颜说道：好。沈夕颜走来。沈夕颜点头。");
        var observed = await Census();
        var saved = await _reviews.DecideAsync(observed, "沈夕颜", MissingIdentityDecisionKind.NotCharacter, 0, _resources.Revision);
        await Edit(0, "𠀀沈夕颜。沈夕颜。沈夕颜。"); // no discovery cue; reviewed name must still be counted
        await Assert.ThrowsAsync<InvalidOperationException>(() => _reviews.DecideAsync(observed, "沈夕颜",
            MissingIdentityDecisionKind.VerifyNewCharacter, saved.Revision, _resources.Revision));
        var fresh = await Census();
        Assert.False(MissingIdentityReviewStore.IsCurrent(saved.History.Single(), fresh, _resources));
        var audit = await Audit(saved);
        Assert.Contains(audit.Items, x => x.Group == "Missing identity" && x.Title.Contains("Cần xác minh lại"));
        Assert.False(audit.ManualReviewComplete);
        var reaffirmed = await _reviews.DecideAsync(fresh, "沈夕颜", MissingIdentityDecisionKind.NotCharacter, saved.Revision, _resources.Revision);
        Assert.Equal(2, reaffirmed.History.Count);
        Assert.True(MissingIdentityReviewStore.IsCurrent(reaffirmed.History.Last(), fresh, _resources));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(saved.History.Single()),
            System.Text.Json.JsonSerializer.Serialize(reaffirmed.History.First()));
    }

    [Fact]
    public async Task Unrelated_source_edit_preserves_decision_but_new_mentions_or_changed_target_require_review()
    {
        await Scenario("王小姐走来。王小姐点头。王小姐说道：好。");
        var saved = await _reviews.DecideAsync(await Census(), "王小姐", MissingIdentityDecisionKind.LinkExisting, 0,
            _resources.Revision, _resources.Characters.Profiles[0].Id, MissingIdentityLinkKind.AliasOrTitle);
        await Edit(1, "月光落在地面上。");
        Assert.True(MissingIdentityReviewStore.IsCurrent(saved.History.Single(), await Census(), _resources));
        await Edit(1, "王小姐在这里。");
        Assert.False(MissingIdentityReviewStore.IsCurrent(saved.History.Single(), await Census(), _resources));
        await Edit(1, "月光落在地面上。");
        _resources.Characters.Profiles[0].ChineseName = "王新名";
        _resources = await new ProjectResourceStore(_session).SaveAsync(_resources);
        Assert.False(MissingIdentityReviewStore.IsCurrent(saved.History.Single(), await Census(), _resources));
        Assert.Single((await _reviews.LoadAsync()).History);
    }

    [Fact]
    public async Task Cancellation_and_revision_conflict_do_not_overwrite_decisions()
    {
        await Scenario("自称普通人。自称普通人。自称普通人。");
        var census = await Census();
        var saved = await _reviews.DecideAsync(census, "普通人", MissingIdentityDecisionKind.NotCharacter, 0, _resources.Revision);
        byte[] before = await File.ReadAllBytesAsync(_reviews.PathName);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _reviews.DecideAsync(census, "普通人",
            MissingIdentityDecisionKind.VerifyNewCharacter, 0, _resources.Revision));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _reviews.DecideAsync(census, "普通人",
            MissingIdentityDecisionKind.VerifyNewCharacter, saved.Revision, _resources.Revision, cancellationToken: cancel.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(_reviews.PathName));
    }

    [Fact]
    public void Operational_completion_is_separate_from_ai_review_and_accuracy()
    {
        Guid id = Guid.NewGuid();
        var indexed = new ChapterIntegrityAudit(id, 1, true, "hash", 1, [], 1, 0, 0, 0, 1, 0);
        var report = new CorpusIntegrityReport(Guid.NewGuid(), 0, [indexed], 0);
        Assert.True(report.CorpusProcessed); Assert.False(report.AiSurveyComplete); Assert.False(report.ManualReviewComplete);
        report = report with { Chapters = [indexed with { Analyzed = 1, Unverified = 0 }], PendingReviews = 1 };
        Assert.True(report.AiSurveyComplete); Assert.False(report.ManualReviewComplete);
        report = report with { PendingReviews = 0 };
        Assert.True(report.Complete); Assert.False(report.Verified); Assert.False(report.AccuracyVerified);
        Assert.Contains("chưa được kiểm chứng", report.Summary);
    }

    private async Task Scenario(string source)
    {
        _session = await _repository.CreateAsync(_root, "P0.1 synthetic");
        foreach (string text in new[] { source, "风吹过山谷。" })
            await _repository.AddChapterAsync(_session, TranslationService.CreateChapter("Chương", text), _session.Manifest.Chapters.Count + 1, "章节");
        var resourceStore = new ProjectResourceStore(_session);
        _resources = await resourceStore.LoadAsync();
        foreach (string chinese in new[] { "王秀兰", "王悦" })
        {
            var character = new CharacterProfile { ChineseName = chinese, VietnameseName = chinese, Gender = CharacterGender.Female, NarrativePronounLocked = true };
            character.ApplyGenderDefaults(); _resources.Characters.Profiles.Add(character);
        }
        _resources = await resourceStore.SaveAsync(_resources);
        _survey = new(_session); _reviews = new(_session);
        var state = await new ProjectCorpusIndexer(_repository).IndexAsync(_session, await _survey.LoadAsync(), _survey, _resources.Characters);
        state.ScanMode = IntelligenceScanMode.FullSemantic;
        foreach (var evidence in state.Evidence) evidence.AiAnalyzed = true;
        await _survey.SaveAsync(state);
    }
    private async Task<CharacterCensusReport> Census() => await CharacterMentionCensus.ScanAsync(_session, _repository,
        _resources, await _survey.LoadAsync(), reviewNames: (await _reviews.LoadAsync()).Latest.Select(x => x.ChineseName).ToArray());
    private async Task<CorpusReviewAudit> Audit(MissingIdentityReviewState journal)
    {
        var state = await _survey.LoadAsync();
        return CorpusReviewAudit.Build(await new CorpusIntegrityAuditor(_repository).AuditAsync(_session, state),
            await Census(), state, journal, _resources);
    }
    private async Task Edit(int index, string source)
    {
        var chapter = (await _repository.LoadChapterAsync(_session, _session.Manifest.Chapters[index].ChapterId)).Chapter;
        await _repository.UpdateChapterAsync(_session, SourceEditPlanner.Apply(chapter, SourceEditPlanner.Preview(chapter, source), true, DateTimeOffset.UtcNow));
    }
    private Dictionary<string, byte[]> Capture() => new[] { _session.ManifestPath, _survey.PathName, _session.Resolve(_session.Manifest.Files.Resources) }
        .Concat(_session.Manifest.Chapters.Select(x => _session.Resolve(x.RelativePath))).ToDictionary(x => x, File.ReadAllBytes);
    private static void AssertPreserved(Dictionary<string, byte[]> files) { foreach (var file in files) Assert.Equal(file.Value, File.ReadAllBytes(file.Key)); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
