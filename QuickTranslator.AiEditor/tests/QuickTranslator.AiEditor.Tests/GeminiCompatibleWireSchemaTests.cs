using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.GeminiSchema.Probe;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GeminiCompatibleWireSchemaTests
{
    private const string Secret = "PRIVATE_KEY_OR_RAW_PROVIDER_SENTINEL";
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return respond(request, ct); }
    }
    private static HttpResponseMessage Json(string text, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    [Fact]
    public void All_production_keywords_are_inventoried_against_an_explicit_official_snapshot()
    {
        var (_, comparison) = GeminiCompatibleWireSchema.Select(SemanticSurveyContract.MultiEvidenceSchema, ProbeSchemaMode.Production);
        Assert.Equal(new[] { "additionalProperties", "anyOf", "enum", "items", "maxItems", "maxLength", "maximum", "minimum", "properties", "required", "type" }, comparison.KeywordSupport.Select(x => x.Keyword));
        var notListed = Assert.Single(comparison.KeywordSupport, x => !x.ListedByGemini);
        Assert.Equal("maxLength", notListed.Keyword); Assert.Equal(28, notListed.Occurrences);
        Assert.Equal(18, comparison.KeywordSupport.Single(x => x.Keyword == "enum").Occurrences);
        Assert.Equal(13, comparison.KeywordSupport.Single(x => x.Keyword == "anyOf").Occurrences);
        Assert.Equal(8, comparison.KeywordSupport.Single(x => x.Keyword == "required").Occurrences);
        Assert.Equal("2026-10-20", comparison.DocumentationCheckedOn); Assert.Equal(GeminiCompatibleWireSchema.DocumentationUrl, comparison.DocumentationUrl);
        Assert.Equal(comparison.Production.Sha256, comparison.Wire.Sha256); Assert.Empty(comparison.RemovedConstraints);
    }

    // Independent full-tree comparison: no property, enum, required entry, evidence/identity
    // role, constraint or ordering may change except a schema keyword named maxLength.
    private static void SameExceptMaxLength(JsonElement original, JsonElement wire, bool fieldNameMap = false)
    {
        Assert.Equal(original.ValueKind, wire.ValueKind);
        if (original.ValueKind == JsonValueKind.Object)
        {
            var fields = original.EnumerateObject().Where(x => fieldNameMap || x.Name != "maxLength").ToArray();
            Assert.Equal(fields.Select(x => x.Name), wire.EnumerateObject().Select(x => x.Name));
            foreach (var field in fields) SameExceptMaxLength(field.Value, wire.GetProperty(field.Name), !fieldNameMap && field.Name is "properties" or "$defs");
        }
        else if (original.ValueKind == JsonValueKind.Array)
        {
            Assert.Equal(original.GetArrayLength(), wire.GetArrayLength());
            for (int i = 0; i < original.GetArrayLength(); i++) SameExceptMaxLength(original[i], wire[i]);
        }
        else Assert.Equal(original.GetRawText(), wire.GetRawText());
    }

    [Fact]
    public void Projection_only_removes_28_string_limits_and_never_mutates_internal_v1_or_v2()
    {
        string v1 = SemanticSurveyContract.ResponseSchema.GetRawText(), v2 = SemanticSurveyContract.MultiEvidenceSchema.GetRawText();
        var (wire, comparison) = GeminiCompatibleWireSchema.Select(SemanticSurveyContract.MultiEvidenceSchema, ProbeSchemaMode.GeminiCompatibleWireSchema);
        SameExceptMaxLength(SemanticSurveyContract.MultiEvidenceSchema, wire);
        Assert.Equal(28, comparison.RemovedConstraintCount); Assert.All(comparison.RemovedConstraints, c => { Assert.Equal("maxLength", c.Keyword); Assert.EndsWith(".maxLength", c.Path); Assert.True(c.Value > 0); });
        Assert.Equal(28, comparison.RemovedConstraints.Select(x => x.Path).Distinct().Count());
        Assert.NotEqual(comparison.Production.Sha256, comparison.Wire.Sha256); Assert.True(comparison.Wire.Utf8Bytes < comparison.Production.Utf8Bytes);
        Assert.Equal(5751, comparison.Wire.Utf8Bytes); Assert.Equal("73BB1273C63AC1911362116627851E7647E560C160BE32F9C667F6EA7039567D", comparison.Wire.Sha256);
        Assert.Equal(comparison.Production.Keywords.Where(x => x != "maxLength"), comparison.Wire.Keywords);
        Assert.False(comparison.Wire.Constraints.ContainsKey("maxLength"));
        foreach (var bound in new[] { "maxItems", "minimum", "maximum" }) Assert.Equal(comparison.Production.Constraints[bound], comparison.Wire.Constraints[bound]);
        Assert.Equal(comparison.Production.EnumSizes.ToArray(), comparison.Wire.EnumSizes.ToArray()); Assert.Equal(comparison.Production.ArrayLimits.ToArray(), comparison.Wire.ArrayLimits.ToArray());
        Assert.Equal(comparison.Production.RequiredOccurrences, comparison.Wire.RequiredOccurrences); Assert.Equal(59, comparison.Wire.RequiredOccurrences);
        Assert.Equal(25, comparison.Wire.FindingProperties); Assert.Equal(13, comparison.Wire.AnyOf); Assert.Equal(94, comparison.Wire.Nodes);
        var root = wire.GetProperty("properties");
        Assert.Equal(JsonValueKind.Number, root.GetProperty("schema_version").GetProperty("enum")[0].ValueKind);
        Assert.Equal(2, root.GetProperty("schema_version").GetProperty("enum")[0].GetInt32());
        Assert.False(wire.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(23, comparison.Wire.EnumSizes["$.findings[].relationship_type"]);
        Assert.Equal(v1, SemanticSurveyContract.ResponseSchema.GetRawText()); Assert.Equal(v2, SemanticSurveyContract.MultiEvidenceSchema.GetRawText());
        Assert.Equal("8011DE631259296A22FEF08A211C3B82FAEA12A559D206869EB3FFBB569D93E2", comparison.Production.Sha256);
    }

    [Fact]
    public void Projection_preserves_fields_and_enum_values_named_maxLength_and_removes_only_schema_constraints()
    {
        using var doc = JsonDocument.Parse("""
            {"type":"object","properties":{"maxLength":{"type":"string","maxLength":9,"enum":["maxLength"]},
            "nested":{"anyOf":[{"type":"string","maxLength":4},{"type":"null"}]}},"required":["maxLength","nested"],
            "additionalProperties":{"type":"string","maxLength":3}}
            """);
        var removed = new List<RemovedSchemaConstraint>(); var wire = GeminiCompatibleWireSchema.RemoveMaxLength(doc.RootElement, removed);
        SameExceptMaxLength(doc.RootElement, wire); Assert.Equal(3, removed.Count);
        Assert.Equal("maxLength", wire.GetProperty("properties").GetProperty("maxLength").GetProperty("enum")[0].GetString());
    }

    [Theory]
    [InlineData("gemini-3.6-flash")]
    [InlineData("gemini-3.8-flash")]
    public async Task Compatible_inspection_and_mock_send_keep_all_other_production_config_and_do_not_auto_select_the_mode(string model)
    {
        var options = ProbeRequest.Options(model); var original = await ProbeRequest.InspectAsync(options);
        var compatible = await ProbeRequest.InspectAsync(options, schemaMode: ProbeSchemaMode.GeminiCompatibleWireSchema);
        Assert.Equal(nameof(ProbeSchemaMode.Production), original.Comparison.Mode); Assert.Equal(0, original.Comparison.RemovedConstraintCount);
        foreach (var field in original.GenerationConfig.EnumerateObject().Where(x => x.Name != "responseJsonSchema"))
            Assert.Equal(field.Value.GetRawText(), compatible.GenerationConfig.GetProperty(field.Name).GetRawText());
        Assert.Equal(original.SafetySettings.GetRawText(), compatible.SafetySettings.GetRawText()); Assert.Equal(original.Endpoint, compatible.Endpoint);
        var handler = new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(compatible.GenerationConfig.GetRawText(), body.RootElement.GetProperty("generationConfig").GetRawText());
            Assert.Equal(compatible.SafetySettings.GetRawText(), body.RootElement.GetProperty("safetySettings").GetRawText());
            Assert.DoesNotContain("maxLength", body.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema").GetRawText());
            Assert.Contains(model + ":generateContent", request.RequestUri!.AbsoluteUri);
            return Json(ProbeRequest.Envelope(ProbeRequest.ExpectedResponse));
        });
        var report = await new ProbeSession(options, ProbeSchemaMode.GeminiCompatibleWireSchema).RunOnceAsync(Secret, ProbeRequest.Confirmation, () => handler);
        Assert.Equal(1, handler.Calls); Assert.True(report.ResponseContractValid); Assert.Equal(nameof(ProbeSchemaMode.GeminiCompatibleWireSchema), report.WireSchemaMode);
        Assert.Equal(compatible.Schema.Sha256, report.WireSchemaSha256);
        Assert.Equal(original.GenerationConfig.GetRawText(), (await ProbeRequest.InspectAsync(options)).GenerationConfig.GetRawText());
    }

    [Theory]
    [InlineData(400)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(0)]
    [InlineData(200)]
    public async Task Compatible_failure_never_retries_switches_model_or_falls_back_to_another_schema(int status)
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Contains("gemini-3.6-flash", request.RequestUri!.AbsoluteUri);
            if (status == 0) throw new HttpRequestException(Secret);
            return Task.FromResult(Json(status == 200 ? "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}" : "{\"error\":{\"status\":\"INVALID_ARGUMENT\"}}", status));
        });
        var session = new ProbeSession(ProbeRequest.Options(), ProbeSchemaMode.GeminiCompatibleWireSchema);
        var report = await session.RunOnceAsync(Secret, ProbeRequest.Confirmation, () => handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(status == 400 ? "Http400Unclassified" : status == 0 ? "NetworkFailure" : status == 200 ? "ProviderRefusal" : "Http" + status, report.Code);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, report.HttpRequests); Assert.NotEqual(true, report.ResponseContractValid);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report));
        var again = await session.RunOnceAsync(Secret, ProbeRequest.Confirmation, () => throw new InvalidOperationException("No second transport"));
        Assert.Equal("ApprovalAlreadyUsed", again.Code);
    }

    [Theory]
    [InlineData("value")]
    [InlineData("reason")]
    [InlineData("identity")]
    [InlineData("support")]
    [InlineData("missing")]
    public async Task Looser_wire_never_relaxes_internal_limits_required_fields_or_quality_audits(string invalid)
    {
        var s = new SemanticFixture(); var request = AdaptiveSemanticTests.Plan(s).Single(); var name = AdaptiveSemanticTests.Name(request.Units[0]);
        var finding = invalid switch {
            "value" => name with { Value = new string('x', 201) }, "reason" => name with { Reason = new string('x', 1001) },
            "identity" => name with { SubjectIdentity = name.SubjectIdentity! with { Name = new string('x', 101) } },
            "support" => name with { SupportSpans = [name.SupportSpans[0] with { Text = new string('x', 241) }] }, _ => name };
        string text = SemanticFixture.Json(AdaptiveSemanticTests.Response(request, finding));
        if (invalid == "missing") { var node = System.Text.Json.Nodes.JsonNode.Parse(text)!; node["findings"]![0]!.AsObject().Remove("source_evidence_ids"); text = node.ToJsonString(); }
        Assert.Throws<InvalidDataException>(() => SemanticSurveyContract.Parse(text));
        var handler = new Handler((_, _) => Task.FromResult(Json(ProbeRequest.Envelope(text))));
        var report = await new ProbeSession(ProbeRequest.Options(), ProbeSchemaMode.GeminiCompatibleWireSchema).RunOnceAsync(Secret, ProbeRequest.Confirmation, () => handler);
        Assert.Equal("SemanticSchemaViolation", report.Code); Assert.False(report.ResponseContractValid); Assert.Equal(1, handler.Calls);
        var wrong = AdaptiveSemanticTests.Response(request, name with { SourceEvidenceIds = [Guid.NewGuid()] });
        Assert.False(SemanticQualityAuditor.Audit(SemanticSurveyContract.Parse(SemanticFixture.Json(wrong)), request, s.Sources).Validated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Compatible_cli_is_still_offline_by_default_and_requires_a_fresh_confirmation(bool omitFlag)
    {
        string[] schemaArgs = omitFlag ? ["--wire-omit-max-length"] : ["--wire-schema", "gemini-compatible"];
        string? Key() => throw new InvalidOperationException(Secret);
        HttpMessageHandler Transport() => throw new InvalidOperationException(Secret);
        using var offline = new StringWriter();
        Assert.Equal(0, await ProbeCommand.RunAsync(schemaArgs, new StringReader(""), offline, Key, Transport));
        using var preview = JsonDocument.Parse(offline.ToString()); var root = preview.RootElement;
        Assert.Equal(0, root.GetProperty("httpRequests").GetInt32()); Assert.Equal(28, root.GetProperty("inspection").GetProperty("Comparison").GetProperty("RemovedConstraintCount").GetInt32());
        using var cancelled = new StringWriter(); Assert.Equal(0, await ProbeCommand.RunAsync([.. schemaArgs, "--live"], new StringReader("NO"), cancelled, Key, Transport));
        Assert.Contains("CancelledBeforeSend", cancelled.ToString()); Assert.DoesNotContain(Secret, cancelled.ToString());
        using var invalid = new StringWriter(); Assert.Equal(2, await ProbeCommand.RunAsync(["--wire-schema", Secret, "--live"], new StringReader(ProbeRequest.Confirmation), invalid, Key, Transport));
        Assert.DoesNotContain(Secret, invalid.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confirmed_compatible_cli_sends_the_selected_wire_once_and_reports_its_mode_and_hash(bool omitFlag)
    {
        string[] schemaArgs = omitFlag ? ["--wire-omit-max-length"] : ["--wire-schema", "gemini-compatible"];
        var handler = new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var wire = body.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema");
            SameExceptMaxLength(SemanticSurveyContract.MultiEvidenceSchema, wire);
            Assert.DoesNotContain("maxLength", wire.GetRawText());
            return Json(ProbeRequest.Envelope(ProbeRequest.ExpectedResponse));
        });
        using var text = new StringWriter();
        int exit = await ProbeCommand.RunAsync(["--model", "gemini-3.6-flash", .. schemaArgs, "--live"],
            new StringReader(ProbeRequest.Confirmation), text, () => Secret, () => handler);
        Assert.Equal(0, exit); Assert.Equal(1, handler.Calls);
        Assert.Contains("\"WireSchemaMode\": \"GeminiCompatibleWireSchema\"", text.ToString());
        Assert.Contains("SchemaAcceptedNeutralResponseValid", text.ToString()); Assert.DoesNotContain(Secret, text.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflicting_baseline_and_omit_flag_cancel_before_key_access_or_http(bool omitFirst)
    {
        string[] args = omitFirst ? ["--wire-omit-max-length", "--wire-schema", "production", "--live"] :
            ["--wire-schema", "production", "--wire-omit-max-length", "--live"];
        using var output = new StringWriter();
        int result = await ProbeCommand.RunAsync(args, new StringReader(ProbeRequest.Confirmation), output,
            () => throw new InvalidOperationException(Secret), () => throw new InvalidOperationException(Secret));
        Assert.Equal(2, result); Assert.Contains("ProbeConfigurationOrExecutionFailure", output.ToString()); Assert.DoesNotContain(Secret, output.ToString());
    }

    [Fact]
    public async Task Omit_flag_live_mock_still_rejects_a_response_exceeding_the_internal_maxLength()
    {
        var invalid = System.Text.Json.Nodes.JsonNode.Parse(ProbeRequest.ExpectedResponse)!;
        invalid["project_id"] = ProbeRequest.SyntheticProject.ToString() + "x"; // 37 UTF-16 units; internal limit is still 36.
        var handler = new Handler(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var projectIdSchema = body.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema").GetProperty("properties").GetProperty("project_id");
            Assert.Equal("string", projectIdSchema.GetProperty("type").GetString()); Assert.False(projectIdSchema.TryGetProperty("maxLength", out _));
            return Json(ProbeRequest.Envelope(invalid.ToJsonString()));
        });
        using var output = new StringWriter();
        int result = await ProbeCommand.RunAsync(["--model", "gemini-3.6-flash", "--wire-omit-max-length", "--live"],
            new StringReader(ProbeRequest.Confirmation), output, () => Secret, () => handler);
        Assert.Equal(1, result); Assert.Equal(1, handler.Calls);
        Assert.Contains("SemanticSchemaViolation", output.ToString()); Assert.Contains("\"ResponseContractValid\": false", output.ToString());
        Assert.DoesNotContain("SchemaAcceptedNeutralResponseValid", output.ToString()); Assert.DoesNotContain(Secret, output.ToString());
        Assert.Equal(36, SemanticSurveyContract.MultiEvidenceSchema.GetProperty("properties").GetProperty("project_id").GetProperty("maxLength").GetInt32());
    }
}
