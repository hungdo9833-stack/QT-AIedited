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
    long ConservativeReservedTokens, IReadOnlyList<SemanticSurveyRequest> Requests)
{
    public int TotalEvidence => Requests.Sum(x => x.Units.Count);
    public int CompletedEvidence { get; init; }
    public int PendingEvidence { get; init; }
    public int BlockedEvidence { get; init; }
    public int OverContextBatches { get; init; }
    public IReadOnlyList<SemanticSurveyRequest> ExecutionRequests { get; init; } = [];
    public IReadOnlyDictionary<int, int> EvidencePerBatch => Requests.GroupBy(x => x.Units.Count).ToDictionary(x => x.Key, x => x.Count());
}

public sealed class SemanticSurveyEngine(IJsonSchemaOllamaClient backend)
{
    public static SemanticDryRun DryRun(IReadOnlyList<SemanticSurveyRequest> plan, SemanticSurveyState state, BookIntelligenceState legacy)
    {
        var cache = new SemanticEvidenceCache(state, legacy); var ready = cache.Ready(plan); var units = plan.SelectMany(x => x.Units).ToArray();
        return new(EntityMemoryIdentity.Fingerprint(plan.Select(x => x.Id).ToArray()), plan.Count, ready.Count, plan.Count(x => x.Units.Any(cache.Blocked)),
            ready.Sum(x => (long)x.EstimatedInputTokens), ready.Sum(x => (long)x.ReservedTokens), plan)
        {
            CompletedEvidence = units.Count(x => cache.Done(x) && !cache.Blocked(x)), PendingEvidence = ready.Sum(x => x.Units.Count),
            BlockedEvidence = units.Count(cache.Blocked), ExecutionRequests = ready,
            OverContextBatches = ready.Count(x => x.PlanningOptions is { } o && x.EstimatedInputTokens + o.MaximumOutputTokens > o.ContextSize)
        };
    }
    public static bool Blocked(SemanticSurveyRequest request, SemanticSurveyState state, BookIntelligenceState legacy)
    {
        var cache = new SemanticEvidenceCache(state, legacy);
        return request.Units.Any(cache.Blocked);
    }
    public async Task<SemanticSurveyState> RunAsync(SemanticSurveyStore store, IReadOnlyList<SemanticSurveyRequest> plan, IReadOnlyList<CensusSource> sources,
        BookIntelligenceState legacy, ModelOptions options, SemanticSurveyBudget budget, string approvedPlanFingerprint,
        IProgress<SemanticSurveyProgress>? progress = null, CancellationToken ct = default, Func<SemanticSurveyRequest, Task<bool>>? current = null)
    {
        options.Validate(); budget.Validate(); using var lease = store.AcquireRunLease(); var state = await store.LoadAsync(ct).ConfigureAwait(false);
        if (DryRun(plan, state, legacy).Fingerprint != approvedPlanFingerprint || plan.Any(x => x.ProjectId != state.ProjectId)) throw new InvalidOperationException("Dry-run plan changed; preview again before sending.");
        if (plan.Any(x => x.PlanningOptions is not null && x.PlanningOptions != options)) throw new InvalidOperationException("Model/options changed; preview again before sending.");
        var cache = new SemanticEvidenceCache(state, legacy);
        var execution = cache.Ready(plan).Concat(plan.SelectMany(x => x.Units).Where(x => cache.Blocked(x) && state.Status(x.Id) != SemanticBatchStatus.Blocked))
            .OrderBy(x => x.ChapterNumber).ThenBy(x => x.Evidence.StartUtf16).ToArray();
        state = await store.UpdateAsync(state.Revision, s =>
        {
            foreach (var r in execution)
            {
                foreach (var unit in r.Units) if (!s.Evidence.Any(x => x.Id == unit.Evidence.Id)) s.Evidence.Add(unit.Evidence);
                if (!s.Batches.Any(x => x.Id == r.Id)) s.Batches.Add(new(r.Id, r.ProjectId, r.Evidence.Id, r.BackendSignature, options, r.ContextFingerprint,
                    r.DependencyFingerprint, r.ContextFactIds, r.AllowedIdentities, r.CpuCandidates,
                    r.WireVersion == 1 ? SemanticSurveyRequests.PromptVersion : SemanticAdaptiveBatching.PromptVersion,
                    r.WireVersion == 1 ? SemanticSurveyContract.Version : SemanticSurveyContract.MultiEvidenceVersion,
                    r.WireVersion == 1 ? null : r.Units.Select(x => new SemanticEvidenceKey(x.Evidence.Id, x.Id)).ToArray(), r.ContextEventIds));
            }
        }, ct).ConfigureAwait(false);
        int requests = 0, reserved = 0, processed = 0;
        foreach (var r in execution)
        {
            ct.ThrowIfCancellationRequested();
            bool blocked = r.Units.Any(cache.Blocked);
            if (!blocked && (requests >= budget.MaximumRequests || (long)reserved + r.ReservedTokens > budget.MaximumReservedTokens)) break;
            if (!blocked && r.Units.Any(u => !sources.Any(s => s.ChapterId == u.Evidence.ChapterId && s.ChapterNumber == u.ChapterNumber && CorpusIntegrityAuditor.SourceHash(s.OriginalText) == u.Evidence.SourceSha256)))
                throw new InvalidOperationException("Source changed; preview again before sending.");
            if (current is not null && !await current(r).ConfigureAwait(false)) throw new InvalidOperationException("Source/memory changed during survey; retained checkpoints, run dry-run again.");
            Guid reservation = Guid.NewGuid();
            state = await store.UpdateAsync(state.Revision, s => s.Attempts.Add(new(reservation, r.Id, null, SemanticBatchStatus.Pending, DateTimeOffset.UtcNow, blocked ? 0 : r.ReservedTokens, null, null, "ReservedBeforeSend", "")), ct).ConfigureAwait(false);
            if (!blocked) { requests++; reserved += r.ReservedTokens; }
            SemanticBatchStatus status = SemanticBatchStatus.Failed; string error = ""; string responseFingerprint = "";
            int? input = null, output = null; SemanticAudit? audit = null; bool cancelled = false, stopAfterFailure = false;
            var failureStage = SemanticFailureStage.Context;
            try
            {
                if (blocked) { status = SemanticBatchStatus.Blocked; error = "InheritedProviderBlock"; }
                else
                {
                    if (r.EstimatedInputTokens + options.MaximumOutputTokens > options.ContextSize) throw new InvalidDataException("ContextBudgetExceeded");
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(options.RequestTimeout);
                    failureStage = SemanticFailureStage.Provider;
                    var completion = await backend.TranslateStructuredAsync(r.Prompt, options, SemanticSurveyContract.SchemaFor(r.WireVersion), timeout.Token).ConfigureAwait(false);
                    input = completion.Metrics.InputTokens; output = completion.Metrics.OutputTokens; responseFingerprint = EntityMemoryIdentity.Fingerprint(completion.Text);
                    failureStage = SemanticFailureStage.Completion;
                    if (completion.Done == false || completion.DoneReason is not (null or "stop")) throw new InvalidDataException("IncompleteProviderResponse");
                    failureStage = SemanticFailureStage.Parse;
                    var parsed = SemanticSurveyContract.Parse(completion.Text);
                    failureStage = SemanticFailureStage.Audit;
                    audit = SemanticQualityAuditor.Audit(parsed, r, sources);
                    status = audit.Validated ? SemanticBatchStatus.Validated : SemanticBatchStatus.Analyzed;
                    error = string.Join(",", audit.Issues.Concat(audit.UnprocessedCpuIds.Count > 0 ? new[] { "UnprocessedCpuEvents" } : []).Concat(audit.Findings.SelectMany(x => x.Issues)).Distinct()).Truncate(240);
                }
            }
            catch (GeminiSurveyBlockedException) { status = SemanticBatchStatus.Blocked; error = "ProviderRefusal"; }
            catch (OperationCanceledException) { cancelled = ct.IsCancellationRequested; error = cancelled ? "Cancelled" : "Timeout"; }
            catch (Exception ex)
            {
                // Never persist provider messages/raw JSON/prompts: they may contain secrets or corpus text.
                var diagnostic = SemanticProviderDiagnostics.Classify(ex, failureStage);
                error = diagnostic.Code; stopAfterFailure = diagnostic.StopRun;
            }
            state = await store.UpdateAsync(state.Revision, s =>
            {
                if (audit is not null) foreach (var finding in audit.Findings)
                {
                    Guid id = EntityMemoryIdentity.StableId(s.ProjectId, "semantic-proposal:" + r.Id + ":" + EntityMemoryIdentity.Fingerprint(finding));
                    if (!s.Proposals.Any(x => x.Id == id)) s.Proposals.Add(new(id, r.Id, finding));
                }
                Guid attemptId = Guid.NewGuid();
                s.Attempts.Add(new(attemptId, r.Id, reservation, status, DateTimeOffset.UtcNow, blocked ? 0 : r.ReservedTokens, input, output, error, responseFingerprint));
                foreach (var unit in r.Units)
                {
                    bool reviewed = audit?.ReviewedEvidenceIds?.Contains(unit.Evidence.Id) == true;
                    var unitStatus = status is SemanticBatchStatus.Blocked or SemanticBatchStatus.Failed ? status : !reviewed ? SemanticBatchStatus.Pending :
                        audit!.Findings.Any(x => x.Finding.SourceEvidenceIds.Contains(unit.Evidence.Id) && !x.EvidenceValid) ? SemanticBatchStatus.Failed :
                        audit!.Findings.Where(x => x.Finding.SourceEvidenceIds.Contains(unit.Evidence.Id)).All(x => x.EvidenceValid && x.Issues.Count == 0) &&
                        !unit.CpuCandidates.Any(x => audit.UnprocessedCpuIds.Contains(x.Id)) ? SemanticBatchStatus.Validated : SemanticBatchStatus.Analyzed;
                    s.EvidenceResults.Add(new(Guid.NewGuid(), attemptId, unit.Evidence.Id, unit.Id, unitStatus));
                }
            }, CancellationToken.None).ConfigureAwait(false);
            processed++; progress?.Report(new(processed, execution.Length, requests, reserved, error.Length > 0 ? error : status.ToString()));
            if (cancelled) throw new OperationCanceledException(ct); if (stopAfterFailure) break;
        }
        return state;
    }
}

internal static class SemanticTextExtensions { internal static string Truncate(this string value, int max) => value.Length <= max ? value : value[..max]; }
