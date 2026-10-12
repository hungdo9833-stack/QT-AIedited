namespace QuickTranslator.AiEditor.Core.Models;

public enum ReplacementRuleSource
{
    Manual,
    ConfirmedGlossary,
    CharacterProfile,
    TraditionalNames,
    TraditionalHanViet
}

public enum ChineseTextClassification
{
    ConfirmedCharacter,
    ConfirmedGlossary,
    Unknown,
    Conflict,
    Ignored
}

public enum BatchReplacementScope
{
    SelectedSegment,
    CurrentChapter,
    CurrentProject
}

public sealed class TextCleanerSettings
{
    public bool AutoApplyConfirmedMappings { get; set; }
    public List<string> IgnoredChineseText { get; set; } = [];
}

public sealed record BatchReplacementRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Find { get; init; } = "";
    public string Replacement { get; init; } = "";
    public ReplacementRuleSource Source { get; init; }
    public bool Confirmed { get; init; }
    public string SourceLabel { get; init; } = "";
}

public sealed record ChineseMappingCandidate(
    string Chinese, string Vietnamese, ReplacementRuleSource Source, bool Confirmed, bool Conflict,
    string SourceLabel);

public sealed record ChineseTextOccurrence(
    Guid Id, Guid ChapterId, string ChapterTitle, Guid SegmentId, int SegmentOrder,
    int StartUtf16, int LengthUtf16, string Text, string Sentence,
    ChineseTextClassification Classification, IReadOnlyList<ChineseMappingCandidate> Candidates,
    bool Protected);

public sealed record ChineseTextGroup(
    string Text, ChineseTextClassification Classification, int Count,
    IReadOnlyList<ChineseMappingCandidate> Candidates, IReadOnlyList<ChineseTextOccurrence> Occurrences);

public sealed record ChineseTextScanReport(
    Guid CharacterProjectId, DateTimeOffset CreatedAt,
    IReadOnlyList<ChineseTextOccurrence> Occurrences, IReadOnlyList<ChineseTextGroup> Groups)
{
    public int TotalOccurrences => Occurrences.Count;
    public int UnknownOccurrences => Occurrences.Count(x => x.Classification == ChineseTextClassification.Unknown);
    public int ConflictOccurrences => Occurrences.Count(x => x.Classification == ChineseTextClassification.Conflict);
}

public sealed record BatchReplacementPreviewItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ChapterId { get; init; }
    public Guid CharacterProjectId { get; init; }
    public string ChapterTitle { get; init; } = "";
    public Guid SegmentId { get; init; }
    public int SegmentOrder { get; init; }
    public int StartUtf16 { get; init; }
    public int LengthUtf16 { get; init; }
    public string Original { get; init; } = "";
    public string? Replacement { get; init; }
    public ReplacementRuleSource Source { get; init; }
    public bool RuleConfirmed { get; init; }
    public string SourceLabel { get; init; } = "";
    public string BeforeSentence { get; init; } = "";
    public string AfterSentence { get; init; } = "";
    public string AnalysedText { get; init; } = "";
    public int DraftRevision { get; init; }
    public int ApprovalVersion { get; init; }
    public Guid? LastApprovalId { get; init; }
    public SegmentStatus SegmentStatus { get; init; }
    public bool Protected { get; init; }
    public bool Conflict { get; init; }
    public string ConflictMessage { get; init; } = "";
}

public sealed record BatchReplacementChange
{
    public int StartUtf16 { get; init; }
    public int LengthUtf16 { get; init; }
    public string Original { get; init; } = "";
    public string Replacement { get; init; } = "";
    public ReplacementRuleSource Source { get; init; }
    public string SourceLabel { get; init; } = "";
}

public sealed class BatchReplacementSegmentRevision
{
    public Guid SegmentId { get; set; }
    public int SegmentOrder { get; set; }
    public string BeforeText { get; set; } = "";
    public string AfterText { get; set; } = "";
    public string? PreviousUserDraft { get; set; }
    public string? PreviousNormalizedText { get; set; }
    public Guid? PreviousDraftSourceAttemptId { get; set; }
    public int PreviousDraftRevision { get; set; }
    public SegmentStatus PreviousStatus { get; set; }
    public string? PreviousLastError { get; set; }
    public int AppliedDraftRevision { get; set; }
    // Null marks legacy runs whose approval state cannot be verified safely.
    public int? AppliedApprovalVersion { get; set; }
    public Guid? AppliedLastApprovalId { get; set; }
    public List<BatchReplacementChange> Changes { get; set; } = [];
}

public sealed class BatchReplacementRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedAt { get; set; }
    public BatchReplacementScope Scope { get; set; }
    public bool Automatic { get; set; }
    public bool Reverted { get; set; }
    public string Description { get; set; } = "";
    public List<BatchReplacementSegmentRevision> Segments { get; set; } = [];
}

public sealed record BatchApplyResult(
    Guid BatchId, Guid ChapterId, string ChapterTitle, int SegmentsChanged, int OccurrencesChanged);

public sealed record BatchProjectChapterResult(
    Guid ChapterId, string ChapterTitle, bool Applied, int OccurrencesChanged, string? Error);

public sealed record BatchProjectApplyReport(
    Guid BatchId, string? BackupDirectory, IReadOnlyList<BatchProjectChapterResult> Chapters)
{
    public int AppliedChapters => Chapters.Count(x => x.Applied);
    public int FailedChapters => Chapters.Count(x => !x.Applied);
}
