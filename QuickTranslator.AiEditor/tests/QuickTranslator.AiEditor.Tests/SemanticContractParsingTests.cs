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

public sealed class SemanticContractParsingTests
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
}
