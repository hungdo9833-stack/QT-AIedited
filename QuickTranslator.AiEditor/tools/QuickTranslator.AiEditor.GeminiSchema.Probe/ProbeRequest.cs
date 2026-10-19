using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.GeminiSchema.Probe;

public sealed record SchemaInventory(string Sha256, int Utf8Bytes, int Nodes, int MaximumDepth, int PropertyOccurrences,
    int RequiredOccurrences, int ClosedObjects, int FindingProperties, int AnyOf,
    IReadOnlyDictionary<string, int> EnumSizes, IReadOnlyDictionary<string, int> ArrayLimits,
    IReadOnlyDictionary<string, int> Constraints, IReadOnlyList<string> Keywords)
{
    public static SchemaInventory Inspect(JsonElement schema)
    {
        int nodes = 0, depth = 0, properties = 0, required = 0, closed = 0, unions = 0;
        var enums = new SortedDictionary<string, int>(); var arrays = new SortedDictionary<string, int>();
        var constraints = new SortedDictionary<string, int>(); var keywords = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement s, string path, int level)
        {
            nodes++; depth = Math.Max(depth, level);
            foreach (var p in s.EnumerateObject())
            {
                keywords.Add(p.Name);
                if (p.Name is "maxLength" or "minimum" or "maximum" or "maxItems") constraints[p.Name] = constraints.GetValueOrDefault(p.Name) + 1;
            }
            if (s.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False) closed++;
            if (s.TryGetProperty("required", out var r)) required += r.GetArrayLength();
            if (s.TryGetProperty("enum", out var choices)) enums[path] = choices.GetArrayLength();
            if (s.TryGetProperty("maxItems", out var maximum)) arrays[path] = maximum.GetInt32();
            if (s.TryGetProperty("properties", out var fields)) foreach (var p in fields.EnumerateObject()) { properties++; Walk(p.Value, path + "." + p.Name, level + 1); }
            if (s.TryGetProperty("items", out var item)) Walk(item, path + "[]", level + 1);
            if (s.TryGetProperty("anyOf", out var variants)) { unions++; int i = 0; foreach (var v in variants.EnumerateArray()) Walk(v, path + ".anyOf[" + i++ + "]", level + 1); }
        }
        Walk(schema, "$", 0); byte[] bytes = Encoding.UTF8.GetBytes(schema.GetRawText());
        return new(Convert.ToHexString(SHA256.HashData(bytes)), bytes.Length, nodes, depth, properties, required, closed,
            schema.GetProperty("properties").GetProperty("findings").GetProperty("items").GetProperty("properties").EnumerateObject().Count(),
            unions, enums, arrays, constraints, keywords.ToArray());
    }
}

public sealed record ProbeInspection(string Model, string Endpoint, JsonElement GenerationConfig, JsonElement SafetySettings, SchemaInventory Schema,
    WireSchemaComparison Comparison);

public static class ProbeRequest
{
    public const string Confirmation = "SEND ONE NEUTRAL SCHEMA PROBE";
    public static readonly Guid SyntheticProject = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid SyntheticChapter = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static string ExpectedResponse => JsonSerializer.Serialize(new SemanticResponse(2, SyntheticProject, SyntheticChapter, [], []), SemanticSurveyContract.JsonOptions);
    internal static string NeutralPrompt => "Neutral JSON Schema compatibility check only. No book, source text, evidence or personal project is supplied. " +
        "Do not invent characters, relationships, events or evidence. Return exactly this object, with empty findings and reviewed_evidence_ids: " + ExpectedResponse;

    // The Gemini wire mode reproduces SemanticSurveyBackend.Gemini exactly: neutral prompt + wire addendum.
    internal static string PromptFor(ProbeSchemaMode mode) => mode == ProbeSchemaMode.GeminiSemanticWire ? NeutralPrompt + GeminiSemanticWire.PromptAddendum : NeutralPrompt;

    public static ModelOptions Options(string model = "gemini-3.6-flash", int maximumOutputTokens = 4096)
    {
        if (model is not ("gemini-3.6-flash" or "gemini-3.8-flash")) throw new ArgumentException("UnsupportedProbeModel");
        var options = new ModelOptions { Model = model, ContextSize = 65536, MaximumOutputTokens = maximumOutputTokens };
        options.Validate(); return options;
    }

    // Exercise the production adapter against an in-memory handler. No copied request
    // builder, reflection, API key, metadata lookup or real network is used for inspection.
    public static async Task<ProbeInspection> InspectAsync(ModelOptions options, CancellationToken ct = default, ProbeSchemaMode schemaMode = ProbeSchemaMode.Production)
    {
        var selection = GeminiCompatibleWireSchema.Select(SemanticSurveyContract.MultiEvidenceSchema, schemaMode);
        using var capture = new Capture(selection.Comparison); using var http = new HttpClient(capture);
        using var client = new GeminiSurveyClient(http, "offline-placeholder", new(100, 1_000_000, 100, 1), Guid.NewGuid(), routeFailuresToPool: true);
        await client.TranslateStructuredAsync(PromptFor(schemaMode), options, selection.Schema, ct);
        return capture.Inspection ?? throw new InvalidDataException("RequestNotCaptured");
    }

    private sealed class Capture(WireSchemaComparison comparison) : HttpMessageHandler
    {
        public ProbeInspection? Inspection { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var config = body.RootElement.GetProperty("generationConfig").Clone();
            Inspection = new(request.RequestUri!.Segments.Last().Split(':')[0], request.RequestUri.AbsoluteUri, config,
                body.RootElement.GetProperty("safetySettings").Clone(), SchemaInventory.Inspect(config.GetProperty("responseJsonSchema")), comparison);
            return new(System.Net.HttpStatusCode.OK) { Content = new StringContent(Envelope(ExpectedResponse), Encoding.UTF8, "application/json") };
        }
    }
    public static string Envelope(string text) => JsonSerializer.Serialize(new { candidates = new[] {
        new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } } });

    internal static bool Matches(JsonElement body, ProbeInspection expected) =>
        body.ValueKind == JsonValueKind.Object && body.EnumerateObject().Count() == 3 &&
        body.TryGetProperty("generationConfig", out var config) && config.GetRawText() == expected.GenerationConfig.GetRawText() &&
        body.TryGetProperty("safetySettings", out var safety) && safety.GetRawText() == expected.SafetySettings.GetRawText() &&
        body.TryGetProperty("contents", out var contents) && contents.GetRawText() ==
            JsonSerializer.SerializeToElement(new[] { new { role = "user", parts = new[] { new { text = PromptFor(Enum.Parse<ProbeSchemaMode>(expected.Comparison.Mode)) } } } }).GetRawText();
}
