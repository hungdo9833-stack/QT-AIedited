using System.Text.Json.Serialization;

namespace QuickTranslator.AiEditor.Core.Models;

public enum SegmentStatus { Untranslated, Translating, AwaitingReview, Approved, Error, InspectionRequired }
public enum AttemptOutcome { InProgress, Completed, InvalidOutput, Failed, Cancelled, Interrupted }
public enum IssueSeverity { Warning, Error }
public enum IssueCode { EmptyOutput, TruncatedOutput, SuspiciousRepetition, PossibleMissingContent, UntranslatedChinese, IncompleteFinalSentence, SourceEndsMidSentence, AmbiguousPronoun }

public sealed record TranslationIssue(IssueCode Code, IssueSeverity Severity, string Message);

public sealed record GenerationMetrics
{
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
    public long? TotalDurationNanoseconds { get; init; }
    public long? LoadDurationNanoseconds { get; init; }
    public long? PromptEvaluationDurationNanoseconds { get; init; }
    public long? EvaluationDurationNanoseconds { get; init; }
    public TimeSpan ClientElapsed { get; init; }
}

public sealed class TranslationAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public AttemptOutcome Outcome { get; set; }
    public ModelOptions ModelOptions { get; set; } = new();
    public string PromptVersion { get; set; } = "";
    public string Prompt { get; set; } = "";
    public PromptPreset? PresetSnapshot { get; set; }
    public long? ProjectResourceRevision { get; set; }
    public string? SourceSha256 { get; set; }
    public Guid? NormalizationRunId { get; set; }
    public string? AiText { get; set; }
    public bool? ResponseDone { get; set; }
    public string? DoneReason { get; set; }
    public GenerationMetrics? Metrics { get; set; }
    public List<TranslationIssue> Issues { get; set; } = [];
    public string? Error { get; set; }
}

public sealed record ApprovalRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset ApprovedAt { get; init; }
    public int DraftRevision { get; init; }
    public string Text { get; init; } = "";
    public Guid? SourceAttemptId { get; init; }
}

public sealed class TranslationSegment
{
    public Guid Id { get; set; }
    public int Order { get; set; }
    public int StartUtf16 { get; set; }
    public string SourceText { get; set; } = "";
    public int HanCharacterCount { get; set; }
    public SegmentStatus Status { get; set; }
    public string? UserDraft { get; set; }
    public Guid? DraftSourceAttemptId { get; set; }
    public int DraftRevision { get; set; }
    public string? LastError { get; set; }
    public List<TranslationAttempt> Attempts { get; set; } = [];
    public List<ApprovalRecord> Approvals { get; set; } = [];
    public List<DraftSnapshot> DraftHistory { get; set; } = [];
    public string? PreservedText { get; set; }
    public bool RequiresRetranslation { get; set; }
    public bool ProtectedByPreviousEdit { get; set; }
    public string? NormalizedText { get; set; }
    public List<PronounNormalizationRun> NormalizationHistory { get; set; } = [];

    [JsonIgnore]
    public TranslationAttempt? LatestAiAttempt => Attempts.LastOrDefault(a => !string.IsNullOrWhiteSpace(a.AiText));
    [JsonIgnore]
    public string EditableText => UserDraft ?? NormalizedText ?? LatestAiAttempt?.AiText ?? PreservedText ?? "";
    [JsonIgnore]
    public bool IsProtectedFromAutomaticTranslation => UserDraft is not null || Approvals.Count != 0 || ProtectedByPreviousEdit;
    [JsonIgnore]
    public bool HasCompletedTranslation => !RequiresRetranslation && Attempts.Any(a =>
        a.Outcome == AttemptOutcome.Completed && !string.IsNullOrWhiteSpace(a.AiText));
}

public sealed record DraftSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset RecordedAt { get; init; }
    public int Revision { get; init; }
    public string Text { get; init; } = "";
    public Guid? SourceAttemptId { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class SegmentationChange
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset ChangedAt { get; set; }
    public string Description { get; set; } = "";
    public List<TranslationSegment> PreviousSegments { get; set; } = [];
    public List<Guid> ReplacementIds { get; set; } = [];
}

public sealed class ChapterDocument
{
    public int SchemaVersion { get; set; } = 3;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ProjectId { get; set; }
    public int? ChapterNumber { get; set; }
    public string Title { get; set; } = "";
    public string ChineseTitle { get; set; } = "";
    public string OriginalText { get; set; } = "";
    public long SourceRevision { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public SegmenterOptions SegmentationOptions { get; set; } = new();
    public ModelOptions DefaultModelOptions { get; set; } = new();
    public Guid? SelectedSegmentId { get; set; }
    public List<TranslationSegment> Segments { get; set; } = [];
    public PromptPreset SelectedPreset { get; set; } = PromptPreset.Default;
    public QueueCheckpoint Queue { get; set; } = new();
    public List<SegmentationChange> SegmentationHistory { get; set; } = [];
    public List<SourceEditRevision> SourceEditHistory { get; set; } = [];
    public List<ConfirmedTerm> ConfirmedTerms { get; set; } = [];
    public string ConfirmedContextNotes { get; set; } = "";
    public CharacterProject CharacterProject { get; set; } = new();
    public TextCleanerSettings TextCleaner { get; set; } = new();
    public List<BatchReplacementRun> BatchReplacementHistory { get; set; } = [];
}
