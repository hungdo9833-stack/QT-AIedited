using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.GeminiSchema.Probe;

public sealed record ProviderDiagnostic(string Code, string ProviderStatus, IReadOnlyList<string> SchemaPaths, IReadOnlyList<string> SchemaHints)
{
    public static ProviderDiagnostic Classify400(ReadOnlyMemory<byte> bytes)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal); var hints = new SortedSet<string>(StringComparer.Ordinal);
        string status = "UNKNOWN"; bool schema = false, config = false, safety = false;
        if (bytes.Length > 32_768) return Plain();
        try
        {
            using var doc = JsonDocument.Parse(bytes, new() { MaxDepth = 24 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return Plain();
            if (error.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String)
                status = s.GetString() switch { "INVALID_ARGUMENT" => "INVALID_ARGUMENT", "FAILED_PRECONDITION" => "FAILED_PRECONDITION", "OUT_OF_RANGE" => "OUT_OF_RANGE", _ => "UNKNOWN" };
            void InspectField(string field)
            {
                field = field.Replace("GenerateContentRequest.", "", StringComparison.Ordinal)
                    .Replace("generationConfig", "generation_config", StringComparison.Ordinal)
                    .Replace("responseJsonSchema", "response_json_schema", StringComparison.Ordinal);
                const string prefix = "generation_config.response_json_schema";
                if (field == prefix || field.StartsWith(prefix + ".", StringComparison.Ordinal) || field.StartsWith(prefix + "[", StringComparison.Ordinal))
                {
                    schema = true; string path = "$schema" + field[prefix.Length..];
                    paths.Add(KnownSchemaPaths.Contains(path) ? path : "$schema");
                    hints.Add("SchemaFieldViolation");
                }
                else if (field == "generation_config" || field.StartsWith("generation_config.", StringComparison.Ordinal)) config = true;
                else if (field is "safetySettings" or "safety_settings" || field.StartsWith("safety_settings[", StringComparison.Ordinal) || field.StartsWith("safetySettings[", StringComparison.Ordinal)) safety = true;
            }
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var detail in details.EnumerateArray())
                    if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("fieldViolations", out var violations) && violations.ValueKind == JsonValueKind.Array)
                        foreach (var violation in violations.EnumerateArray())
                            if (violation.ValueKind == JsonValueKind.Object && violation.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String) InspectField(field.GetString()!);
            if (error.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                string text = message.GetString()!;
                bool marker = new[] { "response_json_schema", "responseJsonSchema", "response schema" }.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
                bool complexity = new[] { "response schema contains too many states", "response schema is too complex" }.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
                bool rejected = new[] { "unknown", "unsupported", "invalid", "too many", "too complex" }.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
                if (complexity || marker && rejected)
                {
                    schema = true; paths.Add("$schema");
                    if (complexity) hints.Add("SchemaComplexityRejected");
                    else hints.Add("SchemaRejectedByMessage");
                    // Echo only schema keywords compiled from this repository, never arbitrary
                    // provider strings, violation descriptions, identifiers or response excerpts.
                    foreach (var keyword in SchemaInventory.Inspect(SemanticSurveyContract.MultiEvidenceSchema).Keywords)
                        if (text.Contains("\"" + keyword + "\"", StringComparison.Ordinal) || text.Contains("'" + keyword + "'", StringComparison.Ordinal)) hints.Add("Keyword:" + keyword);
                }
            }
        }
        catch (JsonException) { return Plain(); }
        return new(schema ? "Http400SchemaRejected" : config ? "Http400GenerationConfigRejected" : safety ? "Http400SafetySettingsRejected" : "Http400Unclassified", status, paths.ToArray(), hints.ToArray());
        ProviderDiagnostic Plain() => new("Http400Unclassified", status, [], []);
    }

    private static readonly HashSet<string> KnownSchemaPaths = Paths();
    private static HashSet<string> Paths()
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement n, string path)
        {
            paths.Add(path);
            if (n.ValueKind == JsonValueKind.Object) foreach (var p in n.EnumerateObject()) Walk(p.Value, path + "." + p.Name);
            if (n.ValueKind == JsonValueKind.Array) { int i = 0; foreach (var item in n.EnumerateArray()) Walk(item, path + "[" + i++ + "]"); }
        }
        Walk(SemanticSurveyContract.MultiEvidenceSchema, "$schema"); return paths;
    }
}
