using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>Optional response translation owned by a backend. Ollama returns the internal contract unchanged.</summary>
public interface ISemanticResponseDecoder
{
    string DecodeResponse(string providerText, int wireVersion);
}

/// <summary>
/// Gemini-only transport encoding of the internal semantic contract (v1/v2).
/// The internal schema compiles to a constrained-decoding grammar Gemini rejects with
/// HTTP 400 INVALID_ARGUMENT: integer <c>enum</c> (schema_version), 13 nullable
/// <c>anyOf</c> unions (five inlined identity objects) and bounded arrays of a
/// 25-field object. This wire uses only the documented core subset
/// (type/properties/required/items/string enum) and no unions: roles become one
/// identity list, nulls become explicit sentinels. Every bound stays enforced by
/// <see cref="SemanticSurveyContract.Parse"/> after <see cref="Decode"/>; the decoder is
/// strict and never fills a missing field. Batch IDs, checkpoints and the auditor
/// are unchanged because the engine only ever sees internal JSON.
/// </summary>
public static class GeminiSemanticWire
{
    public const string Version = "gemini-semantic-wire-1";
    public enum IdentityRole { Subject, Target, Speaker, Listener, Participant }
    public const string NoEventType = "None";
    private static readonly (string Wire, IdentityRole Role)[] SingleRoles =
        [("subject_identity", IdentityRole.Subject), ("target_identity", IdentityRole.Target),
         ("speaker_identity", IdentityRole.Speaker), ("listener_identity", IdentityRole.Listener)];
    private static readonly string[] ChapterFields = ["effective_chapter", "effective_to_chapter", "revealed_chapter"];
    private static readonly string[] IdentityFields = ["role", "entity_id", "local_id", "name", "kind", "status"];
    private static readonly string[] RootFields = ["schema_version", "project_id", "chapter_id", "reviewed_evidence_ids", "findings"];
    internal static readonly string[] FindingFields = ["finding_type", "source_evidence_ids", "support_spans", "identities",
        "identity_resolution_status", "fact_kind", "value", "relationship_type", "event_type", "event_actuality", "event_effect",
        "effective_chapter", "effective_to_chapter", "revealed_chapter", "self_reference", "addressee_reference",
        "cpu_candidate_ids", "affected_fact_ids", "confidence", "uncertainties", "reason"];
    private static readonly string[] SpanFields = ["evidence_id", "start_utf16", "length_utf16", "text"];

    public static JsonElement Schema { get; } = CreateSchema();

    public static string PromptAddendum { get; } = $"""

        GEMINI WIRE FORMAT {Version} (transport encoding only; every rule above still applies and the result is validated against the full internal contract, otherwise the whole batch is rejected):
        - schema_version is the integer stated above. project_id, chapter_id and every evidence/candidate/fact ID are exact GUID strings copied from this prompt.
        - People are listed per finding in "identities". role Subject, Target, Speaker and Listener appear at most once each; role Participant appears once per VerifiedParticipant. Omit a role rather than guessing it. entity_id is "" unless the ID comes from confirmed_identities.
        - event_type "{NoEventType}" means no event type. effective_chapter, effective_to_chapter and revealed_chapter use 0 for unknown/null timing; never guess a chapter.
        - Use "" for an empty value/self_reference/addressee_reference and [] for empty lists.
        - Local limits: at most 64 findings, 16 reviewed_evidence_ids, 8 source_evidence_ids, 8 support_spans, 8 Participant identities, 16 cpu_candidate_ids, 8 affected_fact_ids, 8 uncertainties; support text <=240 UTF-16 units with length_utf16 1..240 and start_utf16 >=0; value <=200, name/self_reference/addressee_reference <=100, local_id <=64, uncertainty <=240, reason <=1000 characters; confidence 0..1; known chapters >=1.
        """;

