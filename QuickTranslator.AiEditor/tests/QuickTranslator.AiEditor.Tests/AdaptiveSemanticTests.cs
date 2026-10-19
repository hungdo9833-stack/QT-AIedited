using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

public sealed class AdaptiveSemanticTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-adaptive-" + Guid.NewGuid().ToString("N"));
    internal static string Windows(int count) => string.Concat(Enumerable.Range(0, count).Select(i => Window("林青说道：我叫林青。周宁是林青的亲生父亲。")));
    internal static string Window(string prefix) => prefix + new string('云', 900 - prefix.Length);
    internal static IReadOnlyList<SemanticSurveyRequest> Plan(SemanticFixture s, int count = 4, int reserve = int.MaxValue) =>
        SemanticSurveyRequests.Plan(s.Memory, s.Sources, new() { ProjectId = s.Memory.ProjectId }, "mock:selected", s.Options,
            batching: new(count, reserve));
    internal static SemanticFinding Name(SemanticSurveyRequest unit, string name = "林青", string? local = null) => new()
    {
        FindingType = SemanticFindingType.CharacterIdentification, IdentityResolutionStatus = SemanticResolution.NewIdentity,
        SubjectIdentity = new(null, local ?? "p_" + unit.Evidence.Id.ToString("N"), name, MemoryEntityKind.Character, SemanticResolution.NewIdentity),
        FactKind = MemoryFactKind.PrimaryName, Value = name, SourceEvidenceIds = [unit.Evidence.Id],
        SupportSpans = [new(unit.Evidence.Id, unit.Evidence.StartUtf16 + unit.EvidenceText.IndexOf(name, StringComparison.Ordinal), name.Length, name)],
        EffectiveChapter = unit.ChapterNumber, RevealedChapter = unit.ChapterNumber, Confidence = .8, Reason = "Labelled mock source, no name-based merge"
    };
    internal static SemanticResponse Response(SemanticSurveyRequest r, params SemanticFinding[] findings) =>
        new(r.WireVersion, r.ProjectId, r.Evidence.ChapterId, r.Units.Select(x => x.Evidence.Id).ToArray(), findings);
    internal static SemanticResponse FromPrompt(string prompt, IReadOnlyList<SemanticSurveyRequest> atomic, bool names = true)
    {
        var ids = Regex.Matches(prompt, @"(?m)^EVIDENCE project=.*? id=([a-fA-F0-9-]{36}) start_utf16=").Select(m => Guid.Parse(m.Groups[1].Value)).ToArray();
        var units = ids.Select(id => atomic.Single(x => x.Evidence.Id == id)).ToArray();
        return new(prompt.Contains("MULTI-EVIDENCE", StringComparison.Ordinal) ? 2 : 1, units[0].ProjectId, units[0].Evidence.ChapterId, ids, names ? units.Select(x => Name(x)).ToArray() : []);
    }
    private async Task<(SemanticFixture S, SemanticSurveyStore Store, ProjectSession Session)> Create(int evidence = 8)
    {
        var repo = new ProjectRepository(); var session = await repo.CreateAsync(_root, "Only synthetic adaptive test");
        var s = new SemanticFixture(Windows(evidence)); s.Memory.ProjectId = session.Manifest.ProjectId;
        var chapter = QuickTranslator.AiEditor.Core.Translation.TranslationService.CreateChapter("Synthetic", s.Sources[0].OriginalText);
        await repo.AddChapterAsync(session, chapter, 1); s.Sources[0] = new(chapter.Id, 1, chapter.OriginalText);
        return (s, new(session), session);
    }
    private static BookIntelligenceState Legacy(SemanticFixture s) => new() { ProjectId = s.Memory.ProjectId };
    private static Task<SemanticSurveyState> Run(SemanticSurveyStore store, SemanticFixture s, IReadOnlyList<SemanticSurveyRequest> plan, SemanticFake fake,
        SemanticSurveyState state, int requests = 50, CancellationToken ct = default) => new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, Legacy(s), s.Options,
            new(requests, 10_000_000), SemanticSurveyEngine.DryRun(plan, state, Legacy(s)).Fingerprint, ct: ct);

    [Fact]
    public void Planner_keeps_all_ids_hashes_ranges_order_and_chapters_and_shares_only_identical_confirmed_snapshots()
    {
        var s = new SemanticFixture(Windows(8)); s.Sources.Add(new(Guid.NewGuid(), 2, Windows(3)));
        var atomic = Plan(s, 1); var grouped = Plan(s);
        Assert.Equal(11, atomic.Count); Assert.Equal(3, grouped.Count); Assert.Equal(new[] { 4, 4, 3 }, grouped.Select(x => x.Units.Count));
        Assert.Equal(atomic.Select(x => (x.Id, x.Evidence)), grouped.SelectMany(x => x.Units).Select(x => (x.Id, x.Evidence)));
        Assert.All(grouped, b => { Assert.Single(b.Units.Select(x => x.SharedContextJson).Distinct()); Assert.Single(b.Units.Select(x => x.Evidence.ChapterId).Distinct()); });
        Assert.True(grouped.Sum(x => x.EstimatedInputTokens) < atomic.Sum(x => x.EstimatedInputTokens));
        Assert.NotEqual(grouped[0].Id, Plan(s, 6)[0].Id);
        var otherContext = atomic[1] with { SharedContextJson = "different confirmed snapshot" };
        Assert.Equal(3, SemanticAdaptiveBatching.Pack([atomic[0], otherContext, atomic[2]], s.Options, new()).Count);
    }

    [Fact]
    public void Planner_respects_complexity_context_and_reserve_without_truncating_evidence()
    {
        var s = new SemanticFixture(Windows(6)); var atomic = Plan(s, 1);
        var complex = SemanticAdaptiveBatching.Pack(atomic.Select(x => x with { Complex = true }), s.Options, new(6));
        Assert.All(complex, x => Assert.InRange(x.Units.Count, 1, 2)); Assert.Equal(6, complex.Sum(x => x.Units.Count));
        var constrained = Plan(s, 6, atomic.Max(x => x.ReservedTokens)); Assert.All(constrained, x => Assert.Single(x.Units));
        var small = s.Options with { ContextSize = 8192, MaximumOutputTokens = 4096 };
        var limited = SemanticSurveyRequests.Plan(s.Memory, s.Sources, Legacy(s), "mock:selected", small);
        Assert.All(limited, x => Assert.True(x.EstimatedInputTokens + small.MaximumOutputTokens <= small.ContextSize));
        Assert.Equal(6, limited.Sum(x => x.Units.Count));
        var tiny = small with { ContextSize = 4608 }; var tinyPlan = SemanticSurveyRequests.Plan(s.Memory, s.Sources, Legacy(s), "mock:selected", tiny);
        Assert.True(SemanticSurveyEngine.DryRun(tinyPlan, new() { ProjectId = s.Memory.ProjectId }, Legacy(s)).OverContextBatches > 0);
    }

    [Fact]
    public void Multi_schema_missing_coverage_cross_evidence_spans_and_foreign_ids_are_independently_audited()
    {
        var s = new SemanticFixture(Windows(4)); var r = Plan(s).Single(); var units = r.Units;
        var good = Response(r, units.Select(x => Name(x)).ToArray());
        Assert.True(SemanticQualityAuditor.Audit(SemanticSurveyContract.Parse(SemanticFixture.Json(good)), r, s.Sources).Validated);
        var partial = good with { ReviewedEvidenceIds = good.ReviewedEvidenceIds.Take(3).ToArray(), Findings = good.Findings.Take(3).ToArray() };
        var checkedPartial = SemanticQualityAuditor.Audit(partial, r, s.Sources);
        Assert.False(checkedPartial.Validated); Assert.Equal(3, checkedPartial.ReviewedEvidenceIds!.Count); Assert.All(checkedPartial.Findings, x => Assert.True(x.EvidenceValid));
        // A range pointing into another excerpt is never trusted: the verbatim quote is re-anchored
        // inside the DECLARED evidence (here it exists there too), or the span is rejected.
        var wrongSpan = Name(units[0]) with { SupportSpans = [Name(units[1]).SupportSpans[0] with { EvidenceId = units[0].Evidence.Id }] };
        var reanchored = Assert.Single(SemanticQualityAuditor.Audit(Response(r, wrongSpan), r, s.Sources).Findings.Single().Finding.SupportSpans);
        Assert.InRange(reanchored.StartUtf16, units[0].Evidence.StartUtf16, units[0].Evidence.StartUtf16 + units[0].Evidence.LengthUtf16 - 2);
        var foreignQuote = Name(units[0]) with { SupportSpans = [new(units[0].Evidence.Id, units[1].Evidence.StartUtf16, 3, "不存在")] };
        Assert.Contains("SupportSpanMismatch", SemanticQualityAuditor.Audit(Response(r, foreignQuote), r, s.Sources).Findings.Single().Issues);
        foreach (var ids in new[] { new[] { Guid.NewGuid() }, new[] { units[0].Evidence.Id, units[0].Evidence.Id } })
        {
            var bad = SemanticQualityAuditor.Audit(good with { ReviewedEvidenceIds = ids }, r, s.Sources);
            Assert.Empty(bad.ReviewedEvidenceIds!); Assert.All(bad.Findings, f => Assert.False(f.EvidenceValid));
        }
        Assert.Throws<InvalidDataException>(() => SemanticSurveyContract.Parse(SemanticFixture.Json(good with { SchemaVersion = 3 })));
        Assert.False(SemanticQualityAuditor.Audit(good with { SchemaVersion = 1 }, r, s.Sources).Validated);
    }

    [Fact]
    public void Homonyms_stay_separate_and_cooccurrence_or_cross_excerpt_speakers_cannot_be_promoted()
    {
        var s = new SemanticFixture(Windows(2)); var r = Plan(s).Single();
        var a = Name(r.Units[0], local: "person_first"); var b = Name(r.Units[1], local: "person_second");
        var separate = SemanticQualityAuditor.Audit(Response(r, a, b), r, s.Sources); Assert.True(separate.Validated);
        Assert.Equal(2, separate.Findings.Select(x => x.Finding.SubjectIdentity!.LocalId).Distinct().Count());
        var collapsed = SemanticQualityAuditor.Audit(Response(r, a, b with { SubjectIdentity = a.SubjectIdentity }), r, s.Sources);
        Assert.All(collapsed.Findings, f => { Assert.False(f.CanPromote); Assert.Contains("CrossEvidenceIdentityNeedsReview", f.Issues); });
        var u = r.Units[0]; var second = Name(r.Units[1], "周宁");
        var unproved = new SemanticFinding { FindingType = SemanticFindingType.RelationshipExtraction, SubjectIdentity = a.SubjectIdentity, TargetIdentity = second.SubjectIdentity,
            RelationshipType = KnowledgeRelationType.Spouse, EventActuality = SemanticActuality.ActualCompleted,
            SourceEvidenceIds = [u.Evidence.Id, r.Units[1].Evidence.Id], SupportSpans = [a.SupportSpans[0], second.SupportSpans[0]], Reason = "Adversarial co-occurrence", IdentityResolutionStatus = SemanticResolution.NewIdentity };
        var checkedRelation = SemanticQualityAuditor.Audit(Response(r, unproved), r, s.Sources).Findings.Single(); Assert.False(checkedRelation.CanPromote);
        Assert.Contains("CooccurrenceIsNotRelationshipProof", checkedRelation.Issues); Assert.Contains("NoJointParticipantProof", checkedRelation.Issues);
        var addressing = unproved with { FindingType = SemanticFindingType.DirectionalAddressing, SpeakerIdentity = a.SubjectIdentity, ListenerIdentity = second.SubjectIdentity, SelfReference = "我", AddresseeReference = "你" };
        Assert.False(SemanticQualityAuditor.Audit(Response(r, addressing), r, s.Sources).Findings.Single().CanPromote);
    }

    [Fact]
    public void Cross_boundary_event_keeps_both_supports_and_cannot_substitute_one_evidence_for_the_other()
    {
        var s = new SemanticFixture(Window("林青说起旧事。") + Window("林青今天恢复记忆了。")); var r = Plan(s).Single();
        var spans = r.Units.Select(x => new SemanticSupport(x.Evidence.Id, x.Evidence.StartUtf16, x.EvidenceText.IndexOf('。') + 1, x.EvidenceText[..(x.EvidenceText.IndexOf('。') + 1)])).ToArray();
        var f = new SemanticFinding { FindingType = SemanticFindingType.EventVerification, EventType = TranslationImpactEventType.CharacterStateChanged,
            EventActuality = SemanticActuality.ActualCompleted, VerifiedParticipants = [Name(r.Units[0]).SubjectIdentity!], IdentityResolutionStatus = SemanticResolution.NewIdentity,
            SourceEvidenceIds = r.Units.Select(x => x.Evidence.Id).ToArray(), SupportSpans = spans, Reason = "Explicit named subject in both source excerpts" };
        var result = SemanticQualityAuditor.Audit(Response(r, f), r, s.Sources); Assert.True(result.Validated); Assert.Equal(2, result.Findings.Single().Finding.SupportSpans.Count);
        Assert.False(SemanticQualityAuditor.Audit(Response(r, f with { SupportSpans = spans.Take(1).ToArray() }), r, s.Sources).Findings.Single().EvidenceValid);
    }

    [Fact]
    public async Task Partial_response_reopen_regroup_resume_sends_only_missing_evidence_and_is_idempotent()
    {
        var (s, store, _) = await Create(6); var atomic = Plan(s, 1); var plan = Plan(s, 6); int calls = 0;
        var fake = new SemanticFake((prompt, _) =>
        {
            var response = FromPrompt(prompt, atomic); if (calls++ == 0) response = response with { ReviewedEvidenceIds = response.ReviewedEvidenceIds.Take(3).ToArray(), Findings = response.Findings.Take(3).ToArray() };
            return Task.FromResult(SemanticFake.Completion(response));
        });
        var state = await Run(store, s, plan, fake, await store.LoadAsync());
        Assert.Equal(3, state.EvidenceResults.Count(x => x.Status == SemanticBatchStatus.Pending)); Assert.Equal(3, state.Proposals.Count);
        var different = Plan(s, 2); state = await store.LoadAsync(); var dry = SemanticSurveyEngine.DryRun(different, state, Legacy(s));
        Assert.Equal(3, dry.CompletedEvidence); Assert.Equal(3, dry.PendingEvidence); Assert.Equal(2, dry.PendingRequests);
        state = await Run(store, s, different, fake, state);
        Assert.Equal(6, state.Proposals.Count); Assert.Equal(3, calls);
        foreach (var first in atomic.Take(3)) Assert.Equal(1, fake.Prompts.Count(p => p.Contains("id=" + first.Evidence.Id, StringComparison.Ordinal)));
        long revision = state.Revision; state = await Run(store, s, Plan(s, 4), fake, state); Assert.Equal(revision, state.Revision); Assert.Equal(3, calls);
        Assert.DoesNotContain(s.Sources[0].OriginalText[..100], Encoding.UTF8.GetString(SemanticSurveyStore.Serialize(state)));
    }

    [Theory]
    [InlineData("refusal")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task Batch_failures_checkpoint_all_members_and_never_automatically_retry_or_change_model(string failure)
    {
        var (s, store, _) = await Create(8); var atomic = Plan(s, 1); var plan = Plan(s); using var cts = new CancellationTokenSource(); int calls = 0;
        var fake = new SemanticFake((prompt, _) =>
        {
            if (calls++ == 0)
            {
                if (failure == "refusal") throw new GeminiSurveyBlockedException("SAFETY");
                if (failure == "cancel") cts.Cancel(); throw new OperationCanceledException();
            }
            return Task.FromResult(SemanticFake.Completion(FromPrompt(prompt, atomic)));
        });
        if (failure == "cancel") await Assert.ThrowsAsync<OperationCanceledException>(() => Run(store, s, plan, fake, new() { ProjectId = s.Memory.ProjectId }, ct: cts.Token));
        else await Run(store, s, plan, fake, await store.LoadAsync());
        var state = await store.LoadAsync(); Assert.Equal(failure == "cancel" ? 1 : 2, calls);
        Assert.Equal(4, state.EvidenceResults.Count(x => x.Status == (failure == "refusal" ? SemanticBatchStatus.Blocked : SemanticBatchStatus.Failed)));
        var changedOptions = s.Options with { Model = "explicit-other-model" };
        var changed = failure == "refusal" ? SemanticSurveyRequests.Plan(s.Memory, s.Sources, Legacy(s), "other", changedOptions, batching: new(2)) : Plan(s, 2);
        var dry = SemanticSurveyEngine.DryRun(changed, state, Legacy(s));
        Assert.Equal(failure == "refusal" ? 4 : 0, dry.BlockedEvidence);
        if (failure == "refusal")
        {
            Assert.All(dry.ExecutionRequests, r => Assert.All(r.Units, u => Assert.DoesNotContain(u.Evidence.Id, atomic.Take(4).Select(x => x.Evidence.Id))));
            var next = new SemanticFake((p, _) => Task.FromResult(SemanticFake.Completion(FromPrompt(p, changed.SelectMany(x => x.Units).ToArray()))));
            state = await new SemanticSurveyEngine(next).RunAsync(store, changed, s.Sources, Legacy(s), changedOptions, new(50, 10_000_000), dry.Fingerprint);
            Assert.Equal(2, next.Prompts.Count);
            Assert.All(atomic.Take(4), u => Assert.DoesNotContain(next.Prompts, p => p.Contains("id=" + u.Evidence.Id, StringComparison.Ordinal)));
            Assert.Equal(4, SemanticSurveyEngine.DryRun(Plan(s, 6), state, Legacy(s)).BlockedEvidence);
        }
        else
        {
            var resume = new SemanticFake((prompt, _) => Task.FromResult(SemanticFake.Completion(FromPrompt(prompt, atomic))));
            state = await Run(store, s, changed, resume, state); Assert.Equal(8, state.Proposals.Count); Assert.Equal(failure == "cancel" ? 4 : 2, resume.Prompts.Count);
        }
    }

    [Fact]
    public async Task Legacy_v1_checkpoint_and_reviews_are_reused_by_copy_on_write_upgrade_with_no_destructive_migration()
    {
        var (s, store, _) = await Create(4); var atomic = Plan(s, 1);
        var fake = new SemanticFake((p, _) => Task.FromResult(SemanticFake.Completion(FromPrompt(p, atomic))));
        var state = await Run(store, s, atomic.Take(2).ToArray(), fake, await store.LoadAsync());
        // Exact v1 shape: no v2 fields, and a human review whose bytes/history must survive.
        state.SchemaVersion = 1; state.EvidenceResults.Clear();
        state.Reviews.Add(new(Guid.NewGuid(), state.Proposals[0].Id, MemoryReviewStatus.Accepted, DateTimeOffset.UtcNow, "Old human decision", new()));
        var node = System.Text.Json.Nodes.JsonNode.Parse(SemanticSurveyStore.Serialize(state))!.AsObject(); node.Remove("evidenceResults");
        foreach (var b in node["batches"]!.AsArray()) { b!.AsObject().Remove("evidenceKeys"); b.AsObject().Remove("contextEventIds"); }
        byte[] original = Encoding.UTF8.GetBytes(node.ToJsonString());
        await SimpleFileStorage.WriteAsync(store.LegacyPathName, original, b => JsonSerializer.Deserialize<SemanticSurveyState>(b, DocumentJson.Options)!);
        File.Delete(store.PathName); // This synthetic v2 setup file only; the v1 file is retained.
        var loaded = await store.LoadAsync(); Assert.Equal(1, loaded.SchemaVersion);
        var grouped = Plan(s); var dry = SemanticSurveyEngine.DryRun(grouped, loaded, Legacy(s)); Assert.Equal(2, dry.CompletedEvidence); Assert.Equal(2, dry.PendingEvidence);
        state = await Run(store, s, grouped, fake, loaded);
        Assert.Equal(2, state.SchemaVersion); Assert.Equal(original, await File.ReadAllBytesAsync(store.LegacyPathName));
        Assert.Equal(4, state.Proposals.Count); Assert.Equal(loaded.Reviews, state.Reviews); Assert.Equal(3, fake.Prompts.Count);
        Assert.Equal(0, SemanticSurveyEngine.DryRun(Plan(s, 6), await store.LoadAsync(), Legacy(s)).PendingRequests);
    }

    [Fact]
    public void Source_model_and_context_changes_invalidate_keys_but_not_unaffected_chapters_or_block_history()
    {
        var s = new SemanticFixture(Windows(4)); s.Sources.Add(new(Guid.NewGuid(), 2, Windows(2))); var before = Plan(s);
        s.Sources[0] = s.Sources[0] with { OriginalText = s.Sources[0].OriginalText + "新增原文。" }; var after = Plan(s);
        Assert.NotEqual(before[0].Id, after[0].Id); Assert.Equal(before.Last().Id, after.Last().Id);
        var other = SemanticSurveyRequests.Plan(s.Memory, s.Sources, Legacy(s), "other:model", s.Options with { Model = "other" });
        Assert.DoesNotContain(other.SelectMany(x => x.Units), u => after.SelectMany(x => x.Units).Any(x => x.Id == u.Id));
    }

    [Fact]
    public async Task Changed_source_or_options_between_preview_and_send_cannot_send_and_human_confirm_reaudits_each_evidence()
    {
        var (s, store, session) = await Create(4); var atomic = Plan(s, 1); var grouped = Plan(s);
        var fake = new SemanticFake((p, _) => Task.FromResult(SemanticFake.Completion(FromPrompt(p, atomic))));
        var changed = s.Sources[0] with { OriginalText = s.Sources[0].OriginalText + "变更" };
        string fp = SemanticSurveyEngine.DryRun(grouped, await store.LoadAsync(), Legacy(s)).Fingerprint;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SemanticSurveyEngine(fake).RunAsync(store, grouped, [changed], Legacy(s), s.Options, new(50, 10_000_000), fp));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SemanticSurveyEngine(fake).RunAsync(store, grouped, s.Sources, Legacy(s), s.Options with { Model = "different" }, new(), fp));
        Assert.Empty(fake.Prompts);
        var state = await Run(store, s, grouped, fake, await store.LoadAsync());
        var last = state.Proposals.Single(p => p.Audit.Finding.SourceEvidenceIds.Contains(atomic[^1].Evidence.Id));
        state = await new SemanticMemoryReviewService(session, new()).ReviewAsync(last.Id, MemoryReviewStatus.Confirmed, new(new(1, null, 1, 1), "Human checked final evidence", new Dictionary<string, Guid>()), state.Revision);
        var memory = await new EntityMemoryStore(session).LoadAsync(); var proof = Assert.Single(memory.Evidence);
        Assert.Equal(atomic[^1].Evidence.Id, last.Audit.Finding.SupportSpans.Single().EvidenceId);
        Assert.Equal(atomic[^1].Evidence.StartUtf16, proof.StartUtf16);
        Assert.Single(memory.Reviews); Assert.Equal(MemoryReviewStatus.Confirmed, state.Reviews.Last().Status);
    }

    [Fact]
    public void Changing_confirmed_context_splits_or_rekeys_only_evidence_that_depends_on_it()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1, "林青说道：我叫林青。"), id = s.Entity("known", "林青");
        var name = s.Fact(id, MemoryFactKind.PrimaryName, "林青", proof, new(1, null, 1, 1)); s.Confirm(name.Id);
        s.Source(2, Windows(2) + Window("空山积雪。") + Window("空山积雪。"));
        var options = new ModelOptions { ContextSize = 32768, MaximumOutputTokens = 4096 };
        var legacy = new BookIntelligenceState { ProjectId = s.State.ProjectId };
        var before = SemanticSurveyRequests.Plan(s.State, s.Sources, legacy, "mock:selected", options);
        Assert.Equal(2, before.Count(x => x.ChapterNumber == 2));
        EntityMemoryReviewService.AppendDecision(s.State, s.Sources, name.Id, MemoryReviewStatus.Accepted, new(1, null, 1, 1), "Human demotes dependency");
        var after = SemanticSurveyRequests.Plan(s.State, s.Sources, legacy, "mock:selected", options);
        var a = before.SelectMany(x => x.Units).Where(x => x.ChapterNumber == 2).ToArray();
        var b = after.SelectMany(x => x.Units).Where(x => x.ChapterNumber == 2).ToArray();
        Assert.NotEqual(a[0].Id, b[0].Id); Assert.NotEqual(a[1].Id, b[1].Id); Assert.Equal(a[2].Id, b[2].Id); Assert.Equal(a[3].Id, b[3].Id);
    }

    [Fact]
    public void Confirmed_event_context_changes_invalidate_batch_review_and_semantic_dependents_not_only_fact_context()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1, "林青恢复记忆了。"), person = s.Entity("person", "林青");
        var name = s.Fact(person, MemoryFactKind.PrimaryName, "林青", proof, new(1, null, 1, 1)); s.Confirm(name.Id);
        var builder = new EntityMemoryBuilder(s.State); var priorEvent = builder.Event(new() { Type = TranslationImpactEventType.CharacterStateChanged, EntityIds = [person],
            EvidenceIds = [proof], Timeline = new(1, null, 1, 1), Provenance = new(MemoryOrigin.User, "confirmed-event", "Synthetic reviewed prior event") }); s.Confirm(priorEvent.Id);
        s.Source(2, Windows(2)); var options = new ModelOptions { ContextSize = 32768 }; var legacy = new BookIntelligenceState { ProjectId = s.State.ProjectId };
        var r = SemanticSurveyRequests.Plan(s.State, s.Sources, legacy, "mock", options).Single(x => x.ChapterNumber == 2);
        Assert.Equal(priorEvent.Id, Assert.Single(r.ContextEventIds));
        var batch = new SemanticBatch(r.Id, r.ProjectId, r.Evidence.Id, r.BackendSignature, options, r.ContextFingerprint, r.DependencyFingerprint, r.ContextFactIds,
            r.AllowedIdentities, r.CpuCandidates, SemanticAdaptiveBatching.PromptVersion, SemanticSurveyContract.MultiEvidenceVersion, r.Units.Select(x => new SemanticEvidenceKey(x.Evidence.Id, x.Id)).ToArray(), r.ContextEventIds);
        Assert.True(SemanticDependencies.Current(batch, r.Evidence, s.State, s.Sources));
        string deps = string.Join(',', r.ContextFactIds.Concat(r.ContextEventIds).Select(x => x.ToString("N")));
        var alias = builder.Fact(new() { SubjectId = person, Kind = MemoryFactKind.Alias, Value = "阿青", InputFactIds = r.ContextFactIds, EvidenceIds = [proof], Timeline = new(1, null, 1, 1),
            Provenance = new(MemoryOrigin.User, "semantic-confirmation:test:deps=" + deps, "Synthetic explicit confirmation", RuleId: r.DependencyFingerprint) }); s.Confirm(alias.Id);
        Assert.True(s.Query().IsFresh(alias.Id));
        EntityMemoryReviewService.AppendDecision(s.State, s.Sources, priorEvent.Id, MemoryReviewStatus.Accepted, new(1, null, 1, 1), "Human demotes prior event");
        Assert.False(SemanticDependencies.Current(batch, r.Evidence, s.State, s.Sources)); Assert.Equal(MemoryReviewStatus.Stale, s.Query().Status(alias.Id));
        var next = SemanticSurveyRequests.Plan(s.State, s.Sources, legacy, "mock", options).Single(x => x.ChapterNumber == 2);
        Assert.Empty(next.ContextEventIds); Assert.NotEqual(r.Id, next.Id);
    }

    [Fact]
    public void Cpu_event_classification_cannot_borrow_proof_from_an_unrelated_member()
    {
        var s = new SemanticFixture(Windows(2)); var units = Plan(s, 1).ToArray(); Guid cpu = Guid.NewGuid();
        units[0] = units[0] with { CpuCandidates = [new(cpu, TranslationImpactEventType.CharacterStateChanged)] };
        var batch = SemanticAdaptiveBatching.Combine(units, s.Options); var e = units[1].Evidence;
        var finding = new SemanticFinding { FindingType = SemanticFindingType.EventVerification, EventActuality = SemanticActuality.NotAnEvent,
            CpuCandidateIds = [cpu], SourceEvidenceIds = [e.Id], SupportSpans = [new(e.Id, e.StartUtf16, 2, "林青")], Reason = "Adversarial attribution" };
        var audited = SemanticQualityAuditor.Audit(Response(batch, finding), batch, s.Sources);
        Assert.Contains(cpu, audited.UnprocessedCpuIds); Assert.Contains("CpuCandidateEvidenceMismatch", audited.Findings.Single().Issues); Assert.False(audited.Validated);
    }

    [Fact]
    public async Task Invalid_proof_is_not_a_success_cache_hit_but_successful_siblings_are_not_resent()
    {
        var (s, store, _) = await Create(4); var units = Plan(s, 1); var plan = Plan(s); int calls = 0;
        var fake = new SemanticFake((p, _) =>
        {
            var r = FromPrompt(p, units);
            if (calls++ == 0) r = r with { Findings = r.Findings.Select((f, i) => i == 0 ? f with { SupportSpans = [f.SupportSpans[0] with { Text = "伪造" }] } : f).ToArray() };
            return Task.FromResult(SemanticFake.Completion(r));
        });
        var state = await Run(store, s, plan, fake, await store.LoadAsync()); Assert.Equal(SemanticBatchStatus.Failed, state.EvidenceResults[0].Status);
        var dry = SemanticSurveyEngine.DryRun(Plan(s, 2), state, Legacy(s)); Assert.Equal(3, dry.CompletedEvidence); Assert.Equal(1, dry.PendingEvidence);
        state = await Run(store, s, Plan(s, 2), fake, state); Assert.Equal(2, calls); Assert.Equal(4, state.Proposals.Count(x => x.Audit.EvidenceValid));
        Assert.Single(state.Proposals, x => !x.Audit.EvidenceValid); Assert.Equal(4, state.Evidence.Count);
        state = await Run(store, s, plan, fake, state); Assert.Equal(2, calls);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
