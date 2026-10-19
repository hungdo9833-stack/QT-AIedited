using System.Net;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.GeminiSchema.Probe;

public sealed record SyntheticSurveyReport(string Model, string WireFormat, int HttpRequests, int? HttpStatus, string Code, string BatchStatus,
    int EvidenceInBatch, int ReviewedEvidence, int Proposals, int EvidenceValidProposals, int ProposalsWithIssues, int Reviews,
    IReadOnlyDictionary<string, int> FindingTypes, IReadOnlyList<string> AuditIssues, int? InputTokens, int? OutputTokens, string ProjectDirectory)
{
    /// <summary>Allowlisted google.rpc status of a non-2xx reply (never the message).</summary>
    public string? ProviderStatus { get; init; }
    /// <summary>Fixed hint codes matched in the error message; the message itself is never kept.</summary>
    public IReadOnlyList<string> ProviderHints { get; init; } = [];
}

/// <summary>Bounded, allowlist-only reading of a Gemini error envelope.</summary>
public static class ProviderErrorHints
{
    private static readonly (string Needle, string Code)[] Hints =
    [
        ("overloaded", "ModelOverloaded"), ("high demand", "HighDemand"), ("try again later", "TryAgainLater"),
        ("internal error", "InternalError"), ("deadline", "DeadlineExceeded"), ("schema", "MentionsSchema"),
        ("too many states", "SchemaTooManyStates"), ("too complex", "SchemaTooComplex"), ("quota", "MentionsQuota"),
        ("timed out", "TimedOut"), ("unavailable", "MentionsUnavailable")
    ];
    public static (string? Status, IReadOnlyList<string> Hints) Classify(byte[]? body)
    {
        if (body is null) return ("BodyOversized", []);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body, new() { MaxDepth = 24 });
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object || !doc.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != System.Text.Json.JsonValueKind.Object) return ("NotErrorEnvelope", []);
            string? status = error.TryGetProperty("status", out var s) && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString() switch
            {
                "UNAVAILABLE" or "INTERNAL" or "DEADLINE_EXCEEDED" or "RESOURCE_EXHAUSTED" or "INVALID_ARGUMENT" or "FAILED_PRECONDITION" or
                    "PERMISSION_DENIED" or "UNAUTHENTICATED" or "NOT_FOUND" => s.GetString(),
                _ => "OTHER"
            } : null;
            string message = error.TryGetProperty("message", out var m) && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString()! : "";
            return (status, Hints.Where(h => message.Contains(h.Needle, StringComparison.OrdinalIgnoreCase)).Select(h => h.Code).ToArray());
        }
        catch (System.Text.Json.JsonException) { return ("NotJson", []); }
    }
}

/// <summary>
/// End-to-end smoke test of the real production path (SemanticSurveyRequests.Plan →
/// SemanticSurveyEngine → SemanticSurveyBackend.Gemini → wire decode → strict Parse →
/// SemanticQualityAuditor → checkpoint) on a NEW synthetic Project in %TEMP%. Never opens
/// a user Project. Exactly one POST at most: one batch, engine budget 1, transport budget 1,
/// and a guard that refuses a second request or any other host. No retry or fallback.
/// The temporary Project is kept for inspection; nothing is deleted.
/// </summary>
public static class SyntheticSurveySmoke
{
    public const string Confirmation = "SEND ONE SYNTHETIC SEMANTIC SURVEY";
    // Fictional text written for this probe: names, alias, kinship, master/disciple,
    // directional addressing, a historical event and a planned (not completed) event.
    private const string PartA = "林青推开院门，对坐在树下的老人说道：“爹，我回来了。”周宁放下茶杯，笑道：“青儿，你终于回来了。”院里的弟子们都叫林青“小师兄”。";
    private const string PartB = "三年前，周宁收林青为徒。今日林青对白月说：“师妹，明日我们一起下山。”白月答道：“好，师兄。”可是两人还没有出发。";
    private const string Filler = "山风吹过竹林，院中一片安静。";
    public static string Text => Pad(PartA) + Pad(PartB);
    private static string Pad(string part) { var text = new System.Text.StringBuilder(part); while (text.Length + Filler.Length <= 880) text.Append(Filler); return text.Append('\n').ToString(); }

