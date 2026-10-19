using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>A live run re-sent the same batch on every Resume and stored a new copy of each valid proposal.</summary>
public sealed class SemanticResumeDedupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-semantic-resume-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class Fake(Func<SemanticSurveyRequest, SemanticResponse> answer, IReadOnlyList<SemanticSurveyRequest> plan) : IJsonSchemaOllamaClient
    {
        public int Calls { get; private set; }
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
        {
            Calls++; var request = plan.Single(x => x.Prompt == prompt);
            return Task.FromResult(new OllamaCompletion(SemanticFixture.Json(answer(request)), true, "stop", new GenerationMetrics()));
        }
    }

    private async Task<(SemanticSurveyStore Store, SemanticFixture S, IReadOnlyList<SemanticSurveyRequest> Plan)> Create(string name)
    {
        var repo = new ProjectRepository(); var session = await repo.CreateAsync(Path.Combine(_root, name), "Synthetic resume " + name);
        var s = new SemanticFixture(AdaptiveSemanticTests.Windows(2)); s.Memory.ProjectId = session.Manifest.ProjectId;
        var chapter = TranslationService.CreateChapter("Synthetic", s.Sources[0].OriginalText); await repo.AddChapterAsync(session, chapter, 1);
        s.Sources[0] = new(chapter.Id, 1, chapter.OriginalText);
        var plan = SemanticSurveyRequests.Plan(s.Memory, s.Sources, new() { ProjectId = s.Memory.ProjectId }, "mock:selected", s.Options, batching: new(2));
        Assert.Equal(2, Assert.Single(plan).Units.Count);
        return (new(session), s, plan);
    }
    private static async Task<SemanticSurveyState> Run(SemanticSurveyStore store, SemanticFixture s, IReadOnlyList<SemanticSurveyRequest> plan, IJsonSchemaOllamaClient client)
    {
        var legacy = new BookIntelligenceState { ProjectId = s.Memory.ProjectId };
        return await new SemanticSurveyEngine(client).RunAsync(store, plan, s.Sources, legacy, s.Options, new(5, 1_000_000),
            SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(), legacy).Fingerprint);
    }
    private static SemanticFinding Fabricated(SemanticSurveyRequest unit) => AdaptiveSemanticTests.Name(unit) with
    {
        FindingType = SemanticFindingType.EventVerification, EventType = TranslationImpactEventType.CharacterStateChanged, EventActuality = SemanticActuality.ActualCompleted,
        SupportSpans = [new(unit.Evidence.Id, unit.Evidence.StartUtf16, 3, "不存在")]
    };
    private static int Pending(SemanticSurveyState state, IReadOnlyList<SemanticSurveyRequest> plan, SemanticFixture s) =>
        SemanticSurveyEngine.DryRun(plan, state, new() { ProjectId = s.Memory.ProjectId }).PendingEvidence;

    [Fact]
    public async Task Evidence_with_a_valid_proposal_is_analyzed_and_not_resent_even_if_another_finding_is_invalid()
    {
        var (store, s, plan) = await Create("mixed");
        var client = new Fake(r => AdaptiveSemanticTests.Response(r, AdaptiveSemanticTests.Name(r.Units[0]), Fabricated(r.Units[0]), AdaptiveSemanticTests.Name(r.Units[1])), plan);
        var state = await Run(store, s, plan, client);
        var results = state.EvidenceResults.Where(x => x.AttemptId == state.Attempts.Last().Id).ToDictionary(x => x.EvidenceId, x => x.Status);
        Assert.Equal(SemanticBatchStatus.Analyzed, results[plan[0].Units[0].Evidence.Id]);
        Assert.Contains(state.Proposals, p => !p.Audit.EvidenceValid); // The invalid one is kept for review, not hidden.
        Assert.Equal(0, Pending(state, plan, s));
        await Run(store, s, plan, client); Assert.Equal(1, client.Calls); // Resume sends nothing.
    }

    [Fact]
    public async Task Evidence_with_only_invalid_findings_still_fails_and_is_resent_on_resume()
    {
        var (store, s, plan) = await Create("invalid");
        var client = new Fake(r => AdaptiveSemanticTests.Response(r, AdaptiveSemanticTests.Name(r.Units[0]), Fabricated(r.Units[1])), plan);
        var state = await Run(store, s, plan, client);
        var results = state.EvidenceResults.Where(x => x.AttemptId == state.Attempts.Last().Id).ToDictionary(x => x.EvidenceId, x => x.Status);
        Assert.Equal(SemanticBatchStatus.Failed, results[plan[0].Units[1].Evidence.Id]);
        Assert.Equal(1, Pending(state, plan, s));
        Assert.Empty(state.Reviews); // Nothing is ever confirmed automatically.
    }

    [Fact]
    public async Task Identical_claims_differing_only_in_wording_or_local_ids_are_stored_once()
    {
        var (store, s, plan) = await Create("dedup");
        var client = new Fake(r =>
        {
            var a = AdaptiveSemanticTests.Name(r.Units[0], local: "first");
            var copy = AdaptiveSemanticTests.Name(r.Units[0], local: "second") with { Reason = "Different wording", Confidence = .4, Uncertainties = ["maybe"] };
            return AdaptiveSemanticTests.Response(r, a, copy, AdaptiveSemanticTests.Name(r.Units[1]));
        }, plan);
        var state = await Run(store, s, plan, client);
        Assert.Equal(2, state.Proposals.Count);
        Assert.Equal(2, state.Proposals.Select(p => SemanticSurveyEngine.ProposalKey(p.Audit)).Distinct().Count());
    }

    [Fact]
    public void Proposal_key_keeps_different_ranges_values_or_audit_outcomes_apart()
    {
        var s = new SemanticFixture(AdaptiveSemanticTests.Windows(2)); var r = AdaptiveSemanticTests.Plan(s, 2).Single(); var u = r.Units[0];
        var baseFinding = AdaptiveSemanticTests.Name(u);
        SemanticAuditedFinding A(SemanticFinding f, bool valid = true) => new(f, valid, valid, valid ? [] : ["SupportSpanMismatch"]);
        string key = SemanticSurveyEngine.ProposalKey(A(baseFinding));
        Assert.Equal(key, SemanticSurveyEngine.ProposalKey(A(baseFinding with { Reason = "x", Confidence = .1, SubjectIdentity = baseFinding.SubjectIdentity! with { LocalId = "zzz" } })));
        Assert.NotEqual(key, SemanticSurveyEngine.ProposalKey(A(baseFinding with { SupportSpans = [baseFinding.SupportSpans[0] with { StartUtf16 = baseFinding.SupportSpans[0].StartUtf16 + 900 }] })));
        Assert.NotEqual(key, SemanticSurveyEngine.ProposalKey(A(baseFinding with { Value = "周宁" })));
        Assert.NotEqual(key, SemanticSurveyEngine.ProposalKey(A(baseFinding with { SubjectIdentity = baseFinding.SubjectIdentity! with { Name = "周宁" } })));
        Assert.NotEqual(key, SemanticSurveyEngine.ProposalKey(A(baseFinding, valid: false)));
    }
}
