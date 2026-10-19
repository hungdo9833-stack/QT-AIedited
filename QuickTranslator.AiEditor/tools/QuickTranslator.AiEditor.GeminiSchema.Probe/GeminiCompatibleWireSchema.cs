using System.Text.Json;

namespace QuickTranslator.AiEditor.GeminiSchema.Probe;

public enum ProbeSchemaMode { Production, GeminiCompatibleWireSchema }
public sealed record RemovedSchemaConstraint(string Path, string Keyword, int Value);
public sealed record SchemaKeywordSupport(string Keyword, int Occurrences, bool ListedByGemini);
public sealed record WireSchemaComparison(string Mode, string VariantVersion, string DocumentationCheckedOn, string DocumentationUrl,
    SchemaInventory Production, SchemaInventory Wire, IReadOnlyList<SchemaKeywordSupport> KeywordSupport,
    IReadOnlyList<RemovedSchemaConstraint> RemovedConstraints)
{
    public int RemovedConstraintCount => RemovedConstraints.Count;
}

/// <summary>Probe-only experiment, NOT a guarantee of provider acceptance. Never changes the internal contract.</summary>
public static class GeminiCompatibleWireSchema
{
    public const string DocumentationUrl = "https://ai.google.dev/api/generate-content#v1beta.GenerationConfig";
    public const string DocumentationCheckedOn = "2026-10-20";
    // Offline snapshot of the official responseJsonSchema subset. Never fetch metadata during a probe.
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal) {
        "$id", "$defs", "$ref", "$anchor", "type", "format", "title", "description", "enum", "items", "prefixItems",
        "minItems", "maxItems", "minimum", "maximum", "anyOf", "oneOf", "properties", "additionalProperties", "required",
        "propertyOrdering" // Explicitly documented non-standard extension.
    };

    public static (JsonElement Schema, WireSchemaComparison Comparison) Select(JsonElement production, ProbeSchemaMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        var removed = new List<RemovedSchemaConstraint>();
        JsonElement wire = mode == ProbeSchemaMode.Production ? production.Clone() : RemoveMaxLength(production, removed);
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(JsonElement node)
        {
            foreach (var p in node.EnumerateObject()) counts[p.Name] = counts.GetValueOrDefault(p.Name) + 1;
            if (node.TryGetProperty("properties", out var props)) foreach (var p in props.EnumerateObject()) Count(p.Value);
            if (node.TryGetProperty("items", out var items)) Count(items);
            if (node.TryGetProperty("anyOf", out var variants)) foreach (var v in variants.EnumerateArray()) Count(v);
        }
        Count(production);
        return (wire, new(mode.ToString(), mode == ProbeSchemaMode.Production ? "production-v2" : "probe-gemini-maxlength-only-1",
            DocumentationCheckedOn, DocumentationUrl, SchemaInventory.Inspect(production), SchemaInventory.Inspect(wire),
            counts.Select(x => new SchemaKeywordSupport(x.Key, x.Value, Supported.Contains(x.Key))).ToArray(), removed));
    }

    public static JsonElement RemoveMaxLength(JsonElement schema, ICollection<RemovedSchemaConstraint> removed)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes))
        {
            void WriteSchema(JsonElement node, string path)
            {
                writer.WriteStartObject();
                foreach (var property in node.EnumerateObject())
                {
                    string childPath = path + "." + property.Name;
                    if (property.Name == "maxLength")
                    { removed.Add(new(childPath, property.Name, property.Value.GetInt32())); continue; }
                    writer.WritePropertyName(property.Name);
                    if (property.Name is "properties" or "$defs")
                    {
                        writer.WriteStartObject();
                        foreach (var field in property.Value.EnumerateObject())
                        { writer.WritePropertyName(field.Name); WriteSchema(field.Value, childPath + "." + field.Name); }
                        writer.WriteEndObject();
                    }
                    else if (property.Name == "items" || property.Name == "additionalProperties" && property.Value.ValueKind == JsonValueKind.Object)
                        WriteSchema(property.Value, childPath);
                    else if (property.Name is "anyOf" or "oneOf" or "allOf" or "prefixItems")
                    {
                        writer.WriteStartArray(); int i = 0;
                        foreach (var variant in property.Value.EnumerateArray()) WriteSchema(variant, childPath + "[" + i++ + "]");
                        writer.WriteEndArray();
                    }
                    else property.Value.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            WriteSchema(schema, "$schema");
        }
        using var doc = JsonDocument.Parse(bytes.ToArray()); return doc.RootElement.Clone();
    }
}
