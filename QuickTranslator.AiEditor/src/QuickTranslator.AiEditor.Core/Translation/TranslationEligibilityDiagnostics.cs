using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum TranslationEligibilityDiagnosticStage
{
    AfterImport,
    AfterPersistedReload,
    Plan,
    AfterWorkerPreflight,
    BeforeEligibilityDecision
}

public enum QueueEligibilityReason
{
    EligibleNeverAttempted,
    EligibleRequiresRetranslation,
    EligibleCancelledAttempt,
    EligibleInterruptedAttempt,
    EligibleFailedRetry,
    ProtectedUserDraft,
    ProtectedApproval,
    ProtectedPreviousEdit,
    CompletedTranslation,
    FailedAttemptRequiresExplicitRetry,
    InvalidOutputRequiresExplicitRetry,
    InProgressAttempt,
    CompletedAttemptWithoutOutput,
    QueueModeExcludesSegment
}

public sealed record TranslationEligibilityDiagnostic
{
    public TranslationEligibilityDiagnosticStage Stage { get; init; }
    public Guid ChapterId { get; init; }
    public int? ChapterNumber { get; init; }
    public QueueMode QueueMode { get; init; }
    public bool WorkerPreflightChangedSegmentation { get; init; }
    public int SegmentCount { get; init; }
    public int EligibleSegmentCount { get; init; }
    public int UntranslatedSegmentCount { get; init; }
    public int CompletedTranslationCount { get; init; }
    public int ProtectedByUserDraftCount { get; init; }
    public int ProtectedByApprovalCount { get; init; }
    public int ProtectedByPreviousEditCount { get; init; }
    public int ExistingAttemptCount { get; init; }
    public int RequiresRetranslationCount { get; init; }
    public int InProgressAttemptCount { get; init; }
    public int CompletedAttemptCount { get; init; }
    public int InvalidOutputAttemptCount { get; init; }
    public int FailedAttemptCount { get; init; }
    public int CancelledAttemptCount { get; init; }
    public int InterruptedAttemptCount { get; init; }
    public Dictionary<QueueEligibilityReason, int> ReasonCounts { get; init; } = [];

    internal void Validate(Guid expectedChapterId)
    {
        int[] counts = [SegmentCount, EligibleSegmentCount, UntranslatedSegmentCount,
            CompletedTranslationCount, ProtectedByUserDraftCount, ProtectedByApprovalCount,
            ProtectedByPreviousEditCount, ExistingAttemptCount, RequiresRetranslationCount,
            InProgressAttemptCount, CompletedAttemptCount, InvalidOutputAttemptCount,
            FailedAttemptCount, CancelledAttemptCount, InterruptedAttemptCount];
        if (ChapterId != expectedChapterId || ChapterId == Guid.Empty || ChapterNumber is <= 0 ||
            !Enum.IsDefined(Stage) || !Enum.IsDefined(QueueMode) || counts.Any(count => count < 0) ||
            EligibleSegmentCount > SegmentCount || UntranslatedSegmentCount > SegmentCount ||
            CompletedTranslationCount > SegmentCount || ReasonCounts is null ||
            ReasonCounts.Any(pair => !Enum.IsDefined(pair.Key) || pair.Value <= 0) ||
            ReasonCounts.Values.Sum() != SegmentCount)
            throw new InvalidDataException("Diagnostic eligibility của chapter không hợp lệ.");
    }
}

public static class TranslationEligibilityDiagnostics
{
    public static TranslationEligibilityDiagnostic Analyze(ChapterDocument chapter, QueueMode mode,
        TranslationEligibilityDiagnosticStage stage, bool workerPreflightChangedSegmentation = false)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var reasons = chapter.Segments.GroupBy(segment => Reason(segment, mode))
            .ToDictionary(group => group.Key, group => group.Count());
        var attempts = chapter.Segments.SelectMany(segment => segment.Attempts).ToArray();
        var result = new TranslationEligibilityDiagnostic
        {
            Stage = stage,
            ChapterId = chapter.Id,
            ChapterNumber = chapter.ChapterNumber,
            QueueMode = mode,
            WorkerPreflightChangedSegmentation = workerPreflightChangedSegmentation,
            SegmentCount = chapter.Segments.Count,
            EligibleSegmentCount = chapter.Segments.Count(segment => TranslationService.IsQueueEligible(segment, mode)),
            UntranslatedSegmentCount = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Untranslated),
            CompletedTranslationCount = chapter.Segments.Count(segment => segment.HasCompletedTranslation),
            ProtectedByUserDraftCount = chapter.Segments.Count(segment => segment.UserDraft is not null),
            ProtectedByApprovalCount = chapter.Segments.Count(segment => segment.Approvals.Count > 0 ||
                segment.Status == SegmentStatus.Approved),
            ProtectedByPreviousEditCount = chapter.Segments.Count(segment => segment.ProtectedByPreviousEdit),
            ExistingAttemptCount = attempts.Length,
            RequiresRetranslationCount = chapter.Segments.Count(segment => segment.RequiresRetranslation),
            InProgressAttemptCount = attempts.Count(attempt => attempt.Outcome == AttemptOutcome.InProgress),
            CompletedAttemptCount = attempts.Count(attempt => attempt.Outcome == AttemptOutcome.Completed),
            InvalidOutputAttemptCount = attempts.Count(attempt => attempt.Outcome == AttemptOutcome.InvalidOutput),
            FailedAttemptCount = attempts.Count(attempt => attempt.Outcome == AttemptOutcome.Failed),
            CancelledAttemptCount = attempts.Count(attempt => attempt.Outcome == AttemptOutcome.Cancelled),
            InterruptedAttemptCount = attempts.Count(attempt => attempt.Outcome == AttemptOutcome.Interrupted),
            ReasonCounts = reasons
        };
        result.Validate(chapter.Id);
        return result;
    }

    public static QueueEligibilityReason Reason(TranslationSegment segment, QueueMode mode)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.UserDraft is not null) return QueueEligibilityReason.ProtectedUserDraft;
        if (segment.Approvals.Count > 0 || segment.Status == SegmentStatus.Approved)
            return QueueEligibilityReason.ProtectedApproval;
        if (segment.ProtectedByPreviousEdit) return QueueEligibilityReason.ProtectedPreviousEdit;
        if (mode == QueueMode.FailedSegments)
            return segment.Status == SegmentStatus.Error
                ? QueueEligibilityReason.EligibleFailedRetry
                : QueueEligibilityReason.QueueModeExcludesSegment;
        if (segment.HasCompletedTranslation) return QueueEligibilityReason.CompletedTranslation;
        if (segment.RequiresRetranslation) return QueueEligibilityReason.EligibleRequiresRetranslation;
        if (segment.Attempts.Count == 0) return QueueEligibilityReason.EligibleNeverAttempted;
        return segment.Attempts[^1].Outcome switch
        {
            AttemptOutcome.Cancelled => QueueEligibilityReason.EligibleCancelledAttempt,
            AttemptOutcome.Interrupted => QueueEligibilityReason.EligibleInterruptedAttempt,
            AttemptOutcome.Failed => QueueEligibilityReason.FailedAttemptRequiresExplicitRetry,
            AttemptOutcome.InvalidOutput => QueueEligibilityReason.InvalidOutputRequiresExplicitRetry,
            AttemptOutcome.InProgress => QueueEligibilityReason.InProgressAttempt,
            AttemptOutcome.Completed => QueueEligibilityReason.CompletedAttemptWithoutOutput,
            _ => QueueEligibilityReason.QueueModeExcludesSegment
        };
    }
}
