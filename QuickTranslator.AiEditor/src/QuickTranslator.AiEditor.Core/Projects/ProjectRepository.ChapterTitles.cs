using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public enum ExistingVietnameseTitlePolicy { KeepExisting, OverwriteConfirmed }

public sealed record ProjectChapterTitleProposal(Guid ChapterId, string VietnameseTitle);

public sealed record ProjectChapterTitlePreviewItem(Guid ChapterId, int Number, string ChineseTitle,
    string CurrentVietnameseTitle, string ProposedVietnameseTitle, bool ExistingTitleProtected,
    bool WillApply);

public sealed record ProjectChapterTitleUpdatePreview(ExistingVietnameseTitlePolicy Policy,
    IReadOnlyList<ProjectChapterTitlePreviewItem> Chapters)
{
    public int ApplyCount => Chapters.Count(item => item.WillApply);
    public int ProtectedCount => Chapters.Count(item => item.ExistingTitleProtected);
}

public sealed partial class ProjectRepository
{
    public async Task<ProjectChapterTitleUpdatePreview> PreviewChapterTitleUpdateAsync(ProjectSession session,
        IReadOnlyCollection<ProjectChapterTitleProposal> proposals,
        ExistingVietnameseTitlePolicy policy = ExistingVietnameseTitlePolicy.KeepExisting,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(proposals);
        ProjectChapterTitleProposal[] requested = proposals.ToArray();
        if (requested.Length == 0 || requested.Length > 10_000 || requested.Any(item => item.ChapterId == Guid.Empty ||
            string.IsNullOrWhiteSpace(item.VietnameseTitle) || item.VietnameseTitle.Length > 1000) ||
            requested.Select(item => item.ChapterId).Distinct().Count() != requested.Length)
            throw new ArgumentException("Danh sách cập nhật tiêu đề phải có 1–10.000 ChapterId duy nhất và tên hợp lệ.", nameof(proposals));
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = new List<ProjectChapterTitlePreviewItem>(requested.Length);
            foreach (ProjectChapterTitleProposal proposal in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectChapterEntry entry = session.Manifest.Chapters.SingleOrDefault(item =>
                    item.ChapterId == proposal.ChapterId && item.DeletedAt is null)
                    ?? throw new FileNotFoundException($"Không tìm thấy chương hoạt động {proposal.ChapterId:D}.");
                string path = session.Resolve(entry.RelativePath);
                ChapterDocument chapter = await ChapterFileStore.ReadVerifiedFileAsync(path, cancellationToken).ConfigureAwait(false);
                if (chapter.Id != entry.ChapterId || chapter.ProjectId != session.Manifest.ProjectId ||
                    chapter.Title != entry.VietnameseTitle || chapter.ChineseTitle != entry.ChineseTitle)
                    throw new InvalidDataException("Tiêu đề trong chapter và manifest không đồng bộ; chưa thể cập nhật hàng loạt.");
                bool protectedExisting = IsProtectedVietnameseTitle(entry);
                bool apply = !protectedExisting || policy == ExistingVietnameseTitlePolicy.OverwriteConfirmed;
                items.Add(new(entry.ChapterId, entry.Number, entry.ChineseTitle, entry.VietnameseTitle,
                    proposal.VietnameseTitle.Trim(), protectedExisting, apply));
            }
            return new(policy, items.AsReadOnly());
        }
        finally { session.OperationGate.Release(); }
    }

    public async Task ApplyChapterTitleUpdateAsync(ProjectSession session, ProjectChapterTitleUpdatePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(preview);
        if (preview.Chapters.Count == 0 || preview.Chapters.Count > 10_000 ||
            preview.Chapters.Select(item => item.ChapterId).Distinct().Count() != preview.Chapters.Count)
            throw new InvalidDataException("Preview cập nhật tiêu đề không hợp lệ.");
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = new List<ChapterDocument>(preview.ApplyCount);
            foreach (ProjectChapterTitlePreviewItem item in preview.Chapters.Where(item => item.WillApply))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectChapterEntry entry = session.Manifest.Chapters.Single(chapter => chapter.ChapterId == item.ChapterId && chapter.DeletedAt is null);
                string path = session.Resolve(entry.RelativePath);
                ChapterDocument current = await ChapterFileStore.ReadVerifiedFileAsync(path, cancellationToken).ConfigureAwait(false);
                if (current.Title != item.CurrentVietnameseTitle || current.ChineseTitle != item.ChineseTitle)
                    throw new InvalidOperationException($"Tiêu đề chương {entry.Number} đã đổi sau preview.");
                if (item.ExistingTitleProtected && preview.Policy != ExistingVietnameseTitlePolicy.OverwriteConfirmed)
                    throw new InvalidOperationException($"Tiêu đề Việt chương {entry.Number} được bảo vệ khỏi ghi đè.");
                ChapterDocument candidate = DocumentJson.Clone(current);
                candidate.Title = item.ProposedVietnameseTitle.Trim();
                candidate.UpdatedAt = DateTimeOffset.UtcNow;
                DocumentJson.Validate(candidate);
                candidates.Add(candidate);
            }
            foreach (ChapterDocument candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteFastChapterCheckpointAsync(session, candidate, "BatchTranslateChapterTitle", cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally { session.OperationGate.Release(); }
    }

    public static bool IsProtectedVietnameseTitle(ProjectChapterEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.VietnameseTitle)) return false;
        return !string.Equals(entry.VietnameseTitle.Trim(), entry.ChineseTitle.Trim(), StringComparison.Ordinal);
    }
}
