using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.GeminiSchema.Probe;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GeminiSchemaProbeTests
{
    private const string Secret = "PRIVATE_KEY_SOURCE_RAW_PROVIDER_SENTINEL";
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return respond(request, ct); }
    }
    private static HttpResponseMessage Json(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData("gemini-3.6-flash", 4096)]
    [InlineData("gemini-3.8-flash", 8192)]
    public async Task Inspection_and_live_mock_use_the_exact_production_v2_schema_config_and_safety(string model, int output)
    {
        var options = ProbeRequest.Options(model, output); var inspection = await ProbeRequest.InspectAsync(options);
        Assert.Equal(model, inspection.Model); Assert.EndsWith(model + ":generateContent", inspection.Endpoint);
        Assert.Equal(SemanticSurveyContract.MultiEvidenceSchema.GetRawText(), inspection.GenerationConfig.GetProperty("responseJsonSchema").GetRawText());
        Assert.Equal(4, inspection.GenerationConfig.EnumerateObject().Count());
        Assert.Equal(output, inspection.GenerationConfig.GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal("low", inspection.GenerationConfig.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());
        Assert.Equal("application/json", inspection.GenerationConfig.GetProperty("responseMimeType").GetString());
        Assert.False(inspection.GenerationConfig.TryGetProperty("responseSchema", out _)); Assert.False(inspection.GenerationConfig.TryGetProperty("temperature", out _));
        Assert.Equal(4, inspection.SafetySettings.GetArrayLength());
        Assert.All(inspection.SafetySettings.EnumerateArray(), s => Assert.Equal("OFF", s.GetProperty("threshold").GetString()));
        Assert.Equal(94, inspection.Schema.Nodes); Assert.Equal(6, inspection.Schema.MaximumDepth); Assert.Equal(13, inspection.Schema.AnyOf);
        Assert.Equal("8011DE631259296A22FEF08A211C3B82FAEA12A559D206869EB3FFBB569D93E2", inspection.Schema.Sha256);
        Assert.Equal(28, inspection.Schema.Constraints["maxLength"]); Assert.Equal(64, inspection.Schema.ArrayLimits["$.findings"]);
        Assert.Equal(inspection.Schema.PropertyOccurrences, inspection.Schema.RequiredOccurrences);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(inspection));

        var handler = new Handler(async (request, ct) =>
        {
            Assert.Equal(new[] { Secret }, request.Headers.GetValues("x-goog-api-key")); Assert.DoesNotContain(Secret, request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal(inspection.GenerationConfig.GetRawText(), body.RootElement.GetProperty("generationConfig").GetRawText());
            Assert.Equal(inspection.SafetySettings.GetRawText(), body.RootElement.GetProperty("safetySettings").GetRawText());
            Assert.DoesNotContain(Secret, body.RootElement.GetRawText());
            return Json(ProbeRequest.Envelope(ProbeRequest.ExpectedResponse));
        });
        var report = await new ProbeSession(options).RunOnceAsync(Secret, ProbeRequest.Confirmation, () => handler);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, report.HttpRequests); Assert.Equal(200, report.HttpStatus);
        Assert.True(report.ProviderAcceptedRequest); Assert.True(report.ResponseContractValid);
        Assert.Equal("SchemaAcceptedNeutralResponseValid", report.Code); Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report));
    }

    [Theory]
    [InlineData("schema", "Http400SchemaRejected")]
    [InlineData("unknownSchemaPath", "Http400SchemaRejected")]
    [InlineData("complexity", "Http400SchemaRejected")]
    [InlineData("keyword", "Http400SchemaRejected")]
    [InlineData("config", "Http400GenerationConfigRejected")]
    [InlineData("safety", "Http400SafetySettingsRejected")]
    [InlineData("plain", "Http400Unclassified")]
    [InlineData("malformed", "Http400Unclassified")]
    [InlineData("oversized", "Http400BodyOversized")]
    public async Task Provider_400_classification_exposes_only_controlled_codes_and_repository_schema_paths(string kind, string code)
    {
        string field = kind switch { "schema" => "generation_config.response_json_schema.properties.findings.items.properties.event_type.anyOf[0].enum",
            "unknownSchemaPath" => "generation_config.response_json_schema.properties." + Secret,
            "config" => "generation_config." + Secret, "safety" => "safety_settings[0]." + Secret, _ => Secret };
        string message = kind == "complexity" ? "The response schema contains too many states for serving. " + Secret :
            kind == "keyword" ? "Unknown 'maxLength' in response_json_schema " + Secret : Secret;
        string body = kind == "malformed" ? Secret : kind == "oversized" ? new string('x', 40_000) + Secret : JsonSerializer.Serialize(new {
            error = new { status = kind == "plain" ? Secret : "INVALID_ARGUMENT", message,
                details = new[] { new { fieldViolations = new[] { new { field, description = Secret } } } } } });
        var handler = new Handler((_, _) => Task.FromResult(Json(body, 400)));
        var session = new ProbeSession(ProbeRequest.Options());
        var report = await session.RunOnceAsync(Secret, ProbeRequest.Confirmation, () => handler);
        Assert.Equal(code, report.Code); Assert.Equal(1, report.HttpRequests); Assert.Equal(1, handler.Calls);
        Assert.False(report.ProviderAcceptedRequest); Assert.Null(report.ResponseContractValid); Assert.NotNull(report.Provider);
        string safe = JsonSerializer.Serialize(report); Assert.DoesNotContain(Secret, safe); Assert.DoesNotContain("description", safe); Assert.DoesNotContain("message", safe);
        if (kind == "schema") Assert.Contains("$schema.properties.findings.items.properties.event_type.anyOf[0].enum", report.Provider.SchemaPaths);
        if (kind == "unknownSchemaPath") Assert.Equal(new[] { "$schema" }, report.Provider.SchemaPaths);
        if (kind == "complexity") Assert.Contains("SchemaComplexityRejected", report.Provider.SchemaHints);
        if (kind == "keyword") Assert.Contains("Keyword:maxLength", report.Provider.SchemaHints);
        int factories = 0; var again = await session.RunOnceAsync(Secret, ProbeRequest.Confirmation, () => { factories++; return handler; });
        Assert.Equal("ApprovalAlreadyUsed", again.Code); Assert.Equal(0, factories); Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    [InlineData(503)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task One_confirmation_never_retries_redirects_or_falls_back(int status)
    {
        var handler = new Handler((request, _) =>
        {
            Assert.Contains("gemini-3.6-flash:generateContent", request.RequestUri!.AbsoluteUri);
            if (status == 0) throw new HttpRequestException(Secret);
            if (status == -1) throw new OperationCanceledException(Secret);
            var response = Json(Secret, status); response.Headers.Location = new Uri("https://example.invalid/" + Secret); return Task.FromResult(response);
        });
        var report = await new ProbeSession(ProbeRequest.Options()).RunOnceAsync(Secret, ProbeRequest.Confirmation, () => handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(status == 0 ? "NetworkFailure" : status == -1 ? "Timeout" : "Http" + status, report.Code);
        Assert.Equal(1, report.HttpRequests); Assert.Equal(1, handler.Calls); Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report));
    }

    [Theory]
    [InlineData("missing", "SemanticSchemaViolation")]
    [InlineData("legacy", "ProbeResponseScopeMismatch")]
    [InlineData("invented", "ProbeResponseScopeMismatch")]
    [InlineData("refusal", "ProviderRefusal")]
    public async Task Http_200_does_not_automatically_validate_missing_or_wrong_scope_data_or_refusal(string kind, string code)
    {
        var node = JsonNode.Parse(ProbeRequest.ExpectedResponse)!.AsObject();
        if (kind == "missing") node.Remove("reviewed_evidence_ids");
        if (kind == "legacy") node["schema_version"] = 1;
        if (kind == "invented") node["reviewed_evidence_ids"] = new JsonArray(Guid.NewGuid().ToString());
        var handler = new Handler((_, _) => Task.FromResult(Json(kind == "refusal" ? "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}" : ProbeRequest.Envelope(node.ToJsonString()))));
        var report = await new ProbeSession(ProbeRequest.Options()).RunOnceAsync(Secret, ProbeRequest.Confirmation, () => handler);
        Assert.Equal(code, report.Code); Assert.True(report.ProviderAcceptedRequest); Assert.NotEqual(true, report.ResponseContractValid);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Offline_and_cancelled_cli_do_not_read_keys_construct_transports_or_use_persisted_live_authorization()
    {
        int keys = 0, transports = 0; string? Key() { keys++; throw new InvalidOperationException(Secret); }
        HttpMessageHandler Transport() { transports++; throw new InvalidOperationException(Secret); }
        using var offline = new StringWriter(); Assert.Equal(0, await ProbeCommand.RunAsync([], new StringReader(""), offline, Key, Transport));
        Assert.Contains("Offline", offline.ToString()); Assert.DoesNotContain("contents", offline.ToString()); Assert.DoesNotContain(Secret, offline.ToString());
        using var declined = new StringWriter(); Assert.Equal(0, await ProbeCommand.RunAsync(["--live"], new StringReader("NO"), declined, Key, Transport));
        Assert.Contains("CancelledBeforeSend", declined.ToString()); Assert.Equal(0, keys); Assert.Equal(0, transports);
        using var invalid = new StringWriter(); Assert.Equal(2, await ProbeCommand.RunAsync(["--model", Secret], new StringReader(ProbeRequest.Confirmation), invalid, Key, Transport));
        Assert.DoesNotContain(Secret, invalid.ToString()); Assert.Equal(0, keys); Assert.Equal(0, transports);
        var missingKey = await new ProbeSession(ProbeRequest.Options()).RunOnceAsync("", ProbeRequest.Confirmation, Transport);
        Assert.Equal("ApiKeyMissingOrInvalid", missingKey.Code); Assert.Equal(0, transports);
    }

    [Fact]
    public async Task Confirmed_cli_reports_only_safe_codes_and_usage_once_without_exporting_generated_text()
    {
        var body = JsonNode.Parse(ProbeRequest.Envelope(ProbeRequest.ExpectedResponse))!.AsObject();
        body["usageMetadata"] = JsonSerializer.SerializeToNode(new { promptTokenCount = 2500, candidatesTokenCount = 45, thoughtsTokenCount = 7 });
        var handler = new Handler((_, _) => Task.FromResult(Json(body.ToJsonString())));
        using var text = new StringWriter(); int keyReads = 0;
        int exit = await ProbeCommand.RunAsync(["--live"], new StringReader(ProbeRequest.Confirmation), text, () => { keyReads++; return Secret; }, () => handler);
        Assert.Equal(0, exit); Assert.Equal(1, keyReads); Assert.Equal(1, handler.Calls);
        string safe = text.ToString(); Assert.Contains("SchemaAcceptedNeutralResponseValid", safe); Assert.Contains("2500", safe);
        Assert.Contains("52", safe); Assert.DoesNotContain(Secret, safe); Assert.DoesNotContain("11111111-1111", safe);
        Assert.DoesNotContain("contents", safe); Assert.DoesNotContain("candidates", safe);
    }

    [Fact]
    public async Task Provider_refusal_is_not_retried_by_reusing_the_same_approval()
    {
        var handler = new Handler((_, _) => Task.FromResult(Json("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")));
        var session = new ProbeSession(ProbeRequest.Options());
        var first = await session.RunOnceAsync(Secret, ProbeRequest.Confirmation, () => handler);
        var second = await session.RunOnceAsync(Secret, ProbeRequest.Confirmation, () => throw new InvalidOperationException(Secret));
        Assert.Equal("ProviderRefusal", first.Code); Assert.Equal("ApprovalAlreadyUsed", second.Code); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void Wire_to_internal_mapping_missing_fields_and_legacy_v1_v2_remain_strict_and_audited()
    {
        var fixture = new SemanticFixture(AdaptiveSemanticTests.Windows(2));
        var request = AdaptiveSemanticTests.Plan(fixture).Single(); var name = AdaptiveSemanticTests.Name(request.Units[0]);
        var response = AdaptiveSemanticTests.Response(request, name);
        var mapped = SemanticSurveyContract.Parse(SemanticFixture.Json(response));
        Assert.Equal(SemanticFixture.Json(response), SemanticFixture.Json(mapped));
        Assert.True(SemanticQualityAuditor.Audit(mapped, request, fixture.Sources).Validated);
        var required = SemanticSurveyContract.MultiEvidenceSchema.GetProperty("properties").GetProperty("findings").GetProperty("items").GetProperty("required").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
        Assert.True(required.SetEquals(typeof(SemanticFinding).GetProperties().Select(x => JsonNamingPolicy.SnakeCaseLower.ConvertName(x.Name))));
        foreach (var field in required)
        {
            var incomplete = JsonNode.Parse(SemanticFixture.Json(response))!.AsObject(); incomplete["findings"]![0]!.AsObject().Remove(field);
            Assert.Throws<InvalidDataException>(() => SemanticSurveyContract.Parse(incomplete.ToJsonString()));
        }
        var wrong = mapped with { Findings = [name with { SourceEvidenceIds = [Guid.NewGuid()] }] };
        Assert.False(SemanticQualityAuditor.Audit(wrong, request, fixture.Sources).Validated);
        var oldRequest = fixture.Plan().First(); var legacy = SemanticFixture.Response(oldRequest, fixture.Name(oldRequest));
        Assert.Equal(SemanticFixture.Json(legacy), SemanticFixture.Json(SemanticSurveyContract.Parse(SemanticFixture.Json(legacy))));
        Assert.Equal(1, SemanticSurveyContract.Parse(SemanticFixture.Json(legacy)).SchemaVersion);
        Assert.Equal(2, mapped.SchemaVersion);
    }

    [Fact]
    public void Identity_roles_timeline_actuality_and_support_are_not_collapsed_or_automatically_verified()
    {
        var s = new SemanticFixture(); var request = AdaptiveSemanticTests.Plan(s).Single(); var name = AdaptiveSemanticTests.Name(request.Units[0]);
        var finding = name with {
            FindingType = SemanticFindingType.DirectionalAddressing,
            SubjectIdentity = name.SubjectIdentity! with { Kind = MemoryEntityKind.Body },
            TargetIdentity = new(null, "target", "周宁", MemoryEntityKind.Character, SemanticResolution.Ambiguous),
            SpeakerIdentity = new(null, "speaker", "林青", MemoryEntityKind.Consciousness, SemanticResolution.Unresolved),
            ListenerIdentity = new(null, "listener", "周宁", MemoryEntityKind.UnresolvedMention, SemanticResolution.Unresolved),
            SelfReference = "我", AddresseeReference = "你", EventType = TranslationImpactEventType.RevealIdentity,
            EventActuality = SemanticActuality.HypotheticalConditional, EventEffect = MemoryEventEffect.End,
            EffectiveChapter = 1, EffectiveToChapter = 2, RevealedChapter = 3, Uncertainties = ["Synthetic ambiguous roles"] };
        string json = SemanticFixture.Json(AdaptiveSemanticTests.Response(request, finding));
        var parsed = SemanticSurveyContract.Parse(json); Assert.Equal(json, SemanticFixture.Json(parsed));
        Assert.Equal(4, new[] { parsed.Findings[0].SubjectIdentity!.LocalId, parsed.Findings[0].TargetIdentity!.LocalId,
            parsed.Findings[0].SpeakerIdentity!.LocalId, parsed.Findings[0].ListenerIdentity!.LocalId }.Distinct().Count());
        var audit = SemanticQualityAuditor.Audit(parsed, request, s.Sources); Assert.False(audit.Validated); Assert.False(audit.Findings[0].CanPromote);
    }
}
