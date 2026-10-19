using System.Text.Json;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Tests;

internal sealed class SemanticFixture
{
    public EntityMemoryState Memory { get; } = new() { ProjectId = Guid.NewGuid() };
    public List<CensusSource> Sources { get; } = [];
    public ModelOptions Options { get; } = new() { Model = "mock-semantic", ContextSize = 32768, MaximumOutputTokens = 4096 };
    public SemanticFixture(string text = "林青说道：我叫林青。周宁是我的父亲。") => Sources.Add(new(Guid.NewGuid(), 1, text));
    public IReadOnlyList<SemanticSurveyRequest> Plan() => SemanticSurveyRequests.Plan(Memory, Sources, new() { ProjectId = Memory.ProjectId }, "mock:selected", Options);
    public SemanticFinding Name(SemanticSurveyRequest request, string name = "林青", string local = "person_1", MemoryEntityKind kind = MemoryEntityKind.Character) => new()
    {
        FindingType = SemanticFindingType.CharacterIdentification, IdentityResolutionStatus = SemanticResolution.NewIdentity,
        SubjectIdentity = new(null, local, name, kind, SemanticResolution.NewIdentity), FactKind = MemoryFactKind.PrimaryName, Value = name,
        SourceEvidenceIds = [request.Evidence.Id], SupportSpans = [new(request.Evidence.Id, Sources.Single(x => x.ChapterId == request.Evidence.ChapterId).OriginalText.IndexOf(name, StringComparison.Ordinal), name.Length, name)],
        Confidence = 0.8, Reason = "Explicit source spelling; requires human confirmation", EffectiveChapter = 1, RevealedChapter = 1
    };
    public static SemanticResponse Response(SemanticSurveyRequest request, params SemanticFinding[] findings) => new(1, request.ProjectId, request.Evidence.ChapterId, [request.Evidence.Id], findings);
    public static string Json(SemanticResponse response) => JsonSerializer.Serialize(response, SemanticSurveyContract.JsonOptions);
}

