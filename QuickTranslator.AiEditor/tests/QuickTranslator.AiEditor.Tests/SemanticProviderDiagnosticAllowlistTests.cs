using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using Xunit.Abstractions;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SemanticProviderDiagnosticAllowlistTests(ITestOutputHelper output)
{
    private const string Secret = "SENTINEL_PRIVATE_KEY_AND_ORIGINAL_NEVER_PERSIST";

    [Fact]
    public void Diagnostic_allowlist_hides_unknown_legacy_text_and_schema_is_not_weakened()
    {
        Assert.Equal("UnexpectedProviderFailure", SemanticProviderDiagnostics.Describe(Secret).Code);
        Assert.DoesNotContain(Secret, SemanticProviderDiagnostics.Describe(Secret).Guidance);
        Assert.Contains("không thể", SemanticProviderDiagnostics.Describe("ProviderFailure").Guidance);
        var schema = SemanticSurveyContract.MultiEvidenceSchema; var keywords = new HashSet<string>(); int nodes = 0, maxDepth = 0, nullable = 0, stringLimits = 0;
        void Walk(JsonElement node, int depth)
        {
            nodes++; maxDepth = Math.Max(maxDepth, depth);
            foreach (var p in node.EnumerateObject())
            {
                keywords.Add(p.Name); if (p.Name == "anyOf") { nullable++; foreach (var variant in p.Value.EnumerateArray()) Walk(variant, depth + 1); }
                if (p.Name == "maxLength") stringLimits++;
                if (p.Name == "items") Walk(p.Value, depth + 1);
                if (p.Name == "properties") foreach (var property in p.Value.EnumerateObject()) Walk(property.Value, depth + 1);
            }
            if (node.TryGetProperty("type", out var type) && type.GetString() == "object")
            { Assert.False(node.GetProperty("additionalProperties").GetBoolean()); Assert.Equal(node.GetProperty("properties").EnumerateObject().Count(), node.GetProperty("required").GetArrayLength()); }
        }
        Walk(schema, 0); Assert.Equal(2, schema.GetProperty("properties").GetProperty("schema_version").GetProperty("enum")[0].GetInt32());
        Assert.True(nullable > 0); Assert.True(stringLimits > 0); Assert.Equal(64, schema.GetProperty("properties").GetProperty("findings").GetProperty("maxItems").GetInt32());
        output.WriteLine(JsonSerializer.Serialize(new { schemaUtf8Bytes = Encoding.UTF8.GetByteCount(schema.GetRawText()), schemaNodes = nodes, maximumStructuralDepth = maxDepth,
            nullableUnions = nullable, localMaxLengthConstraints = stringLimits, keywords = keywords.Order().ToArray(), note = "Offline inventory only, not proof of server acceptance; contract/auditor unchanged." }));
    }
}
