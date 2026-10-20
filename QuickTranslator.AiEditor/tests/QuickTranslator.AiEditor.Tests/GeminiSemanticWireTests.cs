using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GeminiSemanticWireTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-gemini-wire-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Bodies.Add(await request.Content!.ReadAsStringAsync(ct)); return await respond(request); }
    }
    private static HttpResponseMessage Envelope(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } } }),
            Encoding.UTF8, "application/json")
    };

    // Every role, alias/relationship/addressing/event field and both null and non-null timing.
    private static (SemanticFixture S, SemanticSurveyRequest R, SemanticResponse Response) Rich()
    {
        var s = new SemanticFixture(); var r = s.Plan().Single(); var e = r.Evidence.Id; Guid known = Guid.NewGuid();
        SemanticIdentity Person(string local, string name, Guid? id = null) => new(id, local, name, MemoryEntityKind.Character, id is null ? SemanticResolution.NewIdentity : SemanticResolution.Existing);
        var span = new SemanticSupport(e, 0, 2, "林青");
        var relation = new SemanticFinding
        {
            FindingType = SemanticFindingType.RelationshipExtraction, SourceEvidenceIds = [e], SupportSpans = [span, new(e, 10, 2, "周宁")],
            SubjectIdentity = Person("p1", "林青"), TargetIdentity = Person("p2", "周宁", known), IdentityResolutionStatus = SemanticResolution.Existing,
            FactKind = MemoryFactKind.Relationship, Value = "父亲", RelationshipType = KnowledgeRelationType.BiologicalFather,
            EventType = TranslationImpactEventType.RelationshipEstablished, EventActuality = SemanticActuality.HistoricalReference, EventEffect = MemoryEventEffect.Activate,
            EffectiveChapter = null, EffectiveToChapter = 3, RevealedChapter = 1, SpeakerIdentity = Person("p1", "林青"), ListenerIdentity = Person("p3", "某人"),
            SelfReference = "我", AddresseeReference = "你", VerifiedParticipants = [Person("p1", "林青"), Person("p2", "周宁", known)],
            CpuCandidateIds = [Guid.NewGuid()], AffectedFactIds = [Guid.NewGuid()], Confidence = .7, Uncertainties = ["timing unknown"], Reason = "Explicit kinship statement"
        };
        var name = s.Name(r); // No target/speaker/listener, no event: every optional role absent.
        return (s, r, SemanticFixture.Response(r, relation, name));
    }

    [Fact]
    public void Wire_schema_uses_only_documented_core_keywords_no_unions_numeric_enums_or_bounds()
    {
        var keywords = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement node)
        {
            foreach (var p in node.EnumerateObject()) keywords.Add(p.Name);
            Assert.Equal(JsonValueKind.String, node.GetProperty("type").ValueKind); // No ["T","null"] type arrays.
            if (node.TryGetProperty("enum", out var choices)) { Assert.Equal("string", node.GetProperty("type").GetString()); Assert.All(choices.EnumerateArray(), x => Assert.Equal(JsonValueKind.String, x.ValueKind)); }
            if (node.TryGetProperty("properties", out var props))
            {
                Assert.Equal(props.EnumerateObject().Select(x => x.Name), node.GetProperty("required").EnumerateArray().Select(x => x.GetString())); // Everything required.
                foreach (var p in props.EnumerateObject()) Walk(p.Value);
            }
            if (node.TryGetProperty("items", out var items)) Walk(items);
        }
        Walk(GeminiSemanticWire.Schema);
        Assert.Subset(new HashSet<string> { "type", "properties", "required", "items", "enum" }, keywords);
        Assert.DoesNotContain("anyOf", GeminiSemanticWire.Schema.GetRawText());
        // Internal schema still carries every bound and union (unchanged contract).
        string internalSchema = SemanticSurveyContract.MultiEvidenceSchema.GetRawText();
        Assert.Contains("anyOf", internalSchema); Assert.Contains("maxLength", internalSchema); Assert.Contains("\"enum\":[2]", internalSchema);
        Assert.True(GeminiSemanticWire.IsInternalSchema(SemanticSurveyContract.ResponseSchema));
        Assert.True(GeminiSemanticWire.IsInternalSchema(SemanticSurveyContract.MultiEvidenceSchema));
        Assert.False(GeminiSemanticWire.IsInternalSchema(GeminiSemanticWire.Schema));
    }

    [Fact]
    public void Wire_covers_every_internal_finding_field_and_enum_value()
    {
        var internalFinding = SemanticSurveyContract.MultiEvidenceSchema.GetProperty("properties").GetProperty("findings").GetProperty("items").GetProperty("properties");
        var wireFinding = GeminiSemanticWire.Schema.GetProperty("properties").GetProperty("findings").GetProperty("items").GetProperty("properties");
        string[] roles = ["subject_identity", "target_identity", "speaker_identity", "listener_identity", "verified_participants"];
        foreach (var field in internalFinding.EnumerateObject())
        {
            if (roles.Contains(field.Name)) continue;
            Assert.True(wireFinding.TryGetProperty(field.Name, out var wire), field.Name);
            if (field.Value.TryGetProperty("enum", out var choices))
                Assert.Subset(wire.GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToHashSet(), choices.EnumerateArray().Select(x => x.GetString()).ToHashSet());
        }
        Assert.Equal(Enum.GetNames<GeminiSemanticWire.IdentityRole>(), wireFinding.GetProperty("identities").GetProperty("items").GetProperty("properties").GetProperty("role").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains(GeminiSemanticWire.NoEventType, wireFinding.GetProperty("event_type").GetProperty("enum").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void Round_trip_preserves_identities_roles_relationships_addressing_events_and_evidence_exactly()
    {
        var (_, _, response) = Rich(); string internalJson = SemanticFixture.Json(response);
        string wire = GeminiSemanticWire.Encode(internalJson);
        Assert.DoesNotContain("subject_identity", wire); Assert.Contains("\"role\":\"Participant\"", wire);
        string decoded = GeminiSemanticWire.Decode(wire, 1);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(internalJson), JsonNode.Parse(decoded)));
        var parsed = SemanticSurveyContract.Parse(decoded);
        Assert.Equal(response.Findings[0] with { }, parsed.Findings[0], new FindingComparer());
        Assert.Null(parsed.Findings[1].TargetIdentity); Assert.Null(parsed.Findings[1].SpeakerIdentity); Assert.Null(parsed.Findings[1].EventType);
        Assert.Null(parsed.Findings[0].EffectiveChapter); Assert.Equal(3, parsed.Findings[0].EffectiveToChapter);
        Assert.Equal(2, parsed.Findings[0].VerifiedParticipants.Count); Assert.Null(parsed.Findings[0].VerifiedParticipants[0].EntityId);
    }
    private sealed class FindingComparer : IEqualityComparer<SemanticFinding>
    {
        public bool Equals(SemanticFinding? x, SemanticFinding? y) => JsonSerializer.Serialize(x, SemanticSurveyContract.JsonOptions) == JsonSerializer.Serialize(y, SemanticSurveyContract.JsonOptions);
        public int GetHashCode(SemanticFinding obj) => 0;
    }

    [Fact]
    public void Decoder_fails_closed_on_ambiguous_roles_missing_unknown_duplicate_fields_and_wrong_version()
    {
        var (_, _, response) = Rich(); string wire = GeminiSemanticWire.Encode(SemanticFixture.Json(response));
        JsonObject Finding(JsonObject root) => root["findings"]![0]!.AsObject();
        JsonObject Identity(JsonObject root) => Finding(root)["identities"]![0]!.AsObject();
        foreach (Action<JsonObject> change in new Action<JsonObject>[] {
            n => Finding(n)["identities"]!.AsArray().Add(Identity(n).DeepClone()), // Two Subjects: never pick one.
            n => Identity(n)["role"] = "Narrator", n => Identity(n)["role"] = "0", n => Identity(n)["role"] = "subject",
            n => Identity(n)["entity_id"] = null, n => Identity(n)["confirmed"] = true, n => Identity(n).Remove("status"),
            n => Finding(n).Remove("reason"), n => Finding(n)["subject_identity"] = null, n => Finding(n)["review_status"] = "Confirmed",
            n => Finding(n)["event_type"] = null, n => n["schema_version"] = 2, n => n["schema_version"] = "1", n => n.Remove("findings"),
            n => Finding(n)["support_spans"]![0]!.AsObject().Remove("text") })
        {
            var node = JsonNode.Parse(wire)!.AsObject(); change(node);
            Assert.Throws<InvalidDataException>(() => SemanticSurveyContract.Parse(GeminiSemanticWire.Decode(node.ToJsonString(), 1)));
        }
        Assert.Throws<InvalidDataException>(() => GeminiSemanticWire.Decode(wire.Replace("\"schema_version\":1", "\"schema_version\":1,\"schema_version\":1", StringComparison.Ordinal), 1));
        Assert.Throws<InvalidDataException>(() => GeminiSemanticWire.Decode("```json\n" + wire + "\n```", 1));
        // The old internal shape is not silently accepted through the Gemini path.
        Assert.Throws<InvalidDataException>(() => GeminiSemanticWire.Decode(SemanticFixture.Json(response), 1));
    }

    [Fact]
    public void Bounds_removed_from_wire_are_still_enforced_by_internal_contract_after_decoding()
    {
        var (_, _, response) = Rich(); string wire = GeminiSemanticWire.Encode(SemanticFixture.Json(response));
        JsonObject Finding(JsonObject root) => root["findings"]![0]!.AsObject();
        foreach (Action<JsonObject> change in new Action<JsonObject>[] {
            n => Finding(n)["support_spans"]![0]!["text"] = new string('林', 241), n => Finding(n)["support_spans"]![0]!["length_utf16"] = 241,
            n => Finding(n)["support_spans"]![0]!["start_utf16"] = -1, n => Finding(n)["reason"] = new string('x', 1001),
            n => Finding(n)["confidence"] = 1.5, n => Finding(n)["effective_chapter"] = -1, n => Finding(n)["identities"]![0]!["local_id"] = new string('x', 65),
            n => Finding(n)["identities"]![0]!["entity_id"] = "not-a-guid", n => Finding(n)["event_type"] = "Explosion",
            n => n["findings"] = new JsonArray(Enumerable.Range(0, 65).Select(_ => (JsonNode)Finding(n).DeepClone()).ToArray()),
            n => Finding(n)["identities"] = new JsonArray(Enumerable.Range(0, 9).Select(_ => (JsonNode)new JsonObject { ["role"] = "Participant", ["entity_id"] = "", ["local_id"] = "p", ["name"] = "n", ["kind"] = "Character", ["status"] = "NewIdentity" }).ToArray()) })
        {
            var node = JsonNode.Parse(wire)!.AsObject(); change(node);
            Assert.ThrowsAny<Exception>(() => SemanticSurveyContract.Parse(GeminiSemanticWire.Decode(node.ToJsonString(), 1)));
        }
    }

    [Fact]
    public async Task Gemini_backend_sends_wire_and_ollama_backend_keeps_internal_schema()
    {
        var options = new ModelOptions { Model = "gemini-3.6-flash", ContextSize = 65536, MaximumOutputTokens = 4096 };
        var gemini = new Handler(_ => Task.FromResult(Envelope("{}")));
        using (var client = SemanticSurveyBackend.Gemini("FAKE-ONLY", Guid.NewGuid(), options, new(5), gemini))
        {
            await client.TranslateStructuredAsync("neutral", options, SemanticSurveyContract.MultiEvidenceSchema);
            Assert.Equal(GeminiSemanticWire.Version, client.WireFormat);
            using var body = JsonDocument.Parse(gemini.Bodies.Single());
            var config = body.RootElement.GetProperty("generationConfig");
            Assert.Equal(GeminiSemanticWire.Schema.GetRawText(), config.GetProperty("responseJsonSchema").GetRawText());
            Assert.Equal("application/json", config.GetProperty("responseMimeType").GetString());
            Assert.Equal("neutral" + GeminiSemanticWire.PromptAddendum, body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.DoesNotContain("FAKE-ONLY", gemini.Bodies.Single());
        }
        var ollama = new Handler(_ => throw new InvalidOperationException("not reached"));
        using var local = SemanticSurveyBackend.Ollama(OllamaBackendProfile.CreateDefaultLocal(), options with { Model = "mock-model" }, new(1), ollama);
        Assert.Equal("internal", local.WireFormat);
        Assert.Equal("{\"x\":1}", local.DecodeResponse("{\"x\":1}", 2));
    }
}
