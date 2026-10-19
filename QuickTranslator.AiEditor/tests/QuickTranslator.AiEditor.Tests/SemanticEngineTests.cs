using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

internal sealed class SemanticFake(Func<string, CancellationToken, Task<OllamaCompletion>> respond) : IJsonSchemaOllamaClient
{
    public List<string> Prompts { get; } = [];
    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => TranslateStructuredAsync(prompt, options, default, ct);
    public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
    { Prompts.Add(prompt); return respond(prompt, ct); }
    public static OllamaCompletion Completion(SemanticResponse response) => new(SemanticFixture.Json(response), true, "stop", new() { InputTokens = 100, OutputTokens = 30 });
}

public sealed class SemanticEngineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-semantic-b-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();
    private async Task<(ProjectSession Session, SemanticFixture Fixture, SemanticSurveyStore Store)> Create(int chapters = 3)
    {
        var session = await _repository.CreateAsync(_root, "Semantic mock only"); var fixture = new SemanticFixture(); fixture.Memory.ProjectId = session.Manifest.ProjectId; fixture.Sources.Clear();
        for (int i = 1; i <= chapters; i++)
        {
            var chapter = TranslationService.CreateChapter("Mock " + i, "林青说道：我叫林青。周宁是我的父亲。"); await _repository.AddChapterAsync(session, chapter, i);
            fixture.Sources.Add(new(chapter.Id, i, chapter.OriginalText));
        }
        return (session, fixture, new(session));
    }
    private static SemanticFake Names(SemanticFixture fixture, IReadOnlyList<SemanticSurveyRequest> plan) => new((prompt, _) =>
    { var r = plan.Single(x => x.Prompt == prompt); return Task.FromResult(SemanticFake.Completion(SemanticFixture.Response(r, fixture.Name(r) with { EffectiveChapter = r.ChapterNumber, RevealedChapter = r.ChapterNumber }))); });
    private static BookIntelligenceState Legacy(SemanticFixture fixture) => new() { ProjectId = fixture.Memory.ProjectId };

    [Fact]
    public async Task Dry_run_order_budget_resume_idempotency_and_staging_do_not_write_memory_resources_or_excerpts()
    {
        var (session, s, store) = await Create(); var before = Directory.GetFiles(_root, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
        var plan = s.Plan(); var fake = Names(s, plan); var legacy = Legacy(s);
        var dry = SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), legacy); Assert.Equal(3, dry.PendingRequests); Assert.Empty(fake.Prompts); Assert.False(File.Exists(store.PathName));
        var state = await new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, legacy, s.Options, new(1), dry.Fingerprint);
        Assert.Single(state.Proposals); Assert.Equal(SemanticBatchStatus.Pending, state.Status(plan[1].Id));
        state = await new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, legacy, s.Options, new(), dry.Fingerprint);
        Assert.Equal(plan.Select(x => x.Prompt), fake.Prompts); Assert.Equal(3, state.Proposals.Count); Assert.Empty(state.Reviews);
        long revision = state.Revision; state = await new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, legacy, s.Options, new(), dry.Fingerprint);
        Assert.Equal(revision, state.Revision); Assert.Equal(3, fake.Prompts.Count);
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
        Assert.Empty((await new EntityMemoryStore(session).LoadAsync()).Facts);
        Assert.DoesNotContain(s.Sources[0].OriginalText, Encoding.UTF8.GetString(SemanticSurveyStore.Serialize(state)));
        Assert.All(state.Proposals, p => Assert.All(p.Audit.Finding.SupportSpans, span => Assert.Empty(span.Text)));
    }

    [Fact]
    public async Task Provider_block_survives_model_change_and_does_not_route_or_count_as_validated()
    {
        var (_, s, store) = await Create(); var plan = s.Plan(); int calls = 0;
        var fake = new SemanticFake((prompt, _) => { if (calls++ == 0) throw new GeminiSurveyBlockedException("SAFETY"); return Task.FromResult(SemanticFake.Completion(SemanticFixture.Response(plan.Single(x => x.Prompt == prompt)))); });
        var legacy = Legacy(s); string fingerprint = SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), legacy).Fingerprint;
        var state = await new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, legacy, s.Options, new(), fingerprint);
        Assert.Equal(3, calls); Assert.Equal(SemanticBatchStatus.Blocked, state.Status(plan[0].Id)); Assert.Equal(SemanticBatchStatus.Validated, state.Status(plan[1].Id));
        var changedOptions = s.Options with { Model = "other-explicit-model" };
        var changed = SemanticSurveyRequests.Plan(s.Memory, s.Sources, legacy, "explicit:other", changedOptions);
        var next = Names(s, changed); var dry = SemanticSurveyEngine.DryRun(changed, state, legacy); Assert.Equal(1, dry.BlockedBatches);
        state = await new SemanticSurveyEngine(next).RunAsync(store, changed, s.Sources, legacy, changedOptions, new(), dry.Fingerprint);
        Assert.Equal(2, next.Prompts.Count); Assert.DoesNotContain(changed[0].Prompt, next.Prompts); Assert.Equal(SemanticBatchStatus.Blocked, state.Status(changed[0].Id));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("timeout")]
    [InlineData("quota")]
    public async Task Errors_keep_evidence_and_distinct_failed_status_with_no_unbounded_retry(string kind)
    {
        var (_, s, store) = await Create(); var plan = s.Plan();
        var fake = new SemanticFake((_, _) => kind switch { "schema" => Task.FromResult(new OllamaCompletion("{}", true, "stop", new())),
            "timeout" => throw new OperationCanceledException(), _ => throw new GeminiDailyQuotaExceededException() });
        var state = await new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, Legacy(s), s.Options, new(2), SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), Legacy(s)).Fingerprint);
        Assert.Equal(kind == "quota" ? 1 : 2, fake.Prompts.Count); Assert.Equal(3, state.Evidence.Count); Assert.Empty(state.Proposals);
        Assert.Equal(SemanticBatchStatus.Failed, state.Status(plan[0].Id)); Assert.Equal(SemanticBatchStatus.Pending, state.Status(plan[^1].Id));
    }

    [Fact]
    public async Task Cancellation_preserves_completed_proposals_and_inflight_attempt_resume_does_not_duplicate()
    {
        var (_, s, store) = await Create(); var plan = s.Plan(); using var cancel = new CancellationTokenSource(); int calls = 0;
        var fake = new SemanticFake((prompt, ct) => { if (calls++ == 1) { cancel.Cancel(); throw new OperationCanceledException(ct); }
            var r = plan.Single(x => x.Prompt == prompt); return Task.FromResult(SemanticFake.Completion(SemanticFixture.Response(r, s.Name(r)))); });
        string fp = SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), Legacy(s)).Fingerprint;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, Legacy(s), s.Options, new(), fp, ct: cancel.Token));
        Assert.Single((await store.LoadAsync()).Proposals);
        var next = Names(s, plan); var state = await new SemanticSurveyEngine(next).RunAsync(store, plan, s.Sources, Legacy(s), s.Options, new(), fp);
        Assert.Equal(2, next.Prompts.Count); Assert.Equal(3, state.Proposals.Count);
    }

    [Fact]
    public async Task Explicit_confirmation_undo_reopen_and_second_file_failure_are_idempotent_and_do_not_merge_local_identities()
    {
        var (session, s, store) = await Create(1); var plan = s.Plan(); var fake = Names(s, plan);
        var state = await new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, Legacy(s), s.Options, new(), SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), Legacy(s)).Fingerprint);
        var proposal = state.Proposals.Single(); var service = new SemanticMemoryReviewService(session, _repository); var edit = new SemanticPromotion(new(1, null, 1, 1), "Human checked source", new Dictionary<string, Guid>());
        state = await service.ReviewAsync(proposal.Id, MemoryReviewStatus.Accepted, edit, state.Revision); Assert.Empty((await new EntityMemoryStore(session).LoadAsync()).Facts);
        int flush = 0; SimpleFileStorage.FaultPoint.Value = _ => { if (++flush == 2) throw new IOException("Crash after memory commit before semantic journal commit"); };
        try { await Assert.ThrowsAsync<IOException>(() => service.ReviewAsync(proposal.Id, MemoryReviewStatus.Confirmed, edit, state.Revision)); }
        finally { SimpleFileStorage.FaultPoint.Value = null; }
        var memory = await new EntityMemoryStore(session).LoadAsync(); Assert.Single(memory.Facts); Assert.Single(memory.Reviews);
        state = await service.ReviewAsync(proposal.Id, MemoryReviewStatus.Confirmed, edit, state.Revision);
        Assert.Single((await new EntityMemoryStore(session).LoadAsync()).Facts); Assert.Single((await new EntityMemoryStore(session).LoadAsync()).Reviews);
        Assert.NotEmpty(new EntityMemoryQuery(memory, s.Sources).AtChapter(1).Entities);
        state = await service.UndoAsync(proposal.Id, state.Revision, "Explicit undo"); memory = await new EntityMemoryStore(session).LoadAsync();
        Assert.Empty(new EntityMemoryQuery(memory, s.Sources).AtChapter(1).Entities); Assert.Equal(3, state.Reviews.Count); Assert.Equal(2, memory.Reviews.Count);
        Assert.Equal(state.Revision, (await new SemanticSurveyStore(session).LoadAsync()).Revision);
    }

    [Fact]
    public async Task Changed_source_invalidates_only_affected_batch_and_rejects_old_promotion()
    {
        var (session, s, store) = await Create(2); var plan = s.Plan(); var legacy = Legacy(s);
        var state = await new SemanticSurveyEngine(Names(s, plan)).RunAsync(store, plan, s.Sources, legacy, s.Options, new(), SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), legacy).Fingerprint);
        var chapter = (await _repository.LoadChapterAsync(session, s.Sources[0].ChapterId)).Chapter;
        string changed = chapter.OriginalText + "原文新增。"; await _repository.UpdateChapterAsync(session, SourceEditPlanner.Apply(chapter, SourceEditPlanner.Preview(chapter, changed), true, DateTimeOffset.UtcNow));
        s.Sources[0] = s.Sources[0] with { OriginalText = changed }; var next = s.Plan();
        Assert.NotEqual(plan[0].Id, next[0].Id); Assert.Equal(plan[1].Id, next[1].Id); Assert.Equal(1, SemanticSurveyEngine.DryRun(next, state, legacy).PendingRequests);
        var old = state.Proposals.Single(x => x.BatchId == plan[0].Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SemanticMemoryReviewService(session, _repository).ReviewAsync(old.Id, MemoryReviewStatus.Confirmed, new(new(1, null, 1, 1), "Stale rejected", new Dictionary<string, Guid>()), state.Revision));
    }

    [Fact]
    public async Task Revision_guards_process_run_lease_and_history_preserve_sidecar()
    {
        var (_, s, store) = await Create(1); var plan = s.Plan();
        var state = await new SemanticSurveyEngine(Names(s, plan)).RunAsync(store, plan, s.Sources, Legacy(s), s.Options, new(), SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), Legacy(s)).Fingerprint);
        byte[] before = await File.ReadAllBytesAsync(store.PathName);
        using (store.AcquireRunLease()) await Assert.ThrowsAsync<IOException>(() => new SemanticSurveyEngine(Names(s, plan)).RunAsync(store, plan, s.Sources, Legacy(s), s.Options, new(), "x"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(0, _ => { }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateAsync(state.Revision, x => x.Attempts.Clear()));
        Assert.Equal(before, await File.ReadAllBytesAsync(store.PathName));
    }

    [Fact]
    public async Task Competing_semantic_writers_cannot_lose_review_decisions_and_changed_inputs_send_nothing()
    {
        var (_, s, store) = await Create(1); var plan = s.Plan(); var fake = Names(s, plan); string fp = SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), Legacy(s)).Fingerprint;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, Legacy(s), s.Options, new(), fp, current: _ => Task.FromResult(false)));
        Assert.Empty(fake.Prompts);
        var state = await new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, Legacy(s), s.Options, new(), fp);
        async Task<bool> Write(string note)
        {
            try { await store.UpdateAsync(state.Revision, x => x.Reviews.Add(new(Guid.NewGuid(), state.Proposals.Single().Id, MemoryReviewStatus.Accepted, DateTimeOffset.UtcNow, note, new()))); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Write("one")), Task.Run(() => Write("two")));
        Assert.Single(results, x => x); Assert.Single((await store.LoadAsync()).Reviews);
    }

    [Fact]
    public async Task Undo_recovers_an_interrupted_confirmation_even_without_a_journal_memory_link()
    {
        var (session, s, store) = await Create(1); var plan = s.Plan();
        var state = await new SemanticSurveyEngine(Names(s, plan)).RunAsync(store, plan, s.Sources, Legacy(s), s.Options, new(), SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), Legacy(s)).Fingerprint);
        Guid proposal = state.Proposals.Single().Id; var service = new SemanticMemoryReviewService(session, _repository);
        var edit = new SemanticPromotion(new(1, null, 1, 1), "Human confirmation", new Dictionary<string, Guid>());
        state = await service.ReviewAsync(proposal, MemoryReviewStatus.Accepted, edit, state.Revision);
        int writes = 0; SimpleFileStorage.FaultPoint.Value = _ => { if (++writes == 2) throw new IOException("Interrupted journal"); };
        try { await Assert.ThrowsAsync<IOException>(() => service.ReviewAsync(proposal, MemoryReviewStatus.Confirmed, edit, state.Revision)); }
        finally { SimpleFileStorage.FaultPoint.Value = null; }
        state = await service.UndoAsync(proposal, state.Revision, "Undo interrupted confirmation");
        Assert.Empty(new EntityMemoryQuery(await new EntityMemoryStore(session).LoadAsync(), s.Sources).AtChapter(1).Entities);
        Assert.Equal(MemoryReviewStatus.Proposed, state.Reviews.Last().Status);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
