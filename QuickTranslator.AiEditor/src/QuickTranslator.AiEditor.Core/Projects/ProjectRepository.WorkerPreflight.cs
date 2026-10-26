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

            // Retranslating from scratch, or moving an unfinished chapter to a whole-chapter worker: earlier AI
            // translations do not pin the old boundaries. Replaced segments remain available in segmentation history.
            // Human work cannot be merged safely into a single translated chapter, so fail closed rather than lose it.
            if (segmentation.WholeChapter && current.Segments.Count > 1 &&
                current.Segments.Any(ResegmentationPlanner.HasUserData))
                throw new InvalidOperationException(
                    "Chương chưa hoàn tất có bản sửa hoặc bản duyệt của người dùng trong các segment cũ. " +
                    "Gemini không tự gộp lại vì có thể làm mất bản sửa; hãy hoàn tất chương hoặc xử lý các bản sửa trước.");
            bool releaseAi = expectedItem.RetranslateAll || segmentation.WholeChapter;
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
