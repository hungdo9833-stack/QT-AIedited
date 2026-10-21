using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Addressing;

/// <summary>What the CPU plans for the whole book against the current ledger: scene tasks, then characters and pairs.</summary>
public sealed record AddressingSurveyWork(IReadOnlyList<AddressingTask> Scenes, IReadOnlyList<AddressingTask> Tasks, AddressingLedgerState State, AddressingSettings Settings)
{
    public IReadOnlyList<AddressingTask> PendingScenes { get; } = Scenes.Where(t => !State.Done(t)).ToArray();
    public IReadOnlyList<AddressingTask> PendingTasks { get; } = Tasks.Where(t => !State.Done(t)).ToArray();
    public int PendingCharacters => PendingTasks.Count(t => t.Kind == AddressingTaskKind.Character);
    public int PendingPairs => PendingTasks.Count(t => t.Kind == AddressingTaskKind.Pair);
    public int PendingIdentities => PendingTasks.Count(t => t.Kind == AddressingTaskKind.Identity);
    /// <summary>Requests still needed. Pairs are planned again once the scenes are answered, so this is an estimate.</summary>
    public int EstimatedRequests => EstimateRequests(null);
    /// <summary>Requests still needed with this model: its context and output limit decide how many tasks fit in one request.</summary>
    public int EstimateRequests(ModelOptions? options) =>
        AddressingAiSurvey.CountRequests(PendingScenes, Settings, options) + AddressingAiSurvey.CountRequests(PendingTasks, Settings, options);
    public bool Complete => PendingScenes.Count == 0 && PendingTasks.Count == 0;
}

public sealed record AddressingSurveyProgress(string Stage, int RequestsSent, int MaximumRequests, string Message);

/// <param name="StoppedBy">null when nothing is left to ask; RequestBudget when the run's limit was reached (press again to
/// continue); otherwise the controlled error code that stopped it (Http503, ProviderRefusal, Cancelled…).</param>
public sealed record AddressingSurveyResult(AddressingLedgerState State, int RequestsSent, string? StoppedBy, AddressingSurveyWork Remaining);

/// <summary>
/// The whole survey behind one "Khảo sát" button: CPU names → scene tasks (who speaks) → characters and pairs, planned again
/// with the verified scene turns so new pairs are asked. Writes only the ledger of the Project (never the Character Project:
/// that is "Hoàn thành"). One try per request, a per-click request limit, checkpoint after every request; pressing again
/// continues where it stopped and never resends what is done.
/// </summary>
public sealed class AddressingSurveyRunner(IJsonSchemaOllamaClient client)
{
    private const int MaximumRounds = 4;

    /// <summary>CPU only, no provider: what a survey would ask now.</summary>
    public static AddressingSurveyWork Plan(Guid projectId, IReadOnlyList<CensusSource> sources, ProjectResources resources, AddressingLedgerState state,
        AddressingSettings? settings = null, CancellationToken ct = default)
    {
        settings ??= new(); settings.Validate();
        var (names, titles) = AddressingAiSurvey.SurveyNames(projectId, sources, resources, 3, settings.FilterJunkCandidates, ct);
        var scenes = AddressingAiSurvey.PlanScenes(sources, names, settings, ct, titles);
        var turns = state.SceneTurns();
        // Identity questions go with characters and pairs: they need no scene answer, only the verified turns (who talks to whom).
        var tasks = AddressingAiSurvey.PlanTasks(sources, names, settings, ct, turns).Concat(AddressingAiSurvey.PlanIdentities(sources, names, settings, ct, turns)).ToArray();
        return new(scenes, tasks, state, settings);
    }

    public async Task<AddressingSurveyResult> RunAsync(ProjectSession session, IReadOnlyList<CensusSource> sources, ProjectResources resources,
        ModelOptions options, int maximumRequests, AddressingSettings? settings = null, IProgress<AddressingSurveyProgress>? progress = null, CancellationToken ct = default)
    {
        if (maximumRequests is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(maximumRequests));
        settings ??= new(); settings.Validate(); options.Validate();
        var store = new AddressingLedgerStore(session);
        var engine = new AddressingAiEngine(client);
        var state = await store.LoadAsync(ct).ConfigureAwait(false);
        int sent = 0; string? stoppedBy = null;
        // Within one click a task is sent at most once: an invalid answer is retried by the next click, never in a loop.
        var asked = new HashSet<string>(StringComparer.Ordinal);
        AddressingSurveyWork work = null!;
        for (int round = 0; round < MaximumRounds; round++)
        {
            progress?.Report(new("CPU", sent, maximumRequests, "Đang lập kế hoạch khảo sát (CPU, không gọi AI)…"));
            var current = state;
            work = await Task.Run(() => Plan(session.Manifest.ProjectId, sources, resources, current, settings, ct), ct).ConfigureAwait(false);
            // Scenes first: their verified turns decide which quotes each pair task gets.
            bool scenes = work.PendingScenes.Any(t => !asked.Contains(t.TaskId));
            var batch = (scenes ? work.PendingScenes : work.PendingTasks).Where(t => asked.Add(t.TaskId)).ToArray();
            if (batch.Length == 0) break;
            if (sent >= maximumRequests) { stoppedBy = "RequestBudget"; break; }
            string stage = scenes ? "Cảnh" : "Nhân vật và cặp";
            int before = sent, shown = sent; // Progress callbacks run elsewhere: they count their own copy only.
            var run = await engine.RunAsync(store, batch, options, maximumRequests - sent, settings,
                new Progress<string>(m => progress?.Report(new(stage, Interlocked.Increment(ref shown), maximumRequests, m))), ct).ConfigureAwait(false);
            sent = before + run.RequestsSent; state = run.State;
            progress?.Report(new(stage, sent, maximumRequests, $"Xong đợt {stage.ToLowerInvariant()}: {run.RequestsSent} request."));
            if (run.StoppedBy is not null) { stoppedBy = run.StoppedBy; break; }
        }
        var remaining = await Task.Run(() => Plan(session.Manifest.ProjectId, sources, resources, state, settings, ct), ct).ConfigureAwait(false);
        if (stoppedBy is null && !remaining.Complete && remaining.PendingScenes.Concat(remaining.PendingTasks).All(t => asked.Contains(t.TaskId)))
            stoppedBy = "RetryNextRun"; // Only tasks answered invalidly this click are left; the next click asks them once more.
        return new(state, sent, stoppedBy, remaining);
    }
}