    /// <summary>The single batch that would be sent. Offline: in memory only, no file is written.</summary>
    public static SemanticSurveyRequest Plan(ModelOptions options, Guid projectId, Guid chapterId, string text, CancellationToken ct = default)
    {
        var memory = new EntityMemoryState { ProjectId = projectId };
        var plan = SemanticSurveyRequests.Plan(memory, [new(chapterId, 1, text)], new() { ProjectId = projectId }, "gemini:" + options.Model, options, ct, new(2));
        if (plan.Count == 0 || plan[0].WireVersion != 2) throw new InvalidDataException("SyntheticPlanNotMultiEvidence");
        return plan[0];
    }

    public static async Task<SyntheticSurveyReport> RunOnceAsync(ModelOptions options, string root, string apiKey, string confirmation,
        Func<HttpMessageHandler> transportFactory, CancellationToken ct = default)
    {
        if (confirmation != Confirmation || string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains('\r') || apiKey.Contains('\n'))
            return new(options.Model, GeminiSemanticWire.Version, 0, null, confirmation != Confirmation ? "ConfirmationRequired" : "ApiKeyMissingOrInvalid",
                "NotStarted", 0, 0, 0, 0, 0, 0, new Dictionary<string, int>(), [], null, null, root);
        var repo = new ProjectRepository(); var session = await repo.CreateAsync(root, "QT Studio Gemini synthetic smoke", ct);
        var chapter = TranslationService.CreateChapter("Synthetic smoke", Text); await repo.AddChapterAsync(session, chapter, 1, cancellationToken: ct);
        var request = Plan(options, session.Manifest.ProjectId, chapter.Id, chapter.OriginalText, ct);
        var store = new SemanticSurveyStore(session); var legacy = new BookIntelligenceState { ProjectId = request.ProjectId };
        var sources = new[] { new CensusSource(chapter.Id, 1, chapter.OriginalText) };
        var plan = new[] { request }; var guard = new OnePost(transportFactory());
        var budget = new SemanticSurveyBudget(1, 1_000_000);
        using var backend = SemanticSurveyBackend.Gemini(apiKey, GeminiSurveyProjectScope.Resolve(apiKey, Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_PROJECT_ID")), options, budget, guard);
        var state = await new SemanticSurveyEngine(backend).RunAsync(store, plan, sources, legacy, options, budget,
            SemanticSurveyEngine.DryRun(plan, await store.LoadAsync(ct), legacy).Fingerprint, ct: ct);
        var attempt = state.Attempts.Last(); var proposals = state.Proposals.Where(x => x.BatchId == request.Id).ToArray();
        return new(options.Model, backend.WireFormat, guard.Requests, guard.Status, attempt.ErrorCode.Length == 0 ? "SurveyValidated" : attempt.ErrorCode,
            attempt.Status.ToString(), request.Units.Count,
            state.EvidenceResults.Where(x => x.AttemptId == attempt.Id && x.Status is SemanticBatchStatus.Validated or SemanticBatchStatus.Analyzed).Count(),
            proposals.Length, proposals.Count(x => x.Audit.EvidenceValid), proposals.Count(x => x.Audit.Issues.Count > 0), state.Reviews.Count,
            proposals.GroupBy(x => x.Audit.Finding.FindingType.ToString()).ToDictionary(x => x.Key, x => x.Count()),
            proposals.SelectMany(x => x.Audit.Issues).Distinct().Order().ToArray(), attempt.InputTokens, attempt.OutputTokens, root)
        { ProviderStatus = guard.ProviderStatus, ProviderHints = guard.ProviderHints };
    }

    private sealed class OnePost(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public int? Status { get; private set; }
        public string? ProviderStatus { get; private set; }
        public IReadOnlyList<string> ProviderHints { get; private set; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.Host != "generativelanguage.googleapis.com" ||
                Interlocked.CompareExchange(ref _requests, 1, 0) != 0) throw new InvalidOperationException("SingleHttpBudgetExceeded");
            var response = await base.SendAsync(request, ct); Status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode) return response;
            // Classify in memory, then hand the same bytes on so production handling is unchanged.
            byte[] bytes = await response.Content.ReadAsByteArrayAsync(ct);
            (ProviderStatus, ProviderHints) = ProviderErrorHints.Classify(bytes.Length > 32_768 ? null : bytes);
            var replacement = new ByteArrayContent(bytes); replacement.Headers.ContentType = response.Content.Headers.ContentType;
            response.Content.Dispose(); response.Content = replacement; return response;
        }
    }
}
