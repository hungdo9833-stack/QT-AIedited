using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.GeminiSchema.Probe;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GeminiSemanticWireProbeTests : IDisposable
{
    private const string Secret = "SMOKE_PRIVATE_KEY_SENTINEL";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-smoke-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return respond(request); }
    }
    private static HttpResponseMessage Envelope(string text) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } } }), Encoding.UTF8, "application/json")
    };

    // A provider double that answers ONLY from what the prompt contains, in wire format.
    private static async Task<HttpResponseMessage> WireAnswer(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        Assert.Equal(GeminiSemanticWire.Schema.GetRawText(), body.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema").GetRawText());
        string prompt = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.EndsWith(GeminiSemanticWire.PromptAddendum, prompt);
        var evidence = Regex.Matches(prompt, @"(?m)^EVIDENCE project=(\S+) chapter=(\S+) number=\d+ sha256=\S+ id=(\S+) start_utf16=(\d+) length_utf16=(\d+)").ToArray();
        string text = SyntheticSurveySmoke.Text;
        var findings = new JsonArray(evidence.Select(m =>
        {
            int start = int.Parse(m.Groups[4].Value), at = text.IndexOf("林青", start, StringComparison.Ordinal);
            return (JsonNode)new JsonObject
            {
                ["finding_type"] = "CharacterIdentification", ["source_evidence_ids"] = new JsonArray(m.Groups[3].Value),
                ["support_spans"] = new JsonArray(new JsonObject { ["evidence_id"] = m.Groups[3].Value, ["start_utf16"] = at, ["length_utf16"] = 2, ["text"] = "林青" }),
                ["identities"] = new JsonArray(new JsonObject { ["role"] = "Subject", ["entity_id"] = "", ["local_id"] = "p_" + m.Groups[3].Value[..8], ["name"] = "林青", ["kind"] = "Character", ["status"] = "NewIdentity" }),
                ["identity_resolution_status"] = "NewIdentity", ["fact_kind"] = "PrimaryName", ["value"] = "林青", ["relationship_type"] = "Unknown",
                ["event_type"] = "None", ["event_actuality"] = "NotAnEvent", ["event_effect"] = "None", ["effective_chapter"] = 1, ["effective_to_chapter"] = 0,
                ["revealed_chapter"] = 1, ["self_reference"] = "", ["addressee_reference"] = "", ["cpu_candidate_ids"] = new JsonArray(),
                ["affected_fact_ids"] = new JsonArray(), ["confidence"] = 0.8, ["uncertainties"] = new JsonArray(), ["reason"] = "Explicit name in quoted source"
            };
        }).ToArray());
        var root = new JsonObject
        {
            ["schema_version"] = 2, ["project_id"] = evidence[0].Groups[1].Value, ["chapter_id"] = evidence[0].Groups[2].Value,
            ["reviewed_evidence_ids"] = new JsonArray(evidence.Select(m => (JsonNode)m.Groups[3].Value).ToArray()), ["findings"] = findings
        };
        return Envelope(root.ToJsonString());
    }

    [Fact]
    public async Task Probe_semantic_wire_mode_inspects_exact_production_gemini_request_offline()
    {
        var options = ProbeRequest.Options(); var inspection = await ProbeRequest.InspectAsync(options, schemaMode: ProbeSchemaMode.GeminiSemanticWire);
        Assert.Equal(GeminiSemanticWire.Schema.GetRawText(), inspection.GenerationConfig.GetProperty("responseJsonSchema").GetRawText());
        Assert.Equal(0, inspection.Schema.AnyOf); Assert.Empty(inspection.Schema.Constraints); Assert.Empty(inspection.Schema.ArrayLimits);
        Assert.Equal(21, inspection.Schema.FindingProperties); Assert.Equal(GeminiSemanticWire.Version, inspection.Comparison.VariantVersion);
        var output = new StringWriter(); int code = await ProbeCommand.RunAsync(["--wire-schema", "gemini-semantic-wire"], new StringReader(""), output, () => throw new InvalidOperationException("no key offline"),
            () => throw new InvalidOperationException("no transport offline"));
        Assert.Equal(0, code); Assert.Contains("\"httpRequests\": 0", output.ToString());
    }

    [Fact]
    public async Task Probe_semantic_wire_live_mock_decodes_neutral_reply_with_one_post()
    {
        var handler = new Handler(_ => Task.FromResult(Envelope(ProbeRequest.ExpectedResponse)));
        var report = await new ProbeSession(ProbeRequest.Options(), ProbeSchemaMode.GeminiSemanticWire).RunOnceAsync(Secret, ProbeRequest.Confirmation, () => handler);
        Assert.Equal("SchemaAcceptedNeutralResponseValid", report.Code); Assert.Equal(1, handler.Calls); Assert.True(report.ResponseContractValid);
    }

    [Fact]
    public async Task Synthetic_smoke_runs_full_engine_with_one_post_and_stores_only_unconfirmed_proposals()
    {
        var handler = new Handler(WireAnswer);
        var report = await SyntheticSurveySmoke.RunOnceAsync(ProbeRequest.Options(), _root, Secret, SyntheticSurveySmoke.Confirmation, () => handler);
        Assert.Equal(1, handler.Calls); Assert.Equal(1, report.HttpRequests); Assert.Equal(200, report.HttpStatus);
        Assert.Equal(GeminiSemanticWire.Version, report.WireFormat); Assert.Equal(2, report.EvidenceInBatch);
        Assert.Equal("SurveyValidated", report.Code); Assert.Equal("Validated", report.BatchStatus);
        Assert.Equal(2, report.Proposals); Assert.Equal(2, report.EvidenceValidProposals); Assert.Equal(0, report.Reviews);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report));
        Assert.All(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories), f => Assert.DoesNotContain(Secret, File.ReadAllText(f)));
    }

    [Fact]
    public async Task Synthetic_smoke_reports_only_allowlisted_5xx_status_and_hints_never_the_message()
    {
        string body = JsonSerializer.Serialize(new { error = new { code = 503, status = "UNAVAILABLE", message = "The model is overloaded. Please try again later. " + Secret } });
        var bad = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent(body, Encoding.UTF8, "application/json") }));
        var report = await SyntheticSurveySmoke.RunOnceAsync(ProbeRequest.Options(), _root, Secret, SyntheticSurveySmoke.Confirmation, () => bad);
        Assert.Equal(1, bad.Calls); Assert.Equal("Http503", report.Code); Assert.Equal("UNAVAILABLE", report.ProviderStatus);
        Assert.Equal(["ModelOverloaded", "TryAgainLater"], report.ProviderHints);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report));
        Assert.All(Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories), f => Assert.DoesNotContain(Secret, File.ReadAllText(f)));
        Assert.Equal(("OTHER", (IReadOnlyList<string>)[]), ProviderErrorHints.Classify(Encoding.UTF8.GetBytes("{\"error\":{\"status\":\"" + Secret + "\"}}")));
        Assert.Equal("NotJson", ProviderErrorHints.Classify(Encoding.UTF8.GetBytes(Secret)).Status);
    }

    [Fact]
    public async Task Synthetic_smoke_requires_confirmation_and_never_retries_provider_errors()
    {
        var never = new Handler(_ => throw new InvalidOperationException("must not send"));
        var denied = await SyntheticSurveySmoke.RunOnceAsync(ProbeRequest.Options(), _root, Secret, "yes", () => never);
        Assert.Equal("ConfirmationRequired", denied.Code); Assert.Equal(0, never.Calls); Assert.False(Directory.Exists(_root));
        foreach (int status in new[] { 400, 429, 503 })
        {
            var bad = new Handler(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{\"error\":{\"status\":\"INVALID_ARGUMENT\"}}") }));
            var report = await SyntheticSurveySmoke.RunOnceAsync(ProbeRequest.Options(), Path.Combine(_root, status.ToString()), Secret, SyntheticSurveySmoke.Confirmation, () => bad);
            Assert.Equal(1, bad.Calls); Assert.Equal(status, report.HttpStatus); Assert.Equal(0, report.Proposals); Assert.StartsWith("Http" + status, report.Code);
        }
    }
}