public sealed class SemanticContractTests
{
    [Fact]
    public void Strict_schema_rejects_missing_unknown_duplicate_fields_enum_numbers_null_participants_and_invalid_dates()
    {
        var s = new SemanticFixture(); var request = s.Plan().Single(); string valid = SemanticFixture.Json(SemanticFixture.Response(request, s.Name(request)));
        Assert.Single(SemanticSurveyContract.Parse(valid).Findings);
        foreach (Action<JsonObject> change in new Action<JsonObject>[] {
            n => n.Remove("reviewed_evidence_ids"), n => n["confirmed"] = true,
            n => n["findings"]![0]!.AsObject().Remove("reason"), n => n["findings"]![0]!["finding_type"] = 0,
            n => n["findings"]![0]!["verified_participants"] = new JsonArray((JsonNode?)null),
            n => n["findings"]![0]!["effective_chapter"] = 0 })
        {
            var node = JsonNode.Parse(valid)!.AsObject(); change(node); Assert.Throws<InvalidDataException>(() => SemanticSurveyContract.Parse(node.ToJsonString()));
        }
        Assert.Throws<InvalidDataException>(() => SemanticSurveyContract.Parse(valid.Replace("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => SemanticSurveyContract.Parse("```json\n" + valid + "\n```"));
    }

    [Fact]
    public void Independent_evidence_auditor_preserves_invalid_findings_with_diagnostics_not_silent_success()
    {
        var s = new SemanticFixture(); var request = s.Plan().Single(); var good = s.Name(request);
        Assert.True(SemanticQualityAuditor.Audit(SemanticFixture.Response(request, good), request, s.Sources).Validated);
        foreach (var bad in new[] { good with { SourceEvidenceIds = [Guid.NewGuid()] }, good with { SupportSpans = [good.SupportSpans[0] with { Text = "虚构" }] },
            good with { SubjectIdentity = good.SubjectIdentity! with { EntityId = Guid.NewGuid(), LocalId = "", Status = SemanticResolution.Existing } },
            good with { RevealedChapter = 50 }, good with { EffectiveChapter = 2 } })
        {
            var audit = SemanticQualityAuditor.Audit(SemanticFixture.Response(request, bad), request, s.Sources);
            Assert.False(audit.Validated); var kept = Assert.Single(audit.Findings); Assert.False(kept.EvidenceValid); Assert.NotEmpty(kept.Issues); Assert.Empty(kept.Finding.SupportSpans[0].Text);
        }
        Assert.False(SemanticQualityAuditor.Audit(SemanticFixture.Response(request, good) with { ProjectId = Guid.NewGuid() }, request, s.Sources).Validated);
        Assert.False(SemanticQualityAuditor.Audit(SemanticFixture.Response(request, good), request, [s.Sources[0] with { OriginalText = "源已改" }]).Validated);
    }

    [Theory]
    [InlineData("其实是天气的原因。", SemanticActuality.NotAnEvent, false)]
    [InlineData("林青去年就结婚了。", SemanticActuality.HistoricalReference, true)]
    [InlineData("林青提出离婚。", SemanticActuality.PlannedRequested, false)]
    [InlineData("如果林青离婚就离开。", SemanticActuality.HypotheticalConditional, false)]
    [InlineData("林青没有离婚。", SemanticActuality.Negated, false)]
    [InlineData("林青希望恢复记忆。", SemanticActuality.PlannedRequested, false)]
    [InlineData("林青今天恢复记忆了。", SemanticActuality.ActualCompleted, true)]
    [InlineData("听说林青也许结婚了。", SemanticActuality.Uncertain, false)]
    public void Gold_actuality_labels_do_not_turn_nonactual_events_into_promotable_truth(string text, SemanticActuality label, bool eligible)
    {
        var s = new SemanticFixture(text); var request = s.Plan().Single();
        var person = new SemanticIdentity(null, "p", "林青", MemoryEntityKind.Character, SemanticResolution.NewIdentity);
        var finding = new SemanticFinding { FindingType = SemanticFindingType.EventVerification, EventType = TranslationImpactEventType.CharacterStateChanged,
            EventActuality = label, IdentityResolutionStatus = SemanticResolution.NewIdentity, VerifiedParticipants = text.Contains("林青", StringComparison.Ordinal) ? [person] : [],
            SourceEvidenceIds = [request.Evidence.Id], SupportSpans = [new(request.Evidence.Id, 0, text.Length, text)], Reason = "Gold-labelled mock classification", Confidence = .7 };
        var audited = Assert.Single(SemanticQualityAuditor.Audit(SemanticFixture.Response(request, finding), request, s.Sources).Findings);
        Assert.True(audited.EvidenceValid); Assert.Equal(eligible, audited.CanPromote);
        if (label == SemanticActuality.HistoricalReference) Assert.Null(audited.Finding.EffectiveChapter);
    }

    [Fact]
    public void Chapter_thirty_prompt_omits_future_names_facet_labels_and_unapproved_hypotheses()
    {
        var s = new MemoryScenario(); Guid proof = s.Source(1, "蒙面人说话。"), reveal = s.Source(50, "蒙面人其实是隐名甲。"), identity = s.Entity("secret", "隐名甲", MemoryEntityKind.Consciousness);
        var alias = s.Fact(identity, MemoryFactKind.Alias, "蒙面人", proof, new(1, null, 1, 1)); s.Confirm(alias.Id);
        var hidden = s.Fact(identity, MemoryFactKind.PrimaryName, "隐名甲", reveal, new(1, null, 50, 50)); s.Confirm(hidden.Id);
        s.Source(30, "蒙面人走在路上。");
        var plan = SemanticSurveyRequests.Plan(s.State, s.Sources, new() { ProjectId = s.State.ProjectId }, "mock", new() { ContextSize = 32768 });
        var at30 = plan.Single(x => x.ChapterNumber == 30);
        Assert.DoesNotContain("隐名甲", at30.Prompt); Assert.DoesNotContain("Consciousness", at30.Prompt);
        Assert.Contains("蒙面人", at30.AllowedIdentities.Single().Names);
        Assert.Contains("隐名甲", plan.Single(x => x.ChapterNumber == 50).AllowedIdentities.Single().Names);
    }

    [Fact]
    public void Unknown_speaker_listener_and_lexical_nearby_names_are_not_verified_participants()
    {
        var s = new SemanticFixture("林青和周宁站在一边。有人说：我们离婚吧。"); var request = s.Plan().Single();
        var finding = new SemanticFinding { FindingType = SemanticFindingType.DirectionalAddressing, SourceEvidenceIds = [request.Evidence.Id],
            SupportSpans = [new(request.Evidence.Id, 0, s.Sources[0].OriginalText.Length, s.Sources[0].OriginalText)], IdentityResolutionStatus = SemanticResolution.Ambiguous, Reason = "Speaker unresolved" };
        var audit = Assert.Single(SemanticQualityAuditor.Audit(SemanticFixture.Response(request, finding), request, s.Sources).Findings);
        Assert.True(audit.EvidenceValid); Assert.False(audit.CanPromote); Assert.Empty(audit.Finding.VerifiedParticipants);
    }
}
