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
            // Preview from manifest metadata only; reading every chapter body here made large batches crawl.
            Dictionary<Guid, ProjectChapterEntry> active = ActiveEntries(session);
            var items = new List<ProjectChapterTitlePreviewItem>(requested.Length);
            foreach (ProjectChapterTitleProposal proposal in requested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ProjectChapterEntry entry = active.GetValueOrDefault(proposal.ChapterId)
                    ?? throw new FileNotFoundException($"Không tìm thấy chương hoạt động {proposal.ChapterId:D}.");
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
            Dictionary<Guid, ProjectChapterEntry> active = ActiveEntries(session);
            ProjectChapterTitlePreviewItem[] work = preview.Chapters.Where(item => item.WillApply).ToArray();
            foreach (ProjectChapterTitlePreviewItem item in work)
            {
                ProjectChapterEntry entry = active.GetValueOrDefault(item.ChapterId)
                    ?? throw new FileNotFoundException($"Không tìm thấy chương hoạt động {item.ChapterId:D}.");
                if (item.ExistingTitleProtected && preview.Policy != ExistingVietnameseTitlePolicy.OverwriteConfirmed)
                    throw new InvalidOperationException($"Tiêu đề Việt chương {entry.Number} được bảo vệ khỏi ghi đè.");
            }
            // Read in parallel, then write all files and commit the manifest once (previously every chapter
            // rewrote the whole manifest with fsync and fired a full UI refresh).
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var chapters = new ChapterDocument[work.Length];
            await Parallel.ForAsync(0, work.Length, new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
                CancellationToken = cancellationToken
            }, async (index, token) =>
            {
                ChapterDocument chapter = await ReadRegisteredChapterAsync(session, active[work[index].ChapterId], token)
                    .ConfigureAwait(false);
                chapter.Title = work[index].ProposedVietnameseTitle.Trim();
                chapter.UpdatedAt = now;
                chapters[index] = chapter;
            }).ConfigureAwait(false);
            await WriteFastChapterBatchAsync(session, chapters, "BatchTranslateChapterTitle", cancellationToken)
                .ConfigureAwait(false);
        }
        finally { session.OperationGate.Release(); }
    }

    private static Dictionary<Guid, ProjectChapterEntry> ActiveEntries(ProjectSession session) =>
        session.Manifest.Chapters.Where(entry => entry.DeletedAt is null).ToDictionary(entry => entry.ChapterId);

    public static bool IsProtectedVietnameseTitle(ProjectChapterEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.VietnameseTitle)) return false;
        return !string.Equals(entry.VietnameseTitle.Trim(), entry.ChineseTitle.Trim(), StringComparison.Ordinal);
    }
}
