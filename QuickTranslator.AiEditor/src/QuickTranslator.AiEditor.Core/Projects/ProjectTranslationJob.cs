using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Projects;

public enum ProjectTranslationJobStatus
{
    Draft, Ready, Running, PauseRequested, Paused, Cancelling, Cancelled, Interrupted, Completed, CompletedWithErrors
}

public enum ProjectTranslationItemStatus
{
    Pending, Running, Paused, Interrupted, Completed, Failed, Skipped, NeedsReview, Cancelled
}

public enum ProjectTranslationItemDisposition
{
    None, AlreadyTranslated, TranslatedThisJob, ProtectedContent, MissingEligibility,
    Failed, NeedsReview, Cancelled
}

public enum ProjectTranslationWorkerStatus
{
    Ready, Running, Pausing, Paused, Offline, Interrupted, Completed, Cancelled
}

public sealed class ProjectTranslationJob
{
    public int SchemaVersion { get; set; } = 1;
    public Guid JobId { get; set; } = Guid.NewGuid();
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public ProjectTranslationJobStatus Status { get; set; } = ProjectTranslationJobStatus.Draft;
    public ModelOptions ModelOptions { get; set; } = new();
    public OllamaBackendSnapshot? Backend { get; set; }
    public PromptPreset PromptPreset { get; set; } = PromptPreset.Default;
    public long ProjectResourceRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid? CurrentChapterId { get; set; }
    public string? LastError { get; set; }
    public List<ProjectTranslationWorkerState> Workers { get; set; } = [];
    public List<ProjectTranslationItem> Items { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1 || JobId == Guid.Empty || ProjectId == Guid.Empty || Revision < 0 ||
            !Enum.IsDefined(Status) || ModelOptions is null || Backend is null || PromptPreset is null || ProjectResourceRevision < 0 ||
            CreatedAt == default || UpdatedAt == default || Items is null || Items.Count == 0 || Items.Count > 10_000 ||
            Items.Any(item => item is null) || Items.Select(item => item.ChapterId).Distinct().Count() != Items.Count)
            throw new InvalidDataException("Project translation job không hợp lệ.");
        ModelOptions.Validate();
        Backend.Validate();
        if (!string.Equals(Backend.Model, ModelOptions.Model, StringComparison.Ordinal))
            throw new InvalidDataException("Model trong backend snapshot không khớp Queue Plan.");
        PromptPreset.Validate();
        if (Workers is null || Workers.Count > 2 || Workers.Any(worker => worker is null) ||
            Workers.Select(worker => worker.WorkerId).Distinct().Count() != Workers.Count)
            throw new InvalidDataException("Danh sách worker của translation job không hợp lệ.");
        foreach (ProjectTranslationWorkerState worker in Workers) worker.Validate();
        foreach (ProjectTranslationItem item in Items) item.Validate();
        if (Workers.SelectMany(worker => worker.RunningChapterIds).Distinct().Count() !=
            Workers.Sum(worker => worker.RunningChapterIds.Count) ||
            Workers.Any(worker => worker.RunningChapterIds.Any(chapterId =>
                Items.All(item => item.ChapterId != chapterId || item.AssignedWorkerId != worker.WorkerId))))
            throw new InvalidDataException("Danh sách chapter đang chạy của worker không hợp lệ.");
        if (Items.Any(item => item.AssignedWorkerId is Guid workerId && Workers.All(worker => worker.WorkerId != workerId)))
            throw new InvalidDataException("Item tham chiếu worker không tồn tại.");
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
    public SegmenterOptions? AppliedSegmentationOptions { get; set; }
    public DateTimeOffset? PreflightCompletedAt { get; set; }
    public string? PreflightWarning { get; set; }
    public Guid? ChapterQueueRunId { get; set; }
    public Guid? AssignedWorkerId { get; set; }
    public int SourceHanCharacters { get; set; }
    public int TotalSegments { get; set; }
    public int CompletedSegments { get; set; }
    public int ErrorSegments { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Error { get; set; }
    public bool WorkerMetricsRecorded { get; set; }
    public ProjectTranslationItemDisposition Disposition { get; set; }
    public List<TranslationEligibilityDiagnostic> EligibilityDiagnostics { get; set; } = [];

    internal void Validate()
    {
        if (ChapterId == Guid.Empty || Number <= 0 || Order <= 0 || string.IsNullOrWhiteSpace(Title) ||
            !Enum.IsDefined(Status) || SourceFingerprint.Length != 64 || ProtectedFingerprint.Length != 64 ||
            PreflightWarning?.Length > 4000 ||
            !Enum.IsDefined(Disposition) || EligibilityDiagnostics is null || EligibilityDiagnostics.Count > 8 ||
            TotalSegments < 0 || CompletedSegments < 0 ||
            CompletedSegments > TotalSegments || ErrorSegments < 0 || ErrorSegments > TotalSegments || SourceHanCharacters < 0)
            throw new InvalidDataException("Item trong Project translation job không hợp lệ.");
        foreach (TranslationEligibilityDiagnostic diagnostic in EligibilityDiagnostics)
            diagnostic.Validate(ChapterId);
        AppliedSegmentationOptions?.Validate();
    }

    internal void RecordEligibilityDiagnostic(TranslationEligibilityDiagnostic diagnostic)
    {
        diagnostic.Validate(ChapterId);
        EligibilityDiagnostics.RemoveAll(existing => existing.Stage == diagnostic.Stage);
        EligibilityDiagnostics.Add(diagnostic);
        if (EligibilityDiagnostics.Count > 8)
            EligibilityDiagnostics.RemoveRange(0, EligibilityDiagnostics.Count - 8);
    }
}

public sealed class ProjectTranslationWorkerState
{
    public Guid WorkerId { get; set; }
    public Guid ProfileId { get; set; }
    public string Name { get; set; } = "";
    public OllamaBackendSnapshot Backend { get; set; } = OllamaBackendSnapshot.LocalDefault("translategemma:12b");
    public ModelOptions ModelOptions { get; set; } = new();
    public SegmenterOptions SegmentationOptions { get; set; } = new();
    public string BackendIdentity { get; set; } = "";
    public ProjectTranslationWorkerStatus Status { get; set; } = ProjectTranslationWorkerStatus.Ready;
    public Guid? CurrentChapterId { get; set; }
    public List<Guid> RunningChapterIds { get; set; } = [];
    public int Slots { get; set; } = 1;
    public int CompletedChapters { get; set; }
    public int FailedChapters { get; set; }
    public long CompletedHanCharacters { get; set; }
    public double ActiveSeconds { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public double EvaluationSeconds { get; set; }
    public int ErrorSegments { get; set; }
    public int TruncatedOutputCount { get; set; }
    public int PossibleMissingContentCount { get; set; }
    public int TimeoutCount { get; set; }
    public decimal? HourlyPrice { get; set; }
    public string? LastError { get; set; }

    internal void Validate()
    {
        if (WorkerId == Guid.Empty || ProfileId == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 100 ||
            Backend is null || ModelOptions is null || SegmentationOptions is null ||
            BackendIdentity.Length > 256 || BackendIdentity.Any(char.IsControl) ||
            !Enum.IsDefined(Status) || RunningChapterIds is null || Slots is < 1 or > 8 ||
            RunningChapterIds.Count > Slots ||
            RunningChapterIds.Any(id => id == Guid.Empty) || RunningChapterIds.Distinct().Count() != RunningChapterIds.Count ||
            RunningChapterIds.Count > 0 && CurrentChapterId is Guid current && !RunningChapterIds.Contains(current) ||
            CompletedChapters < 0 ||
            FailedChapters < 0 || CompletedHanCharacters < 0 || ActiveSeconds < 0 || double.IsNaN(ActiveSeconds) ||
            double.IsInfinity(ActiveSeconds) || InputTokens < 0 || OutputTokens < 0 || EvaluationSeconds < 0 ||
            double.IsNaN(EvaluationSeconds) || double.IsInfinity(EvaluationSeconds) || ErrorSegments < 0 ||
            TruncatedOutputCount < 0 || PossibleMissingContentCount < 0 || TimeoutCount < 0 ||
            HourlyPrice is < 0 or > 1_000_000)
            throw new InvalidDataException("Worker trong translation job không hợp lệ.");
        Backend.Validate();
        ModelOptions.Validate();
        SegmentationOptions.Validate();
        if (ProfileId != Backend.ProfileId || !string.Equals(Backend.Model, ModelOptions.Model, StringComparison.Ordinal))
            throw new InvalidDataException("Snapshot worker không khớp profile/model.");
    }
}

public sealed record OllamaBackendSnapshot(Guid ProfileId, OllamaBackendKind Kind, string ProfileName,
    string Endpoint, string Model)
{
    public static OllamaBackendSnapshot LocalDefault(string model) => new(OllamaBackendProfile.DefaultLocalId,
        OllamaBackendKind.Local, "Ollama Local", OllamaClient.DefaultEndpoint.ToString(), model);

    public void Validate()
    {
        if (ProfileId == Guid.Empty || !Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(ProfileName) ||
            ProfileName.Length > 100 || string.IsNullOrWhiteSpace(Model) || Model.Length > 200 ||
            !Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint))
            throw new InvalidDataException("Backend snapshot của translation queue không hợp lệ.");
        _ = Kind == OllamaBackendKind.Local
            ? OllamaClient.ValidateLocalEndpoint(endpoint)
            : OllamaClient.ValidateRemoteEndpoint(endpoint);
    }
}

public sealed record ProjectTranslationEnvironment(ModelOptions ModelOptions, PromptPreset PromptPreset,
    ProjectResources Resources, OllamaBackendSnapshot? Backend = null,
    BookIntelligence.IBookKnowledgeContextProvider? BookKnowledgeContext = null);

public sealed record ProjectTranslationProgress(ProjectTranslationJobStatus Status, int TotalChapters,
    int CompletedChapters, int FailedChapters, int SkippedChapters, int PendingChapters,
    Guid? CurrentChapterId, string? CurrentChapterTitle, int CurrentCompletedSegments,
    int CurrentTotalSegments, string? Error);

public sealed record ProjectTranslationWorkerDefinition(Guid WorkerId, OllamaBackendSnapshot Backend,
    IOllamaClient Client, ModelOptions ModelOptions, SegmenterOptions SegmentationOptions,
    decimal? HourlyPrice = null, string BackendIdentity = "", int Slots = 1)
{
    public ProjectTranslationWorkerDefinition(Guid workerId, OllamaBackendSnapshot backend,
        IOllamaClient client, ModelOptions modelOptions, decimal? hourlyPrice = null, string backendIdentity = "",
        int slots = 1)
        : this(workerId, backend, client, modelOptions, new SegmenterOptions(), hourlyPrice, backendIdentity, slots) { }

    public void Validate()
    {
        if (WorkerId == Guid.Empty || Backend is null || Client is null || ModelOptions is null || SegmentationOptions is null ||
            HourlyPrice is < 0 or > 1_000_000 || BackendIdentity.Length > 256 || BackendIdentity.Any(char.IsControl) ||
            Slots is < 1 or > 8)
            throw new InvalidDataException("Cấu hình worker translation không hợp lệ.");
        Backend.Validate(); ModelOptions.Validate(); SegmentationOptions.Validate();
        if (!string.Equals(Backend.Model, ModelOptions.Model, StringComparison.Ordinal))
            throw new InvalidDataException("Model của worker không khớp backend snapshot.");
    }
}

public sealed record ProjectTranslationWorkerProgress(Guid WorkerId, string Name,
    ProjectTranslationWorkerStatus Status, Guid? CurrentChapterId, string? CurrentChapterTitle,
    int CompletedSegments, int TotalSegments, double HanCharactersPerMinute,
    int CompletedChapters, int FailedChapters, decimal EstimatedCost, string? Error,
    double TokensPerSecond = 0, long InputTokens = 0, long OutputTokens = 0,
    int ErrorSegments = 0, int TruncatedOutputCount = 0,
    int PossibleMissingContentCount = 0, int TimeoutCount = 0);

public sealed record ProjectTranslationAggregateMetrics(double HanCharactersPerMinute,
    TimeSpan? EstimatedRemaining, decimal EstimatedCost);

public sealed record ProjectTranslationViewSnapshot(ProjectTranslationJob Job,
    ProjectTranslationProgress Progress, IReadOnlyList<ProjectTranslationWorkerProgress> Workers,
    ProjectTranslationAggregateMetrics Aggregate);
