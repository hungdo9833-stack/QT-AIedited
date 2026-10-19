using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public enum SemanticFindingType { CharacterIdentification, AliasAndIdentityResolution, RelationshipExtraction, DirectionalAddressing, EventVerification, GapAndContradictionAudit }
public enum SemanticActuality { ActualCompleted, HistoricalReference, PlannedRequested, HypotheticalConditional, Negated, Uncertain, NotAnEvent }
public enum SemanticResolution { Existing, NewIdentity, Unresolved, Ambiguous }
public enum SemanticBatchStatus { Pending, Blocked, Failed, Analyzed, Validated }
public sealed record SemanticIdentity(Guid? EntityId, string LocalId, string Name, MemoryEntityKind Kind, SemanticResolution Status);
public sealed record SemanticSupport(Guid EvidenceId, int StartUtf16, int LengthUtf16, string Text);

public sealed record SemanticFinding
{
    public SemanticFindingType FindingType { get; init; }
    public IReadOnlyList<Guid> SourceEvidenceIds { get; init; } = [];
    public IReadOnlyList<SemanticSupport> SupportSpans { get; init; } = [];
    public SemanticIdentity? SubjectIdentity { get; init; }
    public SemanticIdentity? TargetIdentity { get; init; }
    public SemanticResolution IdentityResolutionStatus { get; init; }
    public MemoryFactKind FactKind { get; init; } = MemoryFactKind.PrimaryName;
    public string Value { get; init; } = "";
    public KnowledgeRelationType RelationshipType { get; init; }
    public TranslationImpactEventType? EventType { get; init; }
    public SemanticActuality EventActuality { get; init; } = SemanticActuality.NotAnEvent;
    public MemoryEventEffect EventEffect { get; init; }
    public int? EffectiveChapter { get; init; }
    public int? EffectiveToChapter { get; init; }
    public int? RevealedChapter { get; init; }
    public SemanticIdentity? SpeakerIdentity { get; init; }
    public SemanticIdentity? ListenerIdentity { get; init; }
    public string SelfReference { get; init; } = "";
    public string AddresseeReference { get; init; } = "";
    public IReadOnlyList<SemanticIdentity> VerifiedParticipants { get; init; } = [];
    public IReadOnlyList<Guid> CpuCandidateIds { get; init; } = [];
    public IReadOnlyList<Guid> AffectedFactIds { get; init; } = [];
    public double Confidence { get; init; }
    public IReadOnlyList<string> Uncertainties { get; init; } = [];
    public string Reason { get; init; } = "";
}
public sealed record SemanticResponse(int SchemaVersion, Guid ProjectId, Guid ChapterId,
    IReadOnlyList<Guid> ReviewedEvidenceIds, IReadOnlyList<SemanticFinding> Findings);

