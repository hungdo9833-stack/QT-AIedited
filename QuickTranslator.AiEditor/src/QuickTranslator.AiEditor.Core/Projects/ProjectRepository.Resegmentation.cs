using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed partial class ProjectRepository
{
    public async Task<ProjectResegmentationPreview> PreviewResegmentationAsync(ProjectSession session,
        IReadOnlyCollection<Guid> chapterIds, SegmenterOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(chapterIds);
        ArgumentNullException.ThrowIfNull(options);
        Guid[] ids = ValidateResegmentationIds(chapterIds);
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<ProjectResegmentationItem>(ids.Length);
            foreach (Guid id in ids)
            {
                ProjectChapterEntry entry = session.Manifest.Chapters.SingleOrDefault(chapter =>
                    chapter.ChapterId == id && chapter.DeletedAt is null)
                    ?? throw new FileNotFoundException($"Không tìm thấy chương hoạt động {id:D}.");
                ChapterDocument chapter = await ReadRegisteredChapterAsync(session, entry, cancellationToken).ConfigureAwait(false);
                items.Add(new(id, chapter.Title, ResegmentationPlanner.Preview(chapter, options)));
            }
            return new(items.AsReadOnly());
        }
        finally { session.OperationGate.Release(); }
    }

    public async Task ResegmentChaptersAsync(ProjectSession session, ProjectResegmentationPreview preview,
        bool confirmed, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(preview);
        Guid[] ids = ValidateResegmentationIds(preview.Chapters.Select(item => item.ChapterId).ToArray());
        if (ids.Length != preview.Chapters.Count)
            throw new InvalidDataException("Preview chia lại đoạn chứa ChapterId trùng.");
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.RecoveryReport.RequiresDecision)
                throw new InvalidOperationException("Còn transaction cần phục hồi; chưa thể chia lại đoạn.");
            ProjectManifest after = Clone(session.Manifest);
            var changes = new List<PlannedProjectFile>(ids.Length * 2);
            foreach (ProjectResegmentationItem item in preview.Chapters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectChapterEntry entry = after.Chapters.SingleOrDefault(chapter =>
                    chapter.ChapterId == item.ChapterId && chapter.DeletedAt is null)
                    ?? throw new FileNotFoundException($"Không tìm thấy chương hoạt động {item.ChapterId:D}.");
                ChapterDocument current = await ReadRegisteredChapterAsync(session, entry, cancellationToken).ConfigureAwait(false);
                ChapterDocument candidate = ResegmentationPlanner.Apply(current, item.Preview, confirmed, DateTimeOffset.UtcNow);
                string relative = entry.RelativePath;
                byte[] previous = await File.ReadAllBytesAsync(session.Resolve(relative), cancellationToken).ConfigureAwait(false);
                changes.Add(new(relative + ".bak", previous));
                changes.Add(new(relative, ChapterFileStore.SerializeDocument(candidate)));
                CopySummary(entry, candidate);
            }
            NormalizeManifest(after);
            await Transactions.ExecuteAsync(session, "ResegmentChapters", after, changes, cancellationToken).ConfigureAwait(false);
            PublishManifestCommitted(session, "ResegmentChapters");
        }
        finally { session.OperationGate.Release(); }
    }

    private static Guid[] ValidateResegmentationIds(IReadOnlyCollection<Guid> chapterIds)
    {
        Guid[] ids = chapterIds.Distinct().ToArray();
        if (ids.Length == 0 || ids.Length > 10_000 || ids.Any(id => id == Guid.Empty))
            throw new ArgumentException("Danh sách chương chia lại phải có từ 1 đến 10.000 ChapterId hợp lệ.", nameof(chapterIds));
        return ids;
    }

    private static async Task<ChapterDocument> ReadRegisteredChapterAsync(ProjectSession session,
        ProjectChapterEntry entry, CancellationToken cancellationToken)
    {
        ChapterDocument chapter = await ChapterFileStore.ReadVerifiedFileAsync(
            session.Resolve(entry.RelativePath), cancellationToken).ConfigureAwait(false);
        if (chapter.Id != entry.ChapterId || chapter.ProjectId != session.Manifest.ProjectId)
            throw new InvalidDataException("File chương không thuộc liên kết manifest này.");
        return chapter;
    }
}
