using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit.Abstractions;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SemanticProviderDiagnosticsTests(ITestOutputHelper output) : IDisposable
{
    private const string Secret = "SENTINEL_PRIVATE_KEY_AND_ORIGINAL_NEVER_PERSIST";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-provider-diag-" + Guid.NewGuid().ToString("N"));
    private readonly ModelOptions _options = new() { Model = "gemini-3.8-flash", ContextSize = 65536, MaximumOutputTokens = 4096 };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return respond(request); }
    }
    private static HttpResponseMessage Json(string text, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private async Task<(SemanticSurveyStore Store, SemanticFixture Fixture, IReadOnlyList<SemanticSurveyRequest> Plan)> Create(int evidence = 4)
    {
        var repo = new ProjectRepository(); var session = await repo.CreateAsync(_root, "Synthetic provider diagnostic");
        var fixture = new SemanticFixture(AdaptiveSemanticTests.Windows(evidence)); fixture.Memory.ProjectId = session.Manifest.ProjectId;
        var chapter = TranslationService.CreateChapter("Mock only", fixture.Sources[0].OriginalText); await repo.AddChapterAsync(session, chapter, 1);
        fixture.Sources[0] = new(chapter.Id, 1, chapter.OriginalText);
        var plan = SemanticSurveyRequests.Plan(fixture.Memory, fixture.Sources, new() { ProjectId = fixture.Memory.ProjectId }, "mock:gemini", _options, batching: new(2));
        Assert.Equal(evidence / 2, plan.Count); Assert.All(plan, r => Assert.Equal(2, r.Units.Count)); return (new(session), fixture, plan);
    }
    private Task<SemanticSurveyState> Run(SemanticSurveyStore store, SemanticFixture s, IReadOnlyList<SemanticSurveyRequest> plan, IJsonSchemaOllamaClient backend, SemanticSurveyState state, int requests = 1) =>
        new SemanticSurveyEngine(backend).RunAsync(store, plan, s.Sources, new() { ProjectId = s.Memory.ProjectId }, _options, new(requests, 1_000_000),
            SemanticSurveyEngine.DryRun(plan, state, new() { ProjectId = s.Memory.ProjectId }).Fingerprint);
    private Handler Success(IReadOnlyList<SemanticSurveyRequest> plan, bool findings = false, List<Guid>? sent = null) => new(async request =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
        string prompt = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        var r = plan.Single(x => x.Prompt + GeminiSemanticWire.PromptAddendum == prompt); sent?.Add(r.Id);
        string text = GeminiSemanticWire.Encode(SemanticFixture.Json(AdaptiveSemanticTests.Response(r, findings ? r.Units.Select(x => AdaptiveSemanticTests.Name(x)).ToArray() : [])));
        return Json(JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } },
            usageMetadata = new { promptTokenCount = 123, candidatesTokenCount = 45 } }));
    });

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(429, true)]
    [InlineData(408, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    public async Task Http_status_survives_full_chain_with_safe_history_no_hidden_retry_and_manual_resume(int status, bool retryable)
    {
        var (store, s, plan) = await Create(); var bad = new Handler(_ => Task.FromResult(Json(JsonSerializer.Serialize(new { error = new { message = Secret, status = Secret } }), status)));
        using var client = SemanticSurveyBackend.Gemini(Secret, Guid.NewGuid(), _options, new(5, 1_000_000), bad);
        // Service outages and configuration errors must stop even with budget for other batches.
        var state = await Run(store, s, plan, client, await store.LoadAsync(), status is 400 or 401 or 403 or 404 or >= 500 ? 5 : 1);
        Assert.Equal(1, bad.Calls); var attempt = state.Attempts.Last(); Assert.Equal("Http" + status, attempt.ErrorCode);
        Assert.Equal(SemanticBatchStatus.Failed, attempt.Status); Assert.Null(attempt.InputTokens); Assert.Null(attempt.OutputTokens);
        Assert.Empty(state.Proposals); Assert.Equal(4, state.Evidence.Count); Assert.Equal(2, state.EvidenceResults.Count);
        Assert.Equal(retryable, SemanticProviderDiagnostics.Describe(attempt.ErrorCode).CanRetry);
        Assert.Equal(status != 408, SemanticProviderDiagnostics.Describe(attempt.ErrorCode).StopRun);
        Assert.Equal(1, client.Transport.RequestsUsed);
        Assert.Equal(SemanticBatchStatus.Pending, state.Status(plan[1].Id));
        Assert.DoesNotContain(state.Attempts, x => x.ErrorCode == "TransportBudgetExceeded");
        string stored = Encoding.UTF8.GetString(SemanticSurveyStore.Serialize(state)); Assert.DoesNotContain(Secret, stored); Assert.DoesNotContain(s.Sources[0].OriginalText[..50], stored);
        var oldAttempts = state.Attempts.ToArray(); state = await store.LoadAsync();
        var success = Success(plan); using var resume = SemanticSurveyBackend.Gemini(Secret, Guid.NewGuid(), _options, new(5, 1_000_000), success);
        state = await Run(store, s, plan, resume, state); Assert.Equal(1, success.Calls); Assert.Equal(123, state.Attempts.Last().InputTokens);
        Assert.Equal(oldAttempts, state.Attempts.Take(oldAttempts.Length)); Assert.Equal(SemanticBatchStatus.Validated, state.Status(plan[0].Id));
        state = await Run(store, s, plan, resume, state); Assert.Equal(2, success.Calls);
        long revision = state.Revision; state = await Run(store, s, plan, resume, state); Assert.Equal(revision, state.Revision); Assert.Equal(2, success.Calls);
    }

    [Fact]
    public async Task Service_outage_checkpoints_failed_and_pending_evidence_preserves_reviews_and_blocks_on_manual_resume()
    {
        var (store, s, plan) = await Create(8); var sent = new List<Guid>();
        var first = Success(plan, findings: true, sent: sent);
        using var initial = SemanticSurveyBackend.Gemini(Secret, Guid.NewGuid(), _options, new(10, 1_000_000), first);
        var state = await Run(store, s, plan, initial, await store.LoadAsync());
        Assert.Equal(2, state.Proposals.Count);
        state = await store.UpdateAsync(state.Revision, x => x.Reviews.Add(new(Guid.NewGuid(), x.Proposals[0].Id,
            MemoryReviewStatus.Accepted, DateTimeOffset.UtcNow, "Synthetic human review before outage", new())));
        var priorAttempts = state.Attempts.ToArray();
        string priorProposals = EntityMemoryIdentity.Fingerprint(state.Proposals), priorReviews = EntityMemoryIdentity.Fingerprint(state.Reviews);

        var outageSent = new List<Guid>();
        var outage = new Handler(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(_options.Model, request.RequestUri!.AbsolutePath.Split('/').Last().Split(':')[0]);
            string prompt = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
            outageSent.Add(plan.Single(x => x.Prompt + GeminiSemanticWire.PromptAddendum == prompt).Id);
            return outageSent.Count == 1 ? Json("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}") : Json("{}", 503);
        });
        using var unavailable = SemanticSurveyBackend.Gemini(Secret, Guid.NewGuid(), _options, new(10, 1_000_000), outage);
        await Run(store, s, plan, unavailable, state, requests: 10);
        state = await store.LoadAsync(); // Check the actual persisted checkpoint, not just the returned state.
        Assert.Equal(new[] { plan[1].Id, plan[2].Id }, outageSent); Assert.Equal(2, outage.Calls);
        Assert.Equal("Http503", state.Attempts.Last().ErrorCode);
        Assert.Equal(SemanticBatchStatus.Validated, state.Status(plan[0].Id));
        Assert.Equal(SemanticBatchStatus.Blocked, state.Status(plan[1].Id));
        Assert.Equal(SemanticBatchStatus.Failed, state.Status(plan[2].Id));
        Assert.Equal(SemanticBatchStatus.Pending, state.Status(plan[3].Id));
        Assert.All(plan[2].Units, u => Assert.Equal(SemanticBatchStatus.Failed, state.EvidenceResults.Last(x => x.EvidenceId == u.Evidence.Id).Status));
        Assert.All(plan[3].Units, u => Assert.DoesNotContain(state.EvidenceResults, x => x.EvidenceId == u.Evidence.Id));
        Assert.Equal(priorAttempts, state.Attempts.Take(priorAttempts.Length));
        Assert.Equal(priorProposals, EntityMemoryIdentity.Fingerprint(state.Proposals));
        Assert.Equal(priorReviews, EntityMemoryIdentity.Fingerprint(state.Reviews));
        var attempts = state.Attempts.ToArray(); var results = state.EvidenceResults.ToArray();
        var preview = SemanticSurveyEngine.DryRun(plan, state, new() { ProjectId = s.Memory.ProjectId });
        Assert.Equal(2, preview.CompletedEvidence); Assert.Equal(2, preview.BlockedEvidence); Assert.Equal(4, preview.PendingEvidence);

        var resumeSent = new List<Guid>(); var success = Success(plan, findings: true, sent: resumeSent);
        using var resume = SemanticSurveyBackend.Gemini(Secret, Guid.NewGuid(), _options, new(10, 1_000_000), success);
        state = await Run(store, s, plan, resume, state, requests: 10);
        Assert.Equal(new[] { plan[2].Id, plan[3].Id }, resumeSent); Assert.Equal(2, success.Calls);
        Assert.Equal(new[] { plan[0].Id }, sent); Assert.Equal(6, state.Proposals.Count);
        Assert.Equal(priorProposals, EntityMemoryIdentity.Fingerprint(state.Proposals.Take(2).ToList()));
        Assert.Equal(priorReviews, EntityMemoryIdentity.Fingerprint(state.Reviews));
        Assert.Equal(attempts, state.Attempts.Take(attempts.Length)); Assert.Equal(results, state.EvidenceResults.Take(results.Length));
        Assert.Equal(SemanticBatchStatus.Blocked, state.Status(plan[1].Id));
        Assert.Single(state.Attempts, x => x.ErrorCode == "ProviderRefusal");
        Assert.DoesNotContain(state.Attempts, x => x.ErrorCode == "TransportBudgetExceeded");
        long revision = state.Revision; state = await Run(store, s, plan, resume, await store.LoadAsync(), requests: 10);
        Assert.Equal(revision, state.Revision); Assert.Equal(2, success.Calls);
    }

    [Theory]
    [InlineData("schema", "Http400SchemaRejected")]
    [InlineData("complexity", "Http400SchemaRejected")]
    [InlineData("plain400", "Http400")]
    [InlineData("malformed400", "Http400")]
    [InlineData("array400", "Http400")]
    [InlineData("oversized400", "Http400")]
    [InlineData("network", "NetworkFailure")]
    [InlineData("timeout", "Timeout")]
    [InlineData("envelope", "ProviderParseFailure")]
    [InlineData("shape", "ProviderResponseInvalid")]
    [InlineData("semanticJson", "SemanticParseFailure")]
    [InlineData("semanticSchema", "SemanticSchemaViolation")]
    [InlineData("semanticVersionType", "SemanticSchemaViolation")]
    [InlineData("semanticGuid", "SemanticSchemaViolation")]
    public async Task Schema_rejection_network_timeout_envelope_and_local_contract_failures_are_distinct(string kind, string code)
    {
        var (store, s, plan) = await Create();
        var handler = new Handler(_ =>
        {
            if (kind == "network") throw new HttpRequestException(Secret);
            if (kind == "timeout") throw new OperationCanceledException(Secret);
            if (kind == "schema") return Task.FromResult(Json(JsonSerializer.Serialize(new { error = new { message = Secret, details = new[] {
                new { fieldViolations = new[] { new { field = "generation_config.response_json_schema.properties." + Secret, description = Secret } } } } } }), 400));
            if (kind == "complexity") return Task.FromResult(Json("{\"error\":{\"message\":\"The response schema contains too many states for serving. " + Secret + "\"}}", 400));
            if (kind.EndsWith("400", StringComparison.Ordinal)) return Task.FromResult(Json(kind == "array400" ? "[]" : kind == "malformed400" ? Secret :
                JsonSerializer.Serialize(new { error = new { message = kind == "oversized400" ? new string('x', 20_000) + "response_json_schema" : Secret } }), 400));
            if (kind is "envelope" or "shape") return Task.FromResult(Json(kind == "shape" ? "[]" : Secret));
            string text = kind == "semanticJson" ? Secret : kind == "semanticVersionType" ? "{\"schema_version\":\"2\"}" : kind == "semanticGuid" ?
                GeminiSemanticWire.Encode(SemanticFixture.Json(AdaptiveSemanticTests.Response(plan[0]))).Replace(s.Memory.ProjectId.ToString(), "bad-guid", StringComparison.Ordinal) : "{}";
            return Task.FromResult(Json(JsonSerializer.Serialize(new { candidates = new[] { new { finishReason = "STOP", content = new { parts = new[] { new { text } } } } } })));
        });
        using var client = SemanticSurveyBackend.Gemini(Secret, Guid.NewGuid(), _options, new(1, 1_000_000), handler);
        var state = await Run(store, s, plan, client, await store.LoadAsync());
        Assert.Equal(code, state.Attempts.Last().ErrorCode); Assert.Equal(1, handler.Calls); Assert.Empty(state.Proposals);
        Assert.All(state.EvidenceResults, x => Assert.Equal(SemanticBatchStatus.Failed, x.Status));
        Assert.DoesNotContain(Secret, Encoding.UTF8.GetString(SemanticSurveyStore.Serialize(state)));
    }

    [Fact]
    public async Task Transport_budget_is_not_provider_quota_and_refusal_survives_resume_without_resubmission()
    {
        var (store, s, plan) = await Create(); var handler = new Handler(_ => throw new InvalidOperationException("Must not reach HTTP"));
        using (var tooSmall = SemanticSurveyBackend.Gemini(Secret, Guid.NewGuid(), _options, new(1, 1), handler))
        {
            var state = await Run(store, s, plan, tooSmall, await store.LoadAsync());
            Assert.Equal("TransportBudgetExceeded", state.Attempts.Last().ErrorCode); Assert.Equal(0, handler.Calls);
            var diagnostic = SemanticProviderDiagnostics.Describe(state.Attempts.Last().ErrorCode);
            Assert.Contains("nội bộ", diagnostic.Guidance); Assert.Contains("không phải hết quota Gemini", diagnostic.Guidance);
            Assert.NotEqual(diagnostic.Code, SemanticProviderDiagnostics.Describe("Http429").Code);
        }
        var refusal = new Handler(_ => Task.FromResult(Json("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}")));
        using var client = SemanticSurveyBackend.Gemini(Secret, Guid.NewGuid(), _options, new(5, 1_000_000), refusal);
        var blocked = await Run(store, s, plan, client, await store.LoadAsync()); Assert.Equal("ProviderRefusal", blocked.Attempts.Last().ErrorCode); Assert.Equal(1, refusal.Calls);
        var success = Success(plan); using var resume = SemanticSurveyBackend.Gemini(Secret, Guid.NewGuid(), _options, new(5, 1_000_000), success);
        blocked = await Run(store, s, plan, resume, await store.LoadAsync()); Assert.Equal(1, success.Calls); Assert.Equal(SemanticBatchStatus.Blocked, blocked.Status(plan[0].Id));
        Assert.False(SemanticProviderDiagnostics.Describe("ProviderRefusal").CanRetry);
    }

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
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