/// <summary>Strict provider-neutral wire contract. The model cannot supply a review/Confirmed status.</summary>
public static class SemanticSurveyContract
{
    public const string Version = "abi3-semantic-1";
    public const string MultiEvidenceVersion = "abi3-semantic-2";
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();
    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)); return options;
    }
    private static object Text(int max = 1000) => new { type = "string", maxLength = max };
    private static object Choice<T>() where T : struct, Enum => new { type = "string", @enum = Enum.GetNames<T>() };
    private static object Nullable(object schema) => new { anyOf = new[] { schema, new { type = "null" } } };
    private static object ArrayOf(object schema, int max = 16) => new { type = "array", maxItems = max, items = schema };
    private static object Obj(params (string Name, object Schema)[] fields) => new { type = "object", additionalProperties = false,
        properties = fields.ToDictionary(x => x.Name, x => x.Schema), required = fields.Select(x => x.Name).ToArray() };
    private static object IdentityObject() => Obj(("entity_id", Nullable(Text(36))), ("local_id", Text(64)), ("name", Text(100)),
        ("kind", Choice<MemoryEntityKind>()), ("status", Choice<SemanticResolution>()));
    private static object Identity() => Nullable(IdentityObject());
    public static JsonElement ResponseSchema { get; } = CreateSchema(1);
    public static JsonElement MultiEvidenceSchema { get; } = CreateSchema(2);
    public static JsonElement SchemaFor(int version) => version switch { 1 => ResponseSchema, 2 => MultiEvidenceSchema, _ => throw new InvalidDataException("Unsupported semantic wire version.") };
    private static JsonElement CreateSchema(int version) => JsonSerializer.SerializeToElement(Obj(
        ("schema_version", new { type = "integer", @enum = new[] { version } }), ("project_id", Text(36)), ("chapter_id", Text(36)),
        ("reviewed_evidence_ids", ArrayOf(Text(36))), ("findings", ArrayOf(Obj(
            ("finding_type", Choice<SemanticFindingType>()), ("source_evidence_ids", ArrayOf(Text(36), 8)),
            ("support_spans", ArrayOf(Obj(("evidence_id", Text(36)), ("start_utf16", new { type = "integer", minimum = 0 }),
                ("length_utf16", new { type = "integer", minimum = 1, maximum = 240 }), ("text", Text(240))), 8)),
            ("subject_identity", Identity()), ("target_identity", Identity()), ("identity_resolution_status", Choice<SemanticResolution>()),
            ("fact_kind", Choice<MemoryFactKind>()), ("value", Text(200)), ("relationship_type", Choice<KnowledgeRelationType>()),
            ("event_type", Nullable(Choice<TranslationImpactEventType>())), ("event_actuality", Choice<SemanticActuality>()), ("event_effect", Choice<MemoryEventEffect>()),
            ("effective_chapter", Nullable(new { type = "integer", minimum = 1 })), ("effective_to_chapter", Nullable(new { type = "integer", minimum = 1 })),
            ("revealed_chapter", Nullable(new { type = "integer", minimum = 1 })), ("speaker_identity", Identity()), ("listener_identity", Identity()),
            ("self_reference", Text(100)), ("addressee_reference", Text(100)), ("verified_participants", ArrayOf(IdentityObject(), 8)),
            ("cpu_candidate_ids", ArrayOf(Text(36), 16)), ("affected_fact_ids", ArrayOf(Text(36), 8)),
            ("confidence", new { type = "number", minimum = 0, maximum = 1 }), ("uncertainties", ArrayOf(Text(240), 8)), ("reason", Text())), 64))));

    public static SemanticResponse Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 200_000) throw new InvalidDataException("Semantic response empty/oversized.");
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("schema_version", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out int version)) throw new InvalidDataException("Missing semantic wire version.");
            Check(document.RootElement, SchemaFor(version), "$root");
            return document.RootElement.Deserialize<SemanticResponse>(JsonOptions) ?? throw new InvalidDataException("Empty semantic object.");
        }
        catch (JsonException ex) { throw new InvalidDataException("Semantic JSON does not match the strict contract.", ex); }
    }
    // Validate the exact same restricted schema sent to either provider, including enum case,
    // required fields, duplicate keys and unknown fields (serializer defaults alone are insufficient).
    private static void Check(JsonElement value, JsonElement schema, string path)
    {
        void Fail() => throw new InvalidDataException("Semantic schema mismatch at " + path);
        if (schema.TryGetProperty("anyOf", out var variants))
        {
            foreach (var candidate in variants.EnumerateArray()) { try { Check(value, candidate, path); return; } catch (InvalidDataException) { } }
            Fail();
        }
        string type = schema.GetProperty("type").GetString()!;
        if (type == "null") { if (value.ValueKind != JsonValueKind.Null) Fail(); return; }
        if (type == "object")
        {
            if (value.ValueKind != JsonValueKind.Object) Fail();
            var properties = schema.GetProperty("properties"); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name) || !properties.TryGetProperty(property.Name, out var child)) Fail();
                else Check(property.Value, child, path + "." + property.Name);
            }
            foreach (var required in schema.GetProperty("required").EnumerateArray()) if (!names.Contains(required.GetString()!)) Fail();
        }
        else if (type == "array")
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > schema.GetProperty("maxItems").GetInt32()) Fail();
            foreach (var item in value.EnumerateArray()) Check(item, schema.GetProperty("items"), path + "[]");
        }
        else if (type == "string") { if (value.ValueKind != JsonValueKind.String || schema.TryGetProperty("maxLength", out var length) && value.GetString()!.Length > length.GetInt32()) Fail(); }
        else if (type is "number" or "integer")
        {
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double n) || !double.IsFinite(n) || type == "integer" && !value.TryGetInt32(out _)) Fail();
            if (schema.TryGetProperty("minimum", out var min) && value.GetDouble() < min.GetDouble() || schema.TryGetProperty("maximum", out var max) && value.GetDouble() > max.GetDouble()) Fail();
        }
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(x => x.GetRawText() == value.GetRawText())) Fail();
    }
}
