using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class EntityMemoryIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-memory-b-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();
    private const string Text = "我叫王明。原来是他。后来改名为王宁。被任命为队长。他们结婚。后来离婚。过继之后改口叫叔叔。最后恢复记忆。";
    private async Task<ProjectSession> Create(int chapters = 3)
    {
        var session = await _repository.CreateAsync(_root, "Memory CPU synthetic");
        for (int i = 1; i <= chapters; i++) await _repository.AddChapterAsync(session, TranslationService.CreateChapter("Chapter " + i, Text), i);
        return session;
    }
    private static Dictionary<string, byte[]> Capture(ProjectSession session) => Directory.GetFiles(session.RootDirectory, "*", SearchOption.AllDirectories)
        .Where(x => !x.Contains("abi3-entity-event-memory.v1.json", StringComparison.Ordinal)).ToDictionary(x => x, File.ReadAllBytes);
    private static void Preserved(Dictionary<string, byte[]> before) { foreach (var item in before) Assert.Equal(item.Value, File.ReadAllBytes(item.Key)); }
    private sealed class Progress(Action<MemoryScanProgress> action) : IProgress<MemoryScanProgress> { public void Report(MemoryScanProgress value) => action(value); }

    [Fact]
    public async Task Cpu_scan_checkpoints_resume_and_repeated_imports_are_idempotent_and_do_not_write_legacy_files()
    {
        var session = await Create(); var before = Capture(session); var scanner = new TranslationImpactScanner(); var store = new EntityMemoryStore(session);
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(session, _repository);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.ScanAsync(store,
            sources,
            new ProjectResources { ProjectId = session.Manifest.ProjectId, Characters = new CharacterProject { Id = session.Manifest.ProjectId } },
            new BookIntelligenceState { ProjectId = session.Manifest.ProjectId }, new MissingIdentityReviewState { ProjectId = session.Manifest.ProjectId },
            new Progress(p => { if (p.RescannedChapters == 1) cancel.Cancel(); }), cancel.Token, checkpointBatchSize: 1));
        var partial = await store.LoadAsync(); Assert.Single(partial.Checkpoints); Assert.Equal(8, partial.Events.Count);
        var finished = await scanner.ScanProjectAsync(session, _repository);
        Assert.Equal(3, finished.Checkpoints.Count); Assert.Equal(24, finished.Events.Count);
        Assert.All(partial.Events, e => Assert.Contains(finished.Events, x => x.Id == e.Id));
        var again = await scanner.ScanProjectAsync(session, _repository); Assert.Equal(finished.Revision, again.Revision);
        Assert.Equal(finished.Events.Count, again.Events.Count); Assert.All(again.Events, x => Assert.Equal(MemoryReviewStatus.Proposed, x.InitialStatus));
        Assert.Empty(new EntityMemoryQuery(again, await EntityMemoryProjectInputs.LoadSourcesAsync(session, _repository)).AtChapter(100).Events);
        Preserved(before);
    }

    [Fact]
    public async Task Source_change_preserves_old_events_and_reviews_but_makes_them_stale_on_refresh()
    {
        var session = await Create(1); var scanner = new TranslationImpactScanner(); var state = await scanner.ScanProjectAsync(session, _repository);
        var original = state.Events.First(); var service = new EntityMemoryReviewService(session, _repository);
        state = await service.ReviewAsync(original.Id, MemoryReviewStatus.Accepted, original.Timeline, state.Revision, "Review only, not confirmed");
        string oldReview = JsonSerializer.Serialize(state.Reviews);
        var chapter = (await _repository.LoadChapterAsync(session, session.Manifest.Chapters[0].ChapterId)).Chapter;
        await _repository.UpdateChapterAsync(session, SourceEditPlanner.Apply(chapter, SourceEditPlanner.Preview(chapter, Text + "原文新增了一句。"), true, DateTimeOffset.UtcNow));
        state = await scanner.ScanProjectAsync(session, _repository);
        Assert.Contains(state.Events, x => x.Id == original.Id);
        var query = new EntityMemoryQuery(state, await EntityMemoryProjectInputs.LoadSourcesAsync(session, _repository));
        Assert.Equal(MemoryReviewStatus.Stale, query.Status(original.Id)); Assert.Equal(oldReview, JsonSerializer.Serialize(state.Reviews));
        Assert.Equal(16, state.Events.Count); Assert.Single(state.Checkpoints);
    }

    [Fact]
    public async Task Explicit_event_refinement_and_append_only_rollback_survive_reopen()
    {
        var session = await Create(1); var state = await new TranslationImpactScanner().ScanProjectAsync(session, _repository);
        var source = state.Events.First(); var service = new EntityMemoryReviewService(session, _repository);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReviewAsync(source.Id, MemoryReviewStatus.Confirmed,
            new(1, null, 1, 1), state.Revision, "Cannot confirm keyword participants implicitly"));
        Guid identity = state.Entities.First(x => x.Kind != MemoryEntityKind.AddressingCatalogue).Id;
        state = await service.RefineEventAsync(source.Id, source.Type, MemoryEventEffect.None, [identity], [], new(1, null, 1, 1),
            MemoryReviewStatus.Confirmed, state.Revision, "Human checked the actual participants and reveal");
        var confirmed = state.Events.Single(x => x.Provenance.Origin == MemoryOrigin.User);
        var decision = state.Reviews.Last(x => x.TargetId == confirmed.Id);
        var before = state.Reviews.ToArray();
        state = await service.RollbackAsync(decision.Id, state.Revision, "Undo confirmation; keep all history");
        Assert.Equal(before, state.Reviews.Take(before.Length)); Assert.Equal(decision.Id, state.Reviews.Last().RevertsDecisionId);
        var reopened = await new EntityMemoryStore(session).LoadAsync();
        Assert.Empty(new EntityMemoryQuery(reopened, await EntityMemoryProjectInputs.LoadSourcesAsync(session, _repository)).AtChapter(1).Events);
        Assert.Equal(MemoryReviewStatus.Proposed, reopened.Reviews.Last().Status);
    }

    [Fact]
    public async Task Audit_link_requires_separate_alias_promotion_and_never_changes_project_resources()
    {
        var session = await Create(1); var resourceStore = new ProjectResourceStore(session); var resources = await resourceStore.LoadAsync();
        var character = new CharacterProfile { ChineseName = "王明", VietnameseName = "Vương Minh", Gender = CharacterGender.Male, NarrativePronounLocked = true };
        character.ApplyGenderDefaults(); resources.Characters.Profiles.Add(character); resources = await resourceStore.SaveAsync(resources);
        var chapter = (await _repository.LoadChapterAsync(session, session.Manifest.Chapters[0].ChapterId)).Chapter;
        string text = "我叫王明。小明的信还在。小明说道：好。小明来了。";
        await _repository.UpdateChapterAsync(session, SourceEditPlanner.Apply(chapter, SourceEditPlanner.Preview(chapter, text), true, DateTimeOffset.UtcNow));
        var survey = await new BookIntelligenceStore(session).LoadAsync(); var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(session, _repository);
        var census = CharacterMentionCensus.Scan(resources.ProjectId, survey.Revision, sources, resources, survey);
        await new MissingIdentityReviewStore(session).DecideAsync(census, "小明", MissingIdentityDecisionKind.LinkExisting, 0, resources.Revision,
            character.Id, MissingIdentityLinkKind.AliasOrTitle, "Audit link, not a translation alias");
        var protectedFiles = Capture(session);
        var state = await new TranslationImpactScanner().ScanProjectAsync(session, _repository);
        var link = state.Facts.Single(x => x.Provenance.Origin == MemoryOrigin.AuditOnly);
        var service = new EntityMemoryReviewService(session, _repository);
        state = await service.ReviewAsync(link.Id, MemoryReviewStatus.Confirmed, new(1, null, 1, 1), state.Revision, "Confirm audit association only");
        Assert.DoesNotContain(new EntityMemoryQuery(state, sources).AtChapter(1).Entities.SelectMany(x => x.Facts), x => x.Value == "小明");
        state = await service.PromoteAuditAliasAsync(link.Id, MemoryFactKind.Alias, new(1, null, 1, 1), state.Revision, "Separate explicit alias confirmation");
        Assert.Contains(new EntityMemoryQuery(state, sources).AtChapter(1).Entities.SelectMany(x => x.Facts), x => x.Kind == MemoryFactKind.Alias && x.Value == "小明");
        Assert.DoesNotContain((await resourceStore.LoadAsync()).Characters.Profiles.Single().Aliases, x => x == "小明");
        Preserved(protectedFiles);
    }

    [Fact]
    public void Legacy_profile_and_candidate_with_same_raw_guid_are_still_distinct_handles()
    {
        Guid project = Guid.NewGuid(), collision = Guid.NewGuid();
        var resources = new ProjectResources { ProjectId = project, Characters = new CharacterProject { Id = project,
            Profiles = [new CharacterProfile { Id = collision, ChineseName = "林安", VietnameseName = "Lâm An" }] } };
        var legacy = new BookIntelligenceState { ProjectId = project, CandidateIdentities = [new CandidateIdentity { Id = collision, ChineseName = "林宁", ProposedExistingCharacterId = collision }] };
        var sources = new[] { new CensusSource(Guid.NewGuid(), 1, "我叫林安。她名叫林宁。") };
        var census = CharacterMentionCensus.Scan(project, 0, sources, resources, legacy);
        var memory = new EntityMemoryState { ProjectId = project };
        EntityMemoryProjection.Import(memory, resources, legacy, census, new() { ProjectId = project }); memory.Validate();
        var association = memory.Facts.Single(x => x.Kind == MemoryFactKind.IdentityAssociation);
        Assert.NotEqual(association.SubjectId, association.TargetId);
        int count = memory.Facts.Count; EntityMemoryProjection.Import(memory, resources, legacy, census, new() { ProjectId = project });
        Assert.Equal(count, memory.Facts.Count);
    }

    [Fact]
    public async Task Fresh_manifest_read_not_stale_session_controls_deleted_chapter_freshness()
    {
        var session = await Create(1); var other = (await _repository.OpenAsync(session.ManifestPath)).Session;
        var state = await new TranslationImpactScanner().ScanProjectAsync(session, _repository);
        await _repository.SoftDeleteChapterAsync(other, other.Manifest.Chapters[0].ChapterId);
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(session, _repository);
        Assert.Empty(sources); Assert.Equal(MemoryReviewStatus.Stale, new EntityMemoryQuery(state, sources).Status(state.Events[0].Id));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