    private static JsonObject Str() => new() { ["type"] = "string" };
    private static JsonObject Int() => new() { ["type"] = "integer" };
    private static JsonObject Choice(IEnumerable<string> names) => new() { ["type"] = "string", ["enum"] = new JsonArray(names.Select(x => (JsonNode)x).ToArray()) };
    private static JsonObject Choice<T>() where T : struct, Enum => Choice(Enum.GetNames<T>());
    private static JsonObject ArrayOf(JsonNode item) => new() { ["type"] = "array", ["items"] = item };
    private static JsonObject Obj(params (string Name, JsonNode Schema)[] fields)
    {
        var properties = new JsonObject(); foreach (var (name, schema) in fields) properties[name] = schema;
        return new() { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray(fields.Select(x => (JsonNode)x.Name).ToArray()) };
    }
    private static JsonElement CreateSchema() => JsonSerializer.SerializeToElement(Obj(
        ("schema_version", Int()), ("project_id", Str()), ("chapter_id", Str()), ("reviewed_evidence_ids", ArrayOf(Str())),
        ("findings", ArrayOf(Obj(
            ("finding_type", Choice<SemanticFindingType>()), ("source_evidence_ids", ArrayOf(Str())),
            ("support_spans", ArrayOf(Obj(("evidence_id", Str()), ("start_utf16", Int()), ("length_utf16", Int()), ("text", Str())))),
            ("identities", ArrayOf(Obj(("role", Choice<IdentityRole>()), ("entity_id", Str()), ("local_id", Str()), ("name", Str()),
                ("kind", Choice<MemoryEntityKind>()), ("status", Choice<SemanticResolution>())))),
            ("identity_resolution_status", Choice<SemanticResolution>()), ("fact_kind", Choice<MemoryFactKind>()), ("value", Str()),
            ("relationship_type", Choice<KnowledgeRelationType>()),
            ("event_type", Choice(Enum.GetNames<TranslationImpactEventType>().Append(NoEventType))),
            ("event_actuality", Choice<SemanticActuality>()), ("event_effect", Choice<MemoryEventEffect>()),
            ("effective_chapter", Int()), ("effective_to_chapter", Int()), ("revealed_chapter", Int()),
            ("self_reference", Str()), ("addressee_reference", Str()), ("cpu_candidate_ids", ArrayOf(Str())), ("affected_fact_ids", ArrayOf(Str())),
            ("confidence", new JsonObject { ["type"] = "number" }), ("uncertainties", ArrayOf(Str())), ("reason", Str()))))));

