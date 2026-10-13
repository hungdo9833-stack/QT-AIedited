using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Projects;

public enum ProjectTranslationJobStatus
{
    Draft, Ready, Running, PauseRequested, Paused, Cancelling, Cancelled, Interrupted, Completed, CompletedWithErrors
}

public enum ProjectTranslationItemStatus
{
    Pending, Running, Paused, Interrupted, Completed, Failed, Skipped, NeedsReview, Cancelled
}

public sealed class ProjectTranslationJob
{
    public int SchemaVersion { get; set; } = 1;
    public Guid JobId { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public ProjectTranslationJobStatus Status { get; set; } = ProjectTranslationJobStatus.Draft;
    public ModelOptions ModelOptions { get; set; } = new();
    public PromptPreset PromptPreset { get; set; } = PromptPreset.Default;
    public long ProjectResourceRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CurrentChapterId { get; set; }
    public string? LastError { get; set; }
    public List<ProjectTranslationItem> Items { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1 || JobId == Guid.Empty || ProjectId == Guid.Empty || Revision < 0 ||
            !Enum.IsDefined(Status) || ModelOptions is null || PromptPreset is null || ProjectResourceRevision < 0 ||
            CreatedAt == default || UpdatedAt == default || Items is null || Items.Count == 0 || Items.Count > 10_000 ||
            Items.Any(item => item is null) || Items.Select(item => item.ChapterId).Distinct().Count() != Items.Count)
            throw new InvalidDataException("Project translation job không hợp lệ.");
        ModelOptions.Validate();
        PromptPreset.Validate();
        foreach (ProjectTranslationItem item in Items) item.Validate();
        if (CurrentChapterId is Guid current && !Items.Any(item => item.ChapterId == current))
            throw new InvalidDataException("Job tham chiếu chapter hiện tại không tồn tại.");
    }
}

public sealed class ProjectTranslationItem
{
    public Guid ChapterId { get; set; }
    public int Number { get; set; }
    public int Order { get; set; }
    public string Title { get; set; } = "";
    public ProjectTranslationItemStatus Status { get; set; } = ProjectTranslationItemStatus.Pending;
    public string SourceFingerprint { get; set; } = "";
    public string ProtectedFingerprint { get; set; } = "";
    public string EnqueuedDocumentFingerprint { get; set; } = "";
    public Guid? ChapterQueueRunId { get; set; }
    public int TotalSegments { get; set; }
    public int CompletedSegments { get; set; }
    public int ErrorSegments { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }

    internal void Validate()
    {
        if (ChapterId == Guid.Empty || Number <= 0 || Order <= 0 || string.IsNullOrWhiteSpace(Title) ||
            !Enum.IsDefined(Status) || SourceFingerprint.Length != 64 || ProtectedFingerprint.Length != 64 ||
            EnqueuedDocumentFingerprint.Length != 64 || TotalSegments < 0 || CompletedSegments < 0 ||
            CompletedSegments > TotalSegments || ErrorSegments < 0 || ErrorSegments > TotalSegments)
            throw new InvalidDataException("Item trong Project translation job không hợp lệ.");
    }
}

public sealed record ProjectTranslationEnvironment(ModelOptions ModelOptions, PromptPreset PromptPreset,
    ProjectResources Resources);

public sealed record ProjectTranslationProgress(ProjectTranslationJobStatus Status, int TotalChapters,
    int CompletedChapters, int FailedChapters, int SkippedChapters, int PendingChapters,
    Guid? CurrentChapterId, string? CurrentChapterTitle, int CurrentCompletedSegments,
    int CurrentTotalSegments, string? Error);
