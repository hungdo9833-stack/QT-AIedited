using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record SemanticSurveyBudget(int MaximumRequests = 5, int MaximumReservedTokens = 150_000)
{
    public void Validate() { if (MaximumRequests is < 1 or > 1000 || MaximumReservedTokens is < 1 or > 100_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumRequests)); }
}
public sealed record SemanticSurveyProgress(int Processed, int Total, int Requests, int ReservedTokens, string Message);
public sealed record SemanticDryRun(string Fingerprint, int TotalBatches, int PendingRequests, int BlockedBatches, long EstimatedInputTokens,
    long ConservativeReservedTokens, IReadOnlyList<SemanticSurveyRequest> Requests);

public sealed class SemanticSurveyEngine(IJsonSchemaOllamaClient backend)
{
    public static SemanticDryRun DryRun(IReadOnlyList<SemanticSurveyRequest> plan, SemanticSurveyState state, BookIntelligenceState legacy)
    {
        var pending = plan.Where(x => state.Status(x.Id) is SemanticBatchStatus.Pending or SemanticBatchStatus.Failed).ToArray();
        int blocked = plan.Count(x => Blocked(x, state, legacy)); var ready = pending.Where(x => !Blocked(x, state, legacy)).ToArray();
        return new(EntityMemoryIdentity.Fingerprint(plan.Select(x => x.Id).ToArray()), plan.Count, ready.Length, blocked,
            ready.Sum(x => (long)x.EstimatedInputTokens), ready.Sum(x => (long)x.ReservedTokens), plan);
    }
    public static bool Blocked(SemanticSurveyRequest request, SemanticSurveyState state, BookIntelligenceState legacy)
    {
        var e = request.Evidence;
        // Conservatively retain a refusal across model/context/source changes on this range.
        // There is deliberately no automatic "try another model" or refusal reset.
        bool Hit(Guid chapter, string sha, int start, int length) => chapter == e.ChapterId && start < e.StartUtf16 + e.LengthUtf16 && (long)start + length > e.StartUtf16;
        return legacy.Evidence.Any(x => x.AiBlocked && Hit(x.ChapterId, x.SourceSha256, x.StartUtf16, x.LengthUtf16)) ||
            state.Batches.Where(b => state.Attempts.Any(a => a.BatchId == b.Id && a.Status == SemanticBatchStatus.Blocked))
                .Select(b => state.Evidence.Single(x => x.Id == b.EvidenceId)).Any(x => Hit(x.ChapterId, x.SourceSha256, x.StartUtf16, x.LengthUtf16));
    }
    public async Task<SemanticSurveyState> RunAsync(SemanticSurveyStore store, IReadOnlyList<SemanticSurveyRequest> plan, IReadOnlyList<CensusSource> sources,
        BookIntelligenceState legacy, ModelOptions options, SemanticSurveyBudget budget, string approvedPlanFingerprint,
        IProgress<SemanticSurveyProgress>? progress = null, CancellationToken ct = default, Func<SemanticSurveyRequest, Task<bool>>? current = null)
    {
        options.Validate(); budget.Validate(); using var lease = store.AcquireRunLease(); var state = await store.LoadAsync(ct).ConfigureAwait(false);
        if (DryRun(plan, state, legacy).Fingerprint != approvedPlanFingerprint || plan.Any(x => x.ProjectId != state.ProjectId)) throw new InvalidOperationException("Dry-run plan changed; preview again before sending.");
        state = await store.UpdateAsync(state.Revision, s =>
        {
            foreach (var r in plan)
            {
                if (!s.Evidence.Any(x => x.Id == r.Evidence.Id)) s.Evidence.Add(r.Evidence);
                if (!s.Batches.Any(x => x.Id == r.Id)) s.Batches.Add(new(r.Id, r.ProjectId, r.Evidence.Id, r.BackendSignature, options, r.ContextFingerprint,
                    r.DependencyFingerprint, r.ContextFactIds, r.AllowedIdentities, r.CpuCandidates, SemanticSurveyRequests.PromptVersion, SemanticSurveyContract.Version));
            }
        }, ct).ConfigureAwait(false);
        int requests = 0, reserved = 0, processed = 0;
        foreach (var r in plan.OrderBy(x => x.ChapterNumber).ThenBy(x => x.Evidence.StartUtf16))
        {
            ct.ThrowIfCancellationRequested();
            if (state.Status(r.Id) is SemanticBatchStatus.Analyzed or SemanticBatchStatus.Validated or SemanticBatchStatus.Blocked) { processed++; continue; }
            bool blocked = Blocked(r, state, legacy);
            if (!blocked && (requests >= budget.MaximumRequests || (long)reserved + r.ReservedTokens > budget.MaximumReservedTokens)) break;
            if (current is not null && !await current(r).ConfigureAwait(false)) throw new InvalidOperationException("Source/memory changed during survey; retained checkpoints, run dry-run again.");
            Guid reservation = Guid.NewGuid();
            state = await store.UpdateAsync(state.Revision, s => s.Attempts.Add(new(reservation, r.Id, null, SemanticBatchStatus.Pending, DateTimeOffset.UtcNow, blocked ? 0 : r.ReservedTokens, null, null, "ReservedBeforeSend", "")), ct).ConfigureAwait(false);
            if (!blocked) { requests++; reserved += r.ReservedTokens; }
            SemanticBatchStatus status = SemanticBatchStatus.Failed; string error = ""; string responseFingerprint = "";
            int? input = null, output = null; SemanticAudit? audit = null; bool cancelled = false, quota = false;
            try
            {
                if (blocked) { status = SemanticBatchStatus.Blocked; error = "InheritedProviderBlock"; }
                else
                {
                    if (r.EstimatedInputTokens + options.MaximumOutputTokens > options.ContextSize) throw new InvalidDataException("ContextBudgetExceeded");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(options.RequestTimeout);
                    var completion = await backend.TranslateStructuredAsync(r.Prompt, options, SemanticSurveyContract.ResponseSchema, timeout.Token).ConfigureAwait(false);
                    input = completion.Metrics.InputTokens; output = completion.Metrics.OutputTokens; responseFingerprint = EntityMemoryIdentity.Fingerprint(completion.Text);
                    if (completion.Done == false || completion.DoneReason is not (null or "stop")) throw new InvalidDataException("IncompleteProviderResponse");
                    audit = SemanticQualityAuditor.Audit(SemanticSurveyContract.Parse(completion.Text), r, sources);
                    status = audit.Validated ? SemanticBatchStatus.Validated : SemanticBatchStatus.Analyzed;
                    error = string.Join(",", audit.Issues.Concat(audit.UnprocessedCpuIds.Count > 0 ? new[] { "UnprocessedCpuEvents" } : []).Concat(audit.Findings.SelectMany(x => x.Issues)).Distinct()).Truncate(240);
                }
            }
            catch (GeminiSurveyBlockedException) { status = SemanticBatchStatus.Blocked; error = "ProviderRefusal"; }
            catch (OperationCanceledException) { cancelled = ct.IsCancellationRequested; error = cancelled ? "Cancelled" : "Timeout"; }
            catch (Exception ex)
            {
                // Never persist provider messages/raw JSON/prompts: they may contain secrets or corpus text.
                quota = ex is GeminiDailyQuotaExceededException or GeminiRequestBudgetExceededException or SemanticTransportBudgetException || ex is GeminiSurveyHttpException { StatusCode: System.Net.HttpStatusCode.TooManyRequests };
                error = quota ? "QuotaExhausted" : ex is InvalidDataException ? "InvalidSchemaOrResponse" : ex is OllamaRequestException { Kind: OllamaErrorKind.Timeout } ? "Timeout" : "ProviderFailure";
            }
            state = await store.UpdateAsync(state.Revision, s =>
            {
                if (audit is not null) foreach (var finding in audit.Findings)
                {
                    Guid id = EntityMemoryIdentity.StableId(s.ProjectId, "semantic-proposal:" + r.Id + ":" + EntityMemoryIdentity.Fingerprint(finding));
                    if (!s.Proposals.Any(x => x.Id == id)) s.Proposals.Add(new(id, r.Id, finding));
                }
                s.Attempts.Add(new(Guid.NewGuid(), r.Id, reservation, status, DateTimeOffset.UtcNow, blocked ? 0 : r.ReservedTokens, input, output, error, responseFingerprint));
            }, CancellationToken.None).ConfigureAwait(false);
            processed++; progress?.Report(new(processed, plan.Count, requests, reserved, error.Length > 0 ? error : status.ToString()));
            if (cancelled) throw new OperationCanceledException(ct); if (quota) break;
        }
        return state;
    }
}

internal static class SemanticTextExtensions { internal static string Truncate(this string value, int max) => value.Length <= max ? value : value[..max]; }
