using System.Diagnostics;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using Xunit.Abstractions;

namespace QuickTranslator.AiEditor.Tests;

[Collection("Census metrics isolated")]
public sealed class AdaptiveSemanticBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public void Same_labelled_2B_gold_fixture_preserves_findings_actuality_and_exact_evidence_attribution()
    {
        // The same neutral gold passage and all eight actuality labels used by 2B.
        const string gold = "周宁是林青的亲生父亲。林青与星河是同父同母的兄弟。记录官落款为星河。";
        (string Text, SemanticActuality Label)[] events = [("其实是天气的原因。", SemanticActuality.NotAnEvent), ("林青去年就结婚了。", SemanticActuality.HistoricalReference),
            ("林青提出离婚。", SemanticActuality.PlannedRequested), ("如果林青离婚就离开。", SemanticActuality.HypotheticalConditional), ("林青没有离婚。", SemanticActuality.Negated),
            ("林青希望恢复记忆。", SemanticActuality.PlannedRequested), ("林青今天恢复记忆了。", SemanticActuality.ActualCompleted), ("听说林青也许结婚了。", SemanticActuality.Uncertain)];
        string[] prefixes = [gold, .. events.Select(x => x.Text), "林青在东城行医。", "林青在西城教书。"];
        var s = new SemanticFixture(string.Concat(prefixes.Select(AdaptiveSemanticTests.Window)));
        var atomic = AdaptiveSemanticTests.Plan(s, 1); var packed = AdaptiveSemanticTests.Plan(s);
        var labelled = new Dictionary<Guid, SemanticFinding[]>();
        foreach (var unit in atomic)
        {
            int index = unit.Evidence.StartUtf16 / 900;
            if (index == 0)
            {
                var names = new[] { "周宁", "林青", "星河" }.Select((x, i) => AdaptiveSemanticTests.Name(unit, x, "gold_" + i)).ToArray();
                SemanticFinding Relation(int a, int b, KnowledgeRelationType type) => new() { FindingType = SemanticFindingType.RelationshipExtraction,
                    SubjectIdentity = names[a].SubjectIdentity, TargetIdentity = names[b].SubjectIdentity, RelationshipType = type, IdentityResolutionStatus = SemanticResolution.NewIdentity,
                    EventActuality = SemanticActuality.ActualCompleted, SourceEvidenceIds = [unit.Evidence.Id], SupportSpans = [new(unit.Evidence.Id, 0, gold.Length, gold)], Reason = "Independent labelled direct relation" };
                labelled[unit.Evidence.Id] = [.. names, Relation(0, 1, KnowledgeRelationType.BiologicalFather), Relation(1, 2, KnowledgeRelationType.FullSibling)];
            }
            else if (index <= events.Length)
            {
                var sample = events[index - 1];
                labelled[unit.Evidence.Id] = [new() { FindingType = SemanticFindingType.EventVerification, EventType = TranslationImpactEventType.CharacterStateChanged,
                    EventActuality = sample.Label, IdentityResolutionStatus = SemanticResolution.Unresolved,
                    VerifiedParticipants = sample.Text.Contains("林青", StringComparison.Ordinal) ? [AdaptiveSemanticTests.Name(unit).SubjectIdentity!] : [],
                    SourceEvidenceIds = [unit.Evidence.Id], SupportSpans = [new(unit.Evidence.Id, unit.Evidence.StartUtf16, sample.Text.Length, sample.Text)], Reason = "Independent actuality gold label, scripted response" }];
            }
            else labelled[unit.Evidence.Id] = [AdaptiveSemanticTests.Name(unit, "林青", "homonym_" + index)];
        }
        (SemanticAuditedFinding[] Findings, Guid[] Reviewed) Audit(IReadOnlyList<SemanticSurveyRequest> requests)
        {
            var results = requests.Select(r => SemanticQualityAuditor.Audit(SemanticSurveyContract.Parse(SemanticFixture.Json(AdaptiveSemanticTests.Response(r,
                r.Units.SelectMany(u => labelled[u.Evidence.Id]).ToArray()))), r, s.Sources)).ToArray();
            Assert.All(results, x => Assert.True(x.Validated, string.Join(',', x.Issues.Concat(x.Findings.SelectMany(f => f.Issues)))));
            return (results.SelectMany(x => x.Findings).ToArray(), results.SelectMany(x => x.ReviewedEvidenceIds!).ToArray());
        }
        var before = Audit(atomic); var after = Audit(packed);
        Assert.Equal(before.Findings.Select(EntityMemoryIdentity.Fingerprint), after.Findings.Select(EntityMemoryIdentity.Fingerprint));
        Assert.Equal(atomic.Select(x => x.Evidence.Id), after.Reviewed); Assert.Equal(8, after.Findings.Count(x => x.Finding.FindingType == SemanticFindingType.EventVerification));
        Assert.Equal(5, after.Findings.Count(x => x.Finding.FindingType == SemanticFindingType.CharacterIdentification)); Assert.Equal(2, after.Findings.Count(x => x.Finding.FindingType == SemanticFindingType.RelationshipExtraction));
        Assert.All(after.Findings, x => Assert.True(x.EvidenceValid));
        Assert.Equal(2, after.Findings.Where(x => x.Finding.SubjectIdentity?.LocalId.StartsWith("homonym_", StringComparison.Ordinal) == true).Select(x => x.Finding.SubjectIdentity!.LocalId).Distinct().Count());
        output.WriteLine(JsonSerializer.Serialize(new { fixture = "same 2B neutral gold + 8 actuality labels + 2 explicit homonyms; scripted mocks, NOT LLM accuracy",
            charactersBefore = 5, charactersAfter = 5, relationshipsBefore = 2, relationshipsAfter = 2, actualityMatched = 8, actualityTotal = 8,
            evidenceAttributionErrors = 0, missingEvidence = 0, identityConfusion = 0, baselineRequests = atomic.Count, adaptiveRequests = packed.Count,
            baselineInputEstimate = atomic.Sum(x => x.EstimatedInputTokens), adaptiveInputEstimate = packed.Sum(x => x.EstimatedInputTokens),
            baselineReserve = atomic.Sum(x => x.ReservedTokens), adaptiveReserve = packed.Sum(x => x.ReservedTokens) }));
    }

    [Fact]
    public void Nineteen_chapter_199_evidence_cost_comparison_is_synthetic_not_a_personal_project_rerun()
    {
        var s = new SemanticFixture(); s.Sources.Clear();
        for (int i = 1; i <= 19; i++) s.Sources.Add(new(Guid.NewGuid(), i, AdaptiveSemanticTests.Windows(i <= 9 ? 11 : 10)));
        var options = s.Options with { Model = "gemini-3.8-flash", ContextSize = 65536 };
        IReadOnlyList<SemanticSurveyRequest> Plan(int max) => SemanticSurveyRequests.Plan(s.Memory, s.Sources, new() { ProjectId = s.Memory.ProjectId }, "mock:no-api", options, batching: new(max));
        var watch = Stopwatch.StartNew(); var baseline = Plan(1); var adaptive = Plan(4); double seconds = watch.Elapsed.TotalSeconds;
        Assert.Equal(199, baseline.Count); Assert.Equal(57, adaptive.Count); Assert.Equal(199, adaptive.Sum(x => x.Units.Count));
        Assert.InRange(adaptive.Count, 40, 70); Assert.True(adaptive.Sum(x => x.EstimatedInputTokens) < baseline.Sum(x => x.EstimatedInputTokens));
        Assert.Equal(baseline.Select(x => x.Id), adaptive.SelectMany(x => x.Units).Select(x => x.Id));
        output.WriteLine(JsonSerializer.Serialize(new { corpus = "SYNTHETIC 19 chapters / 199 evidence; NOT the private 19-chapter project", utf16 = s.Sources.Sum(x => x.OriginalText.Length),
            modelLabelOnly = options.Model, context = options.ContextSize, baselineRequests = baseline.Count, adaptiveRequests = adaptive.Count,
            baselineInputEstimate = baseline.Sum(x => x.EstimatedInputTokens), adaptiveInputEstimate = adaptive.Sum(x => x.EstimatedInputTokens),
            baselineReserve = baseline.Sum(x => x.ReservedTokens), adaptiveReserve = adaptive.Sum(x => x.ReservedTokens),
            evidencePerBatch = adaptive.GroupBy(x => x.Units.Count).ToDictionary(x => x.Key, x => x.Count()), planningSeconds = seconds,
            actualApiRequests = 0, actualProviderTokens = (int?)null,
            userReportedRealBaseline = new { requests = 199, estimatedInput = 768794, conservativeReserve = 3961867 },
            note = "Actual private-project adaptive counts/cost cannot be inferred exactly from totals; no personal Project accessed. Estimates use the same UTF-8/3 heuristic, reserve is a byte ceiling, neither is actual usage." }));
    }

    [Fact]
    public async Task Five_hundred_and_one_thousand_chapter_multi_evidence_checkpoint_benchmark_has_noop_resume_and_bounded_growth()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-adaptive-scale-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = await new ProjectRepository().CreateAsync(root, "Synthetic batching scale only");
            var s = new SemanticFixture(); s.Memory.ProjectId = session.Manifest.ProjectId; s.Sources.Clear();
            for (int i = 1; i <= 1000; i++) s.Sources.Add(new(Guid.NewGuid(), i, AdaptiveSemanticTests.Windows(4)));
            var watch = Stopwatch.StartNew(); var atomic = AdaptiveSemanticTests.Plan(s, 1); var plan = AdaptiveSemanticTests.Plan(s); double planSeconds = watch.Elapsed.TotalSeconds;
            Assert.Equal(4000, atomic.Count); Assert.Equal(1000, plan.Count); var lookup = plan.ToDictionary(x => x.Prompt);
            var fake = new SemanticFake((p, _) => { var r = lookup[p]; return Task.FromResult(SemanticFake.Completion(AdaptiveSemanticTests.Response(r, r.Units.Select(x => AdaptiveSemanticTests.Name(x)).ToArray()))); });
            var store = new SemanticSurveyStore(session); var legacy = new BookIntelligenceState { ProjectId = s.Memory.ProjectId }; var state = await store.LoadAsync();
            async Task<SemanticSurveyState> Run(IReadOnlyList<SemanticSurveyRequest> requests) => await new SemanticSurveyEngine(fake).RunAsync(store, requests, s.Sources, legacy, s.Options,
                new(1000, 100_000_000), SemanticSurveyEngine.DryRun(requests, state, legacy).Fingerprint);
            watch.Restart(); state = await Run(plan.Take(500).ToArray()); double first = watch.Elapsed.TotalSeconds; long bytes500 = new FileInfo(store.PathName).Length;
            watch.Restart(); state = await Run(plan); double second = watch.Elapsed.TotalSeconds; long bytes1000 = new FileInfo(store.PathName).Length;
            Assert.Equal(1000, fake.Prompts.Count); Assert.Equal(4000, state.Proposals.Count); Assert.Equal(4000, state.EvidenceResults.Count);
            Assert.InRange((double)bytes1000 / bytes500, 1.8, 2.2); Assert.InRange(bytes1000, 1, 32 * 1024 * 1024);
            Assert.DoesNotContain(AdaptiveSemanticTests.Windows(1)[..150], Encoding.UTF8.GetString(SemanticSurveyStore.Serialize(state)));
            long revision = state.Revision; watch.Restart(); state = await Run(atomic); double resume = watch.Elapsed.TotalSeconds;
            Assert.Equal(revision, state.Revision); Assert.Equal(1000, fake.Prompts.Count);
            output.WriteLine(JsonSerializer.Serialize(new { chapters = 1000, evidence = 4000, utf16 = s.Sources.Sum(x => x.OriginalText.Length),
                baselineRequests = atomic.Count, adaptiveRequests = plan.Count, concurrency = 1, proposals = state.Proposals.Count, bytes500, bytes1000,
                planSeconds, secondsFirst500 = first, secondsAdditional500 = second, resumeAfterRegroupSeconds = resume,
                baselineInputEstimate = atomic.Sum(x => (long)x.EstimatedInputTokens), adaptiveInputEstimate = plan.Sum(x => (long)x.EstimatedInputTokens),
                managedHeapAfterRun = GC.GetTotalMemory(false), processPeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64,
                note = "Synthetic fixture/mocks, snapshot I/O only; not actual model latency/token use or private-project file rereads. Heap/peak include testhost and both plans; snapshots remain O(history) per checkpoint." }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