    /// <summary>True only for the exact internal schemas the engine sends (never a caller-supplied lookalike).</summary>
    public static bool IsInternalSchema(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object && (schema.GetRawText() == SemanticSurveyContract.ResponseSchema.GetRawText() ||
            schema.GetRawText() == SemanticSurveyContract.MultiEvidenceSchema.GetRawText());

    /// <summary>Strict wire → internal JSON. Throws <see cref="InvalidDataException"/>; the result must still pass <see cref="SemanticSurveyContract.Parse"/>.</summary>
    public static string Decode(string wireJson, int expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(wireJson) || wireJson.Length > 200_000) throw new InvalidDataException("Semantic response empty/oversized.");
        JsonDocument document;
        try { document = JsonDocument.Parse(wireJson, new() { MaxDepth = 32 }); }
        catch (JsonException ex) { throw new InvalidDataException("Gemini semantic wire is not valid JSON.", ex); }
        using (document)
        {
            var root = Fields(document.RootElement, RootFields, "$root");
            var version = root["schema_version"];
            if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int v) || v != expectedVersion) Fail("$root.schema_version");
            var findings = new JsonArray();
            foreach (var item in Array(root["findings"], "$root.findings")) findings.Add(DecodeFinding(item));
            return new JsonObject
            {
                ["schema_version"] = Copy(version), ["project_id"] = Copy(root["project_id"]), ["chapter_id"] = Copy(root["chapter_id"]),
                ["reviewed_evidence_ids"] = Copy(root["reviewed_evidence_ids"]), ["findings"] = findings
            }.ToJsonString();
        }
    }

    private static JsonObject DecodeFinding(JsonElement element)
    {
        const string path = "$root.findings[]";
        var f = Fields(element, FindingFields, path);
        var singles = new Dictionary<IdentityRole, JsonNode>(); var participants = new JsonArray();
        foreach (var identity in Array(f["identities"], path + ".identities"))
        {
            var i = Fields(identity, IdentityFields, path + ".identities[]");
            var role = i["role"]; IdentityRole parsed = default;
            if (role.ValueKind != JsonValueKind.String || !Enum.TryParse(role.GetString(), ignoreCase: false, out parsed) ||
                !Enum.IsDefined(parsed) || role.GetString() != parsed.ToString()) Fail(path + ".identities[].role");
            var entity = i["entity_id"];
            if (entity.ValueKind != JsonValueKind.String) Fail(path + ".identities[].entity_id");
            var mapped = new JsonObject
            {
                ["entity_id"] = entity.GetString()!.Length == 0 ? null : Copy(entity), ["local_id"] = Copy(i["local_id"]),
                ["name"] = Copy(i["name"]), ["kind"] = Copy(i["kind"]), ["status"] = Copy(i["status"])
            };
            if (parsed == IdentityRole.Participant) participants.Add(mapped);
            else if (!singles.TryAdd(parsed, mapped)) Fail(path + ".identities[" + parsed + "]"); // Never pick one of two speakers.
        }
        var eventType = f["event_type"];
        if (eventType.ValueKind != JsonValueKind.String) Fail(path + ".event_type");
        JsonNode? Chapter(string name) => f[name] is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out int c) && c == 0 ? null : Copy(f[name]);
        JsonNode? Role(IdentityRole role) => singles.GetValueOrDefault(role);
        return new JsonObject
        {
            ["finding_type"] = Copy(f["finding_type"]), ["source_evidence_ids"] = Copy(f["source_evidence_ids"]), ["support_spans"] = Spans(f["support_spans"]),
            ["subject_identity"] = Role(IdentityRole.Subject), ["target_identity"] = Role(IdentityRole.Target),
            ["identity_resolution_status"] = Copy(f["identity_resolution_status"]), ["fact_kind"] = Copy(f["fact_kind"]), ["value"] = Copy(f["value"]),
            ["relationship_type"] = Copy(f["relationship_type"]), ["event_type"] = eventType.GetString() == NoEventType ? null : Copy(eventType),
            ["event_actuality"] = Copy(f["event_actuality"]), ["event_effect"] = Copy(f["event_effect"]),
            ["effective_chapter"] = Chapter("effective_chapter"), ["effective_to_chapter"] = Chapter("effective_to_chapter"), ["revealed_chapter"] = Chapter("revealed_chapter"),
            ["speaker_identity"] = Role(IdentityRole.Speaker), ["listener_identity"] = Role(IdentityRole.Listener),
            ["self_reference"] = Copy(f["self_reference"]), ["addressee_reference"] = Copy(f["addressee_reference"]), ["verified_participants"] = participants,
            ["cpu_candidate_ids"] = Copy(f["cpu_candidate_ids"]), ["affected_fact_ids"] = Copy(f["affected_fact_ids"]),
            ["confidence"] = Copy(f["confidence"]), ["uncertainties"] = Copy(f["uncertainties"]), ["reason"] = Copy(f["reason"])
        };
    }

    private static JsonArray Spans(JsonElement spans)
    {
        var result = new JsonArray();
        foreach (var span in Array(spans, "$root.findings[].support_spans"))
        {
            var s = Fields(span, SpanFields, "$root.findings[].support_spans[]");
            result.Add(new JsonObject { ["evidence_id"] = Copy(s["evidence_id"]), ["start_utf16"] = Copy(s["start_utf16"]), ["length_utf16"] = Copy(s["length_utf16"]), ["text"] = Copy(s["text"]) });
        }
        return result;
    }

    /// <summary>Internal → wire. Used by mocks/probes; Decode(Encode(x)) must reproduce x.</summary>
    public static string Encode(string internalJson)
    {
        var root = JsonNode.Parse(internalJson)!.AsObject();
        var findings = new JsonArray();
        foreach (var finding in root["findings"]!.AsArray())
        {
            var f = finding!.AsObject(); var identities = new JsonArray();
            JsonObject Identity(JsonNode node, IdentityRole role) => new()
            {
                ["role"] = role.ToString(), ["entity_id"] = node["entity_id"]?.DeepClone() ?? "", ["local_id"] = node["local_id"]!.DeepClone(),
                ["name"] = node["name"]!.DeepClone(), ["kind"] = node["kind"]!.DeepClone(), ["status"] = node["status"]!.DeepClone()
            };
            foreach (var (wire, role) in SingleRoles) if (f[wire] is { } node) identities.Add(Identity(node, role));
            foreach (var participant in f["verified_participants"]!.AsArray()) identities.Add(Identity(participant!, IdentityRole.Participant));
            var mapped = new JsonObject();
            foreach (string name in FindingFields)
                mapped[name] = name switch
                {
                    "identities" => identities,
                    "event_type" => f[name]?.DeepClone() ?? NoEventType,
                    _ when ChapterFields.Contains(name) => f[name]?.DeepClone() ?? 0,
                    _ => f[name]!.DeepClone()
                };
            findings.Add(mapped);
        }
        root["findings"] = findings;
        return root.ToJsonString();
    }

    private static Dictionary<string, JsonElement> Fields(JsonElement element, string[] expected, string path)
    {
        if (element.ValueKind != JsonValueKind.Object) Fail(path);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!expected.Contains(property.Name, StringComparer.Ordinal) || !result.TryAdd(property.Name, property.Value)) Fail(path + "." + property.Name);
        if (result.Count != expected.Length) Fail(path); // Missing fields are never defaulted.
        return result;
    }
    private static JsonElement.ArrayEnumerator Array(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array) Fail(path);
        return element.EnumerateArray();
    }
    private static JsonNode? Copy(JsonElement element) => JsonNode.Parse(element.GetRawText());
    private static void Fail(string path) => throw new InvalidDataException("Gemini semantic wire mismatch at " + path);
}
