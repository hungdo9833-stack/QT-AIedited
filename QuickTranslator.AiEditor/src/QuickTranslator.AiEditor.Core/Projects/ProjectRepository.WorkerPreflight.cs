using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Projects;

internal sealed record WorkerPreflightResult(ChapterDocument Chapter, ProjectTranslationJob Job,
    ResegmentationPreview Preview);

public sealed partial class ProjectRepository
{
    internal async Task<WorkerPreflightResult> PreflightWorkerChapterAsync(ProjectSession session,
        ProjectTranslationJob expectedJob, Guid workerId, Guid chapterId, SegmenterOptions segmentation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(expectedJob);
        ArgumentNullException.ThrowIfNull(segmentation);
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectManifest after = Clone(session.Manifest);
            ProjectChapterEntry entry = after.Chapters.SingleOrDefault(item =>
                item.ChapterId == chapterId && item.DeletedAt is null)
                ?? throw new FileNotFoundException("Chapter worker vừa claim không còn active trong Project.");
            ProjectTranslationItem expectedItem = expectedJob.Items.SingleOrDefault(item => item.ChapterId == chapterId)
                ?? throw new InvalidDataException("Queue job không chứa chapter worker vừa claim.");
            if (expectedItem.Status != ProjectTranslationItemStatus.Running || expectedItem.AssignedWorkerId != workerId)
                throw new InvalidOperationException("Worker không còn quyền preflight chapter này.");

            ChapterDocument current = await ReadRegisteredChapterAsync(session, entry, cancellationToken).ConfigureAwait(false);
            if (ProjectTranslationFingerprints.Source(current) != expectedItem.SourceFingerprint ||
                ProjectTranslationFingerprints.Protected(current) != expectedItem.ProtectedFingerprint)
                throw new InvalidOperationException("Chapter đã đổi ngoài Queue trước worker preflight; không tự chia lại.");
            // This worker owns the chapter and has not started its run yet, so a Running checkpoint on disk is
            // left over from a stopped process; without this, resegmentation refuses the chapter forever.
            TranslationService.RecoverInterruptedState(current, DateTimeOffset.UtcNow);

            // Retranslating from scratch, or moving an unfinished chapter to a whole-chapter worker: earlier AI
            // translations do not pin the old boundaries. Replaced segments remain available in segmentation history.
            // Segments the user drafted, edited or approved keep their boundaries and text; a whole-chapter worker then
            // sends each stretch between them as one request instead of the whole chapter. When the user confirmed
            // retranslating those too, they are released first and the chapter as it was goes into segmentation history.
            ChapterDocument? beforeRelease = null;
            if (expectedItem.RetranslateAll && expectedItem.OverrideUserEdits &&
                current.Segments.Any(ResegmentationPlanner.HasUserData))
            {
                beforeRelease = DocumentJson.Clone(current);
                foreach (TranslationSegment segment in current.Segments) ReleaseUserData(segment);
            }
            bool releaseAi = expectedItem.RetranslateAll || segmentation.WholeChapter;
            ResegmentationPreview preview = ResegmentationPlanner.Preview(current, segmentation,
                allowRecoverableCheckpoint: true, releaseAiTranslations: releaseAi);
            ChapterDocument candidate = ResegmentationPlanner.Apply(current, preview,
                confirmed: preview.ProtectedSegmentCount > 0, DateTimeOffset.UtcNow,
                allowRecoverableCheckpoint: true, releaseAiTranslations: releaseAi);
            if (beforeRelease is not null)
            {
                const string description = "Dịch lại cả đoạn đã sửa/duyệt";
                if (preview.BoundariesChanged)
                {
                    SegmentationChange last =candidate.SegmentationHistory[^1];
                    last.PreviousSegments = beforeRelease.Segments;
                    last.Description = description + ": " + last.Description;
                }
                else
                    candidate.SegmentationHistory.Add(new()
                    {
                        ChangedAt = DateTimeOffset.UtcNow,
                        Description = description,
                        PreviousSegments = beforeRelease.Segments,
                        ReplacementIds = candidate.Segments.Select(segment => segment.Id).ToList(),
                        PreviousQueue = beforeRelease.Queue
                    });
                DocumentJson.Validate(candidate);
            }
            ProjectTranslationJob proposed = ProjectTranslationJobStore.Clone(expectedJob);
            ProjectTranslationItem item = proposed.Items.Single(value => value.ChapterId == chapterId);
            item.SourceFingerprint = ProjectTranslationFingerprints.Source(candidate);
            item.ProtectedFingerprint = ProjectTranslationFingerprints.Protected(candidate);
            item.TotalSegments = candidate.Segments.Count;
            item.CompletedSegments = ProjectTranslationCoordinator.CompletedSegmentsFor(candidate, item);
            item.ErrorSegments = candidate.Segments.Count(segment => segment.Status == SegmentStatus.Error);
            item.SourceHanCharacters = candidate.Segments.Sum(segment => TextSegmenter.CountHanCharacters(segment.SourceText));
            item.AppliedSegmentationOptions = segmentation with { };
            item.PreflightCompletedAt = DateTimeOffset.UtcNow;
            item.ChapterQueueRunId = null;
            item.PreflightWarning = preview.Warnings.Count == 0 ? null : string.Join(" ", preview.Warnings);
            item.RecordEligibilityDiagnostic(TranslationEligibilityDiagnostics.Analyze(candidate,
                QueueMode.MissingSegments, TranslationEligibilityDiagnosticStage.AfterWorkerPreflight,
                preview.BoundariesChanged));

            CopySummary(entry, candidate);
            await session.ChapterStore.SaveSerializedAsync(candidate.Id,
                ChapterFileStore.SerializeDocument(candidate), cancellationToken).ConfigureAwait(false);
            FaultPoint?.Invoke("AfterFastChapterWrite");
            NormalizeManifest(after);
            await WriteFastManifestAsync(session.ManifestPath, after, cancellationToken).ConfigureAwait(false);
            session.Manifest = after;
            PublishManifestCommitted(session, "WorkerSegmentationPreflight");
            return new(candidate, proposed, preview);
        }
        finally { session.OperationGate.Release(); }
    }

    /// <summary>Makes a segment the user drafted, edited or approved eligible for automatic translation again.</summary>
    private static void ReleaseUserData(TranslationSegment segment)
    {
        segment.UserDraft = null;
        segment.DraftSourceAttemptId = null;
        segment.DraftRevision = 0;
        segment.Approvals = [];
        segment.DraftHistory = [];
        segment.PreservedText = null;
        segment.ProtectedByPreviousEdit = false;
        segment.NormalizedText = null;
        segment.NormalizationHistory = [];
        if (segment.Status == SegmentStatus.Approved)
            segment.Status = segment.HasCompletedTranslation ? SegmentStatus.AwaitingReview : SegmentStatus.Untranslated;
    }
}
