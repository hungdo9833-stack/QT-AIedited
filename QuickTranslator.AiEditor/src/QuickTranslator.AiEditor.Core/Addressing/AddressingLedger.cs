using System.Collections.Concurrent;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Addressing;

public sealed record AddressingAttempt(Guid Id, string RequestId, IReadOnlyList<string> TaskIds, string Model, DateTimeOffset At, bool Succeeded,
    string ErrorCode, int? InputTokens, int? OutputTokens);

/// <summary>Append-only checkpoint of step 2. Stores refs, hypotheses and controlled codes; never prompts, raw replies or keys.</summary>
public sealed class AddressingLedgerState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public List<AddressingAttempt> Attempts { get; set; } = [];
    public List<AddressingProposal> Proposals { get; set; } = [];
    public List<AddressingReview> Reviews { get; set; } = [];

    /// <summary>The latest human decision for a proposal, or null when it is still pending.</summary>
    public AddressingReview? LatestReview(Guid proposalId) => Reviews.LastOrDefault(r => r.ProposalId == proposalId);

    /// <summary>
    /// Done = a verified proposal exists, the task was answered twice without one (no endless resend), or the
    /// provider refused it (never resent automatically; kept for manual review).
    /// </summary>
    public bool Done(string taskId) => Proposals.Any(p => p.TaskId == taskId && p.EvidenceValid) ||
        // Count answered attempts, not proposals: identical invalid answers share one proposal id.
        Attempts.Count(a => a.Succeeded && a.TaskIds.Contains(taskId)) >= 2 ||
        Attempts.Any(a => a.ErrorCode == "ProviderRefusal" && a.TaskIds.Contains(taskId));

    /// <summary>
    /// A scene's id is its quotes. If every earlier answer had another participant list (a name discovered since),
    /// the question changed: ask again. Other kinds use <see cref="Done(string)"/>.
    /// </summary>
    public bool Done(AddressingTask task)
    {
        if (task.Kind != AddressingTaskKind.Scene) return Done(task.TaskId);
        var answered = Proposals.Where(p => p.TaskId == task.TaskId).ToArray();
        return (answered.Length == 0 || answered.Any(p => p.Subject == task.Subject)) && Done(task.TaskId);
    }

    /// <summary>Usable turns of verified scene proposals: evidence for planning pair tasks (step 2b), never profile data.</summary>
    public IReadOnlyList<AddressingTurn> SceneTurns() => Proposals
        .Where(p => p.Kind == AddressingTaskKind.Scene && p.EvidenceValid && LatestReview(p.Id)?.Decision != AddressingDecision.Rejected)
        .SelectMany(p => p.Turns.Where(t => t.IsUsable())).ToArray();
}

public sealed class AddressingLedgerStore(ProjectSession session)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public string PathName => session.Resolve("Settings/addressing-ledger.v1.json");

    public Task<AddressingLedgerState> LoadAsync(CancellationToken ct = default) =>
        SimpleFileStorage.ReadOptionalAsync(PathName, Decode, () => new AddressingLedgerState { ProjectId = session.Manifest.ProjectId }, ct);

    public async Task<AddressingLedgerState> UpdateAsync(Action<AddressingLedgerState> update, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(PathName, _ => new(1, 1)); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var state = await LoadAsync(ct).ConfigureAwait(false);
            int attempts = state.Attempts.Count, proposals = state.Proposals.Count, reviews = state.Reviews.Count;
            update(state);
            if (state.Attempts.Count < attempts || state.Proposals.Count < proposals || state.Reviews.Count < reviews)
                throw new InvalidOperationException("Addressing ledger is append-only.");
            state.Revision++;
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            await SimpleFileStorage.WriteAsync(PathName, JsonSerializer.SerializeToUtf8Bytes(state, DocumentJson.Options), Decode, ct).ConfigureAwait(false);
            return state;
        }
        finally { gate.Release(); }
    }

    private AddressingLedgerState Decode(byte[] bytes)
    {
        var state = JsonSerializer.Deserialize<AddressingLedgerState>(bytes, DocumentJson.Options) ?? throw new InvalidDataException("Empty addressing ledger.");
        if (state.SchemaVersion != 1 || state.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Addressing ledger belongs to another Project or schema.");
        return state;
    }
}

public sealed record AddressingRunResult(AddressingLedgerState State, int RequestsSent, int Planned, string? StoppedBy);

/// <summary>
/// Runs planned requests through one selected backend (Gemini via SemanticSurveyBackend, or Ollama): one try per
/// request, a per-run request budget, checkpoint after every request, stop on service/quota/config failures.
/// </summary>
public sealed class AddressingAiEngine(IJsonSchemaOllamaClient client)
{
    public async Task<AddressingRunResult> RunAsync(AddressingLedgerStore store, IReadOnlyList<AddressingTask> tasks, ModelOptions options,
        int maximumRequests, AddressingSettings? settings = null, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        options.Validate();
        if (maximumRequests is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        var plan = AddressingAiSurvey.PlanRequests(tasks.Where(t => !state.Done(t)).ToArray(), options.Model, settings);
        int sent = 0; string? stoppedBy = null;
        foreach (var request in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (sent >= maximumRequests) { stoppedBy = "RequestBudget"; break; }
            if (request.EstimatedInputTokens + options.MaximumOutputTokens > options.ContextSize) { stoppedBy = "ContextBudgetExceeded"; break; }
            sent++;
            IReadOnlyList<AddressingProposal>? proposals = null; string error = ""; int? input = null, output = null; bool stop = false;
            var stage = SemanticFailureStage.Provider;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(options.RequestTimeout);
                var completion = await client.TranslateStructuredAsync(request.Prompt, options, AddressingAiSurvey.Schema, timeout.Token).ConfigureAwait(false);
                input = completion.Metrics.InputTokens; output = completion.Metrics.OutputTokens;
                stage = SemanticFailureStage.Completion;
                if (completion.Done == false || completion.DoneReason is not (null or "stop")) throw new InvalidDataException("IncompleteProviderResponse");
                stage = SemanticFailureStage.Parse;
                proposals = AddressingAiSurvey.Verify(completion.Text, request);
            }
            catch (GeminiSurveyBlockedException) { error = "ProviderRefusal"; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { error = "Cancelled"; stop = true; }
            catch (OperationCanceledException) { error = "Timeout"; }
            catch (Exception ex)
            {
                // Controlled codes only: never persist provider messages, prompts or source text.
                var diagnostic = SemanticProviderDiagnostics.Classify(ex, stage); error = diagnostic.Code; stop = diagnostic.StopRun;
            }
            state = await store.UpdateAsync(s =>
            {
                s.Attempts.Add(new(Guid.NewGuid(), request.Id, request.Tasks.Select(t => t.TaskId).ToArray(), options.Model, DateTimeOffset.UtcNow,
                    proposals is not null, error, input, output));
                foreach (var p in proposals ?? []) if (!s.Proposals.Any(x => x.Id == p.Id)) s.Proposals.Add(p);
            }, CancellationToken.None).ConfigureAwait(false);
            progress?.Report(proposals is not null ? $"OK {request.Tasks.Count} tasks" : error);
            if (stop || error == "ProviderRefusal") { stoppedBy = error; break; }
        }
        return new(state, sent, plan.Count, stoppedBy);
    }
}
