using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit.Abstractions;

namespace QuickTranslator.AiEditor.Tests;

[Collection("Census metrics isolated")]
public sealed class SemanticQualityAndScaleTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Labelled_neutral_fixture_measures_schema_entities_relations_and_classifications_separately_without_claiming_model_accuracy()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-semantic-gold-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProjectRepository(); var session = await repository.CreateAsync(root, "Neutral gold mock");
            const string text = "周宁是林青的亲生父亲。林青与星河是同父同母的兄弟。记录官落款为星河。";
            var chapter = TranslationService.CreateChapter("Gold", text); await repository.AddChapterAsync(session, chapter, 1);
            var s = new SemanticFixture(text); s.Memory.ProjectId = session.Manifest.ProjectId; s.Sources[0] = new(chapter.Id, 1, text); var request = s.Plan().Single();
            var census = CharacterMentionCensus.Scan(s.Memory.ProjectId, 0, s.Sources, new() { ProjectId = s.Memory.ProjectId, Characters = new() { Id = s.Memory.ProjectId } }, new() { ProjectId = s.Memory.ProjectId });
            Assert.DoesNotContain(census.Candidates, x => x.ChineseName == "星河" && x.Category == MentionCandidateCategory.PersonCandidate);
            // Gold labels are independent of the production discovery algorithm. These outputs
            // are scripted mocks, not a measurement of Gemini/Ollama semantic ability.
            string[] goldEntities = ["周宁", "林青", "星河"]; var identities = goldEntities.Select((name, i) => new SemanticIdentity(null, "p" + i, name, MemoryEntityKind.Character, SemanticResolution.NewIdentity)).ToArray();
            var findings = goldEntities.Select((name, i) => s.Name(request, name, "p" + i)).ToList();
            SemanticFinding Relation(int a, int b, KnowledgeRelationType type) => new() { FindingType = SemanticFindingType.RelationshipExtraction,
                SubjectIdentity = identities[a], TargetIdentity = identities[b], IdentityResolutionStatus = SemanticResolution.NewIdentity, RelationshipType = type, Value = type.ToString(),
                EventActuality = SemanticActuality.ActualCompleted, SourceEvidenceIds = [request.Evidence.Id], SupportSpans = [new(request.Evidence.Id, 0, text.Length, text)],
                Reason = "Direct source relation, not an inferred parent", EffectiveChapter = 1, RevealedChapter = 1, Confidence = .9 };
            findings.Add(Relation(0, 1, KnowledgeRelationType.BiologicalFather)); findings.Add(Relation(1, 2, KnowledgeRelationType.FullSibling));
            string json = SemanticFixture.Json(SemanticFixture.Response(request, findings.ToArray()));
            var audited = SemanticQualityAuditor.Audit(SemanticSurveyContract.Parse(json), request, s.Sources); Assert.True(audited.Validated);
            var store = new SemanticSurveyStore(session); var fake = new SemanticFake((_, _) => Task.FromResult(SemanticFake.Completion(SemanticSurveyContract.Parse(json))));
            var state = await new SemanticSurveyEngine(fake).RunAsync(store, [request], s.Sources, new() { ProjectId = session.Manifest.ProjectId }, s.Options, new(), SemanticSurveyEngine.DryRun([request], await store.LoadAsync(), new() { ProjectId = session.Manifest.ProjectId }).Fingerprint);
            var service = new SemanticMemoryReviewService(session, repository);
            foreach (var proposal in state.Proposals.ToArray()) state = await service.ReviewAsync(proposal.Id, MemoryReviewStatus.Confirmed, new(new(1, null, 1, 1), "Checked neutral gold proof", new Dictionary<string, Guid>()), state.Revision);
            var memory = await new EntityMemoryStore(session).LoadAsync(); Assert.Equal(3, memory.Entities.Count); Assert.Equal(5, memory.Facts.Count);
            var query = new EntityMemoryQuery(memory, s.Sources); Assert.Equal(2, query.AtChapter(1).Relationships.Count);
            EntityMemoryReasoning.Propose(memory, s.Sources); Assert.Equal(2, memory.Facts.Count(x => x.Provenance.Origin == MemoryOrigin.Rule));
            Assert.All(memory.Facts.Where(x => x.Provenance.Origin == MemoryOrigin.Rule), f => Assert.Equal(MemoryReviewStatus.Proposed, new EntityMemoryQuery(memory, s.Sources).Status(f.Id)));
            object Metrics(IReadOnlyCollection<string> gold, IReadOnlyCollection<string> predicted)
            {
                int tp = predicted.Intersect(gold, StringComparer.Ordinal).Count(), fp = predicted.Except(gold, StringComparer.Ordinal).Count(), fn = gold.Except(predicted, StringComparer.Ordinal).Count();
                return new { truePositive = tp, falsePositive = fp, falseNegative = fn, precision = tp / (double)Math.Max(1, tp + fp), recall = tp / (double)Math.Max(1, tp + fn) };
            }
            var predictedNames = audited.Findings.Where(x => x.Finding.FindingType == SemanticFindingType.CharacterIdentification).Select(x => x.Finding.Value).ToArray();
            string[] goldRelations = ["p0->p1:BiologicalFather", "p1->p2:FullSibling"];
            var predictedRelations = audited.Findings.Where(x => x.Finding.FindingType == SemanticFindingType.RelationshipExtraction).Select(x => $"{x.Finding.SubjectIdentity!.LocalId}->{x.Finding.TargetIdentity!.LocalId}:{x.Finding.RelationshipType}").ToArray();
            output.WriteLine(JsonSerializer.Serialize(new { corpus = "independent neutral labelled mock, NOT real model evaluation", schemaValid = 1, schemaTotal = 1,
                entities = Metrics(goldEntities, predictedNames), relationships = Metrics(goldRelations, predictedRelations), cpuMissRecoveredByMock = "星河",
                actuality = "8/8 gold mock classification routing cases in SemanticContractTests; not LLM accuracy", blocked = 0, unprocessed = 0,
                requests = fake.Prompts.Count, inputEstimate = request.EstimatedInputTokens, tokenReserve = request.ReservedTokens }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("林青提出离婚。", TranslationImpactEventType.RelationshipEnded)]
    [InlineData("林青希望恢复记忆。", TranslationImpactEventType.CharacterStateChanged)]
    [InlineData("其实是天气的原因。", TranslationImpactEventType.RevealIdentity)]
    public void Independent_conservative_guard_flags_incorrect_actual_completed_claims(string text, TranslationImpactEventType type)
    {
        var s = new SemanticFixture(text); var r = s.Plan().Single();
        var f = new SemanticFinding { FindingType = SemanticFindingType.EventVerification, EventType = type, EventActuality = SemanticActuality.ActualCompleted,
            SourceEvidenceIds = [r.Evidence.Id], SupportSpans = [new(r.Evidence.Id, 0, text.Length, text)], Reason = "Deliberately wrong mock classification" };
        var result = SemanticQualityAuditor.Audit(SemanticFixture.Response(r, f), r, s.Sources);
        Assert.False(result.Validated); Assert.False(result.Findings.Single().CanPromote); Assert.NotEmpty(result.Findings.Single().Issues);
    }

    [Fact]
    public void Same_spelling_body_mind_similar_names_and_invalid_utf16_remain_distinct_or_fail_closed()
    {
        var s = new SemanticFixture("林青与林清看着纸条上的林青😀。"); var r = s.Plan().Single();
        var body = s.Name(r, "林青", "body", MemoryEntityKind.Body); var mind = s.Name(r, "林青", "mind", MemoryEntityKind.Consciousness); var similar = s.Name(r, "林清", "near");
        var result = SemanticQualityAuditor.Audit(SemanticFixture.Response(r, body, mind, similar), r, s.Sources); Assert.True(result.Validated);
        Assert.Equal(3, result.Findings.Select(x => x.Finding.SubjectIdentity!.LocalId).Distinct().Count());
        int emoji = s.Sources[0].OriginalText.IndexOf("😀", StringComparison.Ordinal);
        var broken = body with { SupportSpans = [new(r.Evidence.Id, emoji, 1, "\ud83d")] };
        Assert.False(SemanticQualityAuditor.Audit(SemanticFixture.Response(r, broken), r, s.Sources).Validated);
    }

    [Fact]
    public void Context_dependency_changes_invalidate_only_relevant_prompts_and_confirmed_semantic_dependents()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1, "林青说道：我叫林青。"), id = s.Entity("person", "林青");
        var primary = s.Fact(id, MemoryFactKind.PrimaryName, "林青", proof, new(1, null, 1, 1)); s.Confirm(primary.Id); s.Source(2, "空山的云朵缓缓飘过。");
        var options = new QuickTranslator.AiEditor.Core.Models.ModelOptions { ContextSize = 32768 };
        var before = SemanticSurveyRequests.Plan(s.State, s.Sources, new() { ProjectId = s.State.ProjectId }, "mock", options);
        EntityMemoryReviewService.AppendDecision(s.State, s.Sources, primary.Id, MemoryReviewStatus.Accepted, new(1, null, 1, 1), "Demoted by human");
        var after = SemanticSurveyRequests.Plan(s.State, s.Sources, new() { ProjectId = s.State.ProjectId }, "mock", options);
        Assert.NotEqual(before[0].Id, after[0].Id); Assert.Equal(before[1].Id, after[1].Id);
    }

    [Fact]
    public void Confirmed_semantic_facts_and_events_stale_when_confirmed_context_is_demoted_without_rewriting_history()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1, "林青也叫阿青。林青恢复记忆了。"), id = s.Entity("person", "林青");
        var name = s.Fact(id, MemoryFactKind.PrimaryName, "林青", proof, new(1, null, 1, 1)); s.Confirm(name.Id);
        string fp = SemanticDependencies.Fingerprint(s.Query(), [name.Id]); var builder = new EntityMemoryBuilder(s.State);
        var alias = builder.Fact(new() { SubjectId = id, Kind = MemoryFactKind.Alias, Value = "阿青", InputFactIds = [name.Id], EvidenceIds = [proof], Timeline = new(1, null, 1, 1),
            Provenance = new(MemoryOrigin.User, "semantic-confirmation:alias", "Synthetic checked semantic alias", RuleId: fp) }); s.Confirm(alias.Id);
        var item = builder.Event(new() { Type = TranslationImpactEventType.CharacterStateChanged, EntityIds = [id], EvidenceIds = [proof], Timeline = new(1, null, 1, 1),
            Provenance = new(MemoryOrigin.User, "semantic-confirmation:event:deps=" + name.Id.ToString("N"), "Synthetic checked event", RuleId: fp) }); s.Confirm(item.Id);
        Assert.NotEmpty(s.Query().AtChapter(1).Events); Assert.Contains(s.Query().AtChapter(1).Entities.Single().Names, x => x == "阿青");
        int count = s.State.Reviews.Count;
        EntityMemoryReviewService.AppendDecision(s.State, s.Sources, name.Id, MemoryReviewStatus.Accepted, new(1, null, 1, 1), "Demote input, not dependent history");
        Assert.Equal(MemoryReviewStatus.Stale, s.Query().Status(alias.Id)); Assert.Equal(MemoryReviewStatus.Stale, s.Query().Status(item.Id));
        Assert.Empty(s.Query().AtChapter(1).Events); Assert.Empty(s.Query().AtChapter(1).Entities); Assert.Equal(count + 1, s.State.Reviews.Count);
    }

    [Fact]
    public async Task One_thousand_chapter_synthetic_semantic_checkpoint_storage_is_linear_and_resume_is_offline()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-semantic-scale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await new ProjectRepository().CreateAsync(root, "Synthetic semantic scale");
            var s = new SemanticFixture(); s.Memory.ProjectId = session.Manifest.ProjectId; s.Sources.Clear();
            for (int i = 1; i <= 1000; i++) s.Sources.Add(new(Guid.NewGuid(), i, "记录官落款为星河。NEVER_COPY_FULL_EXCERPT" + new string('中', 500)));
            var watch = Stopwatch.StartNew(); var plan = s.Plan(); double planSeconds = watch.Elapsed.TotalSeconds; var lookup = plan.ToDictionary(x => x.Prompt);
            var fake = new SemanticFake((prompt, _) => { var r = lookup[prompt]; return Task.FromResult(SemanticFake.Completion(SemanticFixture.Response(r, s.Name(r, "星河", "p") with { EffectiveChapter = r.ChapterNumber, RevealedChapter = r.ChapterNumber }))); });
            var store = new SemanticSurveyStore(session); var legacy = new BookIntelligenceState { ProjectId = session.Manifest.ProjectId }; var state = await store.LoadAsync();
            var half = plan.Take(500).ToArray(); watch.Restart();
            state = await new SemanticSurveyEngine(fake).RunAsync(store, half, s.Sources, legacy, s.Options, new(1000, 100_000_000), SemanticSurveyEngine.DryRun(half, state, legacy).Fingerprint);
            double first = watch.Elapsed.TotalSeconds; long bytes500 = new FileInfo(store.PathName).Length;
            watch.Restart(); state = await new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, legacy, s.Options, new(1000, 100_000_000), SemanticSurveyEngine.DryRun(plan, state, legacy).Fingerprint);
            double second = watch.Elapsed.TotalSeconds; long bytes1000 = new FileInfo(store.PathName).Length;
            Assert.Equal(1000, state.Proposals.Count); Assert.Equal(1000, fake.Prompts.Count); Assert.All(state.Batches, b => Assert.Equal(SemanticBatchStatus.Validated, state.Status(b.Id)));
            Assert.InRange((double)bytes1000 / bytes500, 1.8, 2.2); Assert.InRange(bytes1000, 1, 12 * 1024 * 1024);
            Assert.DoesNotContain("NEVER_COPY_FULL_EXCERPT", Encoding.UTF8.GetString(SemanticSurveyStore.Serialize(state)));
            watch.Restart(); var again = await new SemanticSurveyEngine(fake).RunAsync(store, plan, s.Sources, legacy, s.Options, new(), SemanticSurveyEngine.DryRun(plan, state, legacy).Fingerprint);
            double resume = watch.Elapsed.TotalSeconds; Assert.Equal(state.Revision, again.Revision); Assert.Equal(1000, fake.Prompts.Count);
            output.WriteLine(JsonSerializer.Serialize(new { chapters = 1000, utf16 = s.Sources.Sum(x => x.OriginalText.Length), requests = 1000, proposals = state.Proposals.Count, bytes500, bytes1000,
                planSeconds, secondsFirst500 = first, secondsAdditional500 = second, resumeSeconds = resume, inputEstimate = plan.Sum(x => (long)x.EstimatedInputTokens), tokenReserve = plan.Sum(x => (long)x.ReservedTokens),
                concurrency = 1, note = "mock responses; planning+sidecar benchmark, not HTTP/project-file rereads or model latency; no model quality claim" }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
