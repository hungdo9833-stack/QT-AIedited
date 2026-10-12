namespace QuickTranslator.AiEditor.Core.Models;

public enum QueueStatus { Idle, Running, PauseRequested, Paused, Cancelling, Cancelled, Interrupted, Completed, CompletedWithWarnings, CompletedWithErrors, Faulted }
public enum QueueMode { MissingSegments, FailedSegments }

public sealed class QueueCheckpoint
{
    public Guid RunId { get; set; }
    public QueueStatus Status { get; set; }
    public QueueMode Mode { get; set; }
    public List<Guid> CandidateIds { get; set; } = [];
    public int NextIndex { get; set; }
    public Guid? CurrentSegmentId { get; set; }
    public TimeSpan Elapsed { get; set; }
    public ModelOptions ModelOptions { get; set; } = new();
    public PromptPreset Preset { get; set; } = PromptPreset.Default;
    public string? LastError { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => Status is QueueStatus.Running or QueueStatus.PauseRequested or QueueStatus.Cancelling;
}

public sealed record QueueProgress(QueueStatus Status, int Total, int Translated, int Errors, int NeedsInspection,
    int Remaining, int? CurrentOrder, TimeSpan Elapsed, TimeSpan? EstimatedRemaining, string? Error);

public sealed record ConfirmedTerm(string Chinese, string Vietnamese);
