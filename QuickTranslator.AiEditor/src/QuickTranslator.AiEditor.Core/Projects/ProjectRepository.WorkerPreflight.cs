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

            // Retranslating from scratch: earlier AI translations do not pin the old boundaries, so the chapter takes this
            // worker's segment size. Only what the user wrote or approved keeps its boundaries. Old segments stay in history.
            bool releaseAi = expectedItem.RetranslateAll;
            ResegmentationPreview preview = ResegmentationPlanner.Preview(current, segmentation,
                allowRecoverableCheckpoint: true, releaseAiTranslations: releaseAi);
            ChapterDocument candidate = ResegmentationPlanner.Apply(current, preview,
                confirmed: preview.ProtectedSegmentCount > 0, DateTimeOffset.UtcNow,
                allowRecoverableCheckpoint: true, releaseAiTranslations: releaseAi);
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
}
