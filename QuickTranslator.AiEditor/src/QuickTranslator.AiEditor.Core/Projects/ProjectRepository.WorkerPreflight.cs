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
            if (session.RecoveryReport.RequiresDecision)
                throw new InvalidOperationException("Còn transaction cần phục hồi; worker preflight chưa thể ghi dữ liệu.");
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
                ProjectTranslationFingerprints.Protected(current) != expectedItem.ProtectedFingerprint ||
                expectedItem.PreflightCompletedAt is null &&
                ProjectTranslationFingerprints.Document(current) != expectedItem.EnqueuedDocumentFingerprint)
                throw new InvalidOperationException("Chapter đã đổi ngoài Queue trước worker preflight; không tự chia lại.");

            ResegmentationPreview preview = ResegmentationPlanner.Preview(current, segmentation,
                allowRecoverableCheckpoint: true);
            ChapterDocument candidate = ResegmentationPlanner.Apply(current, preview,
                confirmed: preview.ProtectedSegmentCount > 0, DateTimeOffset.UtcNow,
                allowRecoverableCheckpoint: true);
            ProjectTranslationJob proposed = ProjectTranslationJobStore.Clone(expectedJob);
            ProjectTranslationItem item = proposed.Items.Single(value => value.ChapterId == chapterId);
            item.SourceFingerprint = ProjectTranslationFingerprints.Source(candidate);
            item.ProtectedFingerprint = ProjectTranslationFingerprints.Protected(candidate);
            item.EnqueuedDocumentFingerprint = ProjectTranslationFingerprints.Document(candidate);
            item.TotalSegments = candidate.Segments.Count;
            item.CompletedSegments = candidate.Segments.Count(segment => segment.HasCompletedTranslation ||
                segment.Status == SegmentStatus.Approved);
            item.ErrorSegments = candidate.Segments.Count(segment => segment.Status == SegmentStatus.Error);
            item.SourceHanCharacters = candidate.Segments.Sum(segment => TextSegmenter.CountHanCharacters(segment.SourceText));
            item.AppliedSegmentationOptions = segmentation with { };
            item.PreflightCompletedAt = DateTimeOffset.UtcNow;
            item.ChapterQueueRunId = null;
            item.PreflightWarning = preview.Warnings.Count == 0 ? null : string.Join(" ", preview.Warnings);

            string chapterRelative = entry.RelativePath;
            byte[] previousChapter = await File.ReadAllBytesAsync(session.Resolve(chapterRelative), cancellationToken)
                .ConfigureAwait(false);
            CopySummary(entry, candidate);
            if (!preview.BoundariesChanged)
            {
                await session.ChapterStore.SaveSerializedAsync(candidate.Id,
                    ChapterFileStore.SerializeDocument(candidate), Hash(previousChapter), cancellationToken)
                    .ConfigureAwait(false);
                session.Manifest = after;
                return new(candidate, proposed, preview);
            }
            NormalizeManifest(after);
            var changes = new PlannedProjectFile[]
            {
                new(chapterRelative + ".bak", previousChapter),
                new(chapterRelative, ChapterFileStore.SerializeDocument(candidate))
            };
            await Transactions.ExecuteAsync(session, "WorkerSegmentationPreflight", after, changes,
                cancellationToken).ConfigureAwait(false);
            PublishManifestCommitted(session, "WorkerSegmentationPreflight");
            // The chapter and manifest are the structural transaction. Queue metadata is a derived
            // progress index and is checkpointed adaptively by the coordinator; after a crash the
            // worker preflight can be repeated safely from the committed chapter.
            return new(candidate, proposed, preview);
        }
        finally { session.OperationGate.Release(); }
    }
}
