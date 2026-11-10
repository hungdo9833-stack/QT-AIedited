using System.Windows;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private void OnOpenChapterManager(object sender, RoutedEventArgs e)
    {
        NavigateTo(WorkspaceId.Chapters);
    }

    private async Task SoftDeleteFromManagerAsync(IReadOnlyList<Guid> chapterIds)
    {
        if (_projectSession is null) throw new InvalidOperationException("Dự án đã đóng.");
        ProjectSession session = _projectSession;
        Guid[] selected = chapterIds.Distinct().ToArray();
        if (selected.Length == 0) return;
        if (selected.Any(id => !session.Manifest.Chapters.Any(entry => entry.ChapterId == id && entry.DeletedAt is null)))
            throw new InvalidOperationException("Lựa chọn đã thay đổi; hãy chọn lại các chương đang hoạt động.");
        EnsureChaptersNotOwnedByActiveTranslationJob(selected);
        bool completed = await RunWorkspaceTransitionAsync(async () =>
        {
            await _projectRepository.SoftDeleteChaptersAsync(session, selected);
            if (session.Manifest.CurrentChapterId is Guid current) await LoadProjectChapterCoreAsync(current);
            else { DetachChapter(); ClearChapterEditor(); RefreshProjectChapterList(); Refresh(); }
        });
        if (!completed) throw new OperationCanceledException("Chưa chuyển chương vì workspace chưa sẵn sàng.");
    }

    private async Task RestoreFromManagerAsync(IReadOnlyList<Guid> chapterIds)
    {
        if (_projectSession is null) throw new InvalidOperationException("Dự án đã đóng.");
        ProjectSession session = _projectSession;
        ProjectChapterEntry[] selected = chapterIds.Distinct().Select(id =>
            session.Manifest.Chapters.SingleOrDefault(entry => entry.ChapterId == id && entry.DeletedAt is not null)
                ?? throw new InvalidOperationException("Lựa chọn đã thay đổi; hãy chọn lại chương trong History.")).ToArray();
        // A restored chapter goes back to its old number and the chapters from there on move up; nothing collides.
        bool completed = await RunWorkspaceTransitionAsync(async () =>
        {
            await _projectRepository.RestoreDeletedChaptersAsync(session, selected.Select(entry => entry.ChapterId).ToArray());
            RefreshProjectChapterList();
        });
        if (!completed) throw new OperationCanceledException("Chưa phục hồi vì workspace chưa sẵn sàng.");
    }

    /// <summary>Tác vụ chương → "Đánh số lại liên tục": closes the gaps an older version left after delete.</summary>
    private async void OnCloseChapterNumberGaps(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null) return;
        ProjectSession session = _projectSession;
        IReadOnlyList<int> missing = ProjectRepository.MissingChapterNumbers(session.Manifest);
        if (missing.Count == 0)
        {
            ThemedDialog.Show(this, "Số chương đã liên tục, không có số nào bị nhảy.", "Đánh số lại chương",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (ThemedDialog.Show(this,
                $"Truyện đang thiếu số chương: {string.Join(", ", missing.Take(15))}{(missing.Count > 15 ? "…" : "")}.\n\n" +
                "Đánh số lại tất cả chương thành 1, 2, 3… theo đúng thứ tự hiện tại? Nội dung và bản dịch không đổi.\n" +
                "Nếu truyện đã đăng lên Sắc Viện, những chương bị đổi số cần đăng lại để số trên web khớp.",
                "Đánh số lại chương", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            int changed = await _projectRepository.CloseChapterNumberGapsAsync(session);
            if (session.Manifest.CurrentChapterId is Guid current && _service is not null) await LoadProjectChapterCoreAsync(current);
            RefreshProjectChapterList();
            SetStatus($"Đã đánh số lại {changed} chương; số chương giờ liên tục từ 1 đến {session.Manifest.ChapterCount}.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    /// <summary>Says once, on opening a book, that its numbers have gaps and how to fix them.</summary>
    private void ReportChapterNumberGaps()
    {
        if (_projectSession is null) return;
        IReadOnlyList<int> missing = ProjectRepository.MissingChapterNumbers(_projectSession.Manifest);
        if (missing.Count > 0)
            SetStatus($"⚠ Số chương bị nhảy (thiếu {string.Join(", ", missing.Take(5))}{(missing.Count > 5 ? "…" : "")}). " +
                "Mục lục → Tác vụ chương → Đánh số lại liên tục để sửa.");
    }

    private void EnsureChaptersNotOwnedByActiveTranslationJob(IReadOnlyCollection<Guid> chapterIds)
    {
        ProjectTranslationJob? job = _projectTranslationCoordinator?.CurrentJob;
        if (job is null || job.Status is ProjectTranslationJobStatus.Completed or
            ProjectTranslationJobStatus.CompletedWithErrors or ProjectTranslationJobStatus.Cancelled) return;
        ProjectTranslationItem[] conflicts = job.Items.Where(item => chapterIds.Contains(item.ChapterId) &&
            item.Status is not ProjectTranslationItemStatus.Completed and not ProjectTranslationItemStatus.Skipped and
            not ProjectTranslationItemStatus.Cancelled).ToArray();
        if (conflicts.Length == 0) return;
        throw new InvalidOperationException($"Có {conflicts.Length:N0} chương còn thuộc Queue đang hoạt động hoặc có checkpoint chưa hoàn tất. " +
            "Hãy Pause rồi hủy các mục pending hoặc hoàn tất Queue trước khi chuyển chương vào History.");
    }

    private async Task ResegmentFromManagerAsync(IReadOnlyList<Guid> chapterIds)
    {
        if (_projectSession is null) throw new InvalidOperationException("Dự án đã đóng.");
        Guid[] selected = chapterIds.Distinct().ToArray();
        EnsureChaptersNotOwnedByActiveTranslationJob(selected);
        if (_projectTranslationCoordinator?.IsRunning == true)
            throw new InvalidOperationException("Hãy tạm dừng hàng đợi dịch và chờ lưu điểm dừng trước khi chia lại đoạn.");
        ProjectSession session = _projectSession;
        bool completed = await RunWorkspaceTransitionAsync(async () =>
        {
            ProjectResegmentationPreview preview = await _projectRepository.PreviewResegmentationAsync(
                session, selected, ReadSegmenterOptions());
            string warnings = string.Join("\n", preview.Chapters.SelectMany(item => item.Preview.Warnings).Take(8));
            string message = $"Xem trước {preview.Chapters.Count:N0} chương\n" +
                $"• Đổi ranh giới: {preview.ChangedChapters:N0}\n" +
                $"• Có đoạn được bảo vệ: {preview.ProtectedChapters:N0}\n" +
                $"• Cảnh báo: {preview.WarningCount:N0}" +
                (warnings.Length == 0 ? "" : "\n\n" + warnings) +
                "\n\nChỉ vùng chưa dịch được chia lại; nháp, approvals và lịch sử giữ nguyên theo anchor. Áp dụng cho toàn bộ lựa chọn?";
            if (ThemedDialog.Show(this, message, "Preview chia lại đoạn", MessageBoxButton.YesNo,
                    preview.ProtectedChapters > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
                throw new OperationCanceledException("Đã hủy chia lại đoạn trước khi ghi dữ liệu.");
            await _projectRepository.ResegmentChaptersAsync(session, preview,
                confirmed: preview.ProtectedChapters > 0);
            if (_service is not null && selected.Contains(_service.GetSnapshot().Id))
                await LoadProjectChapterCoreAsync(_service.GetSnapshot().Id);
            else RefreshProjectChapterList();
        });
        if (!completed) throw new OperationCanceledException("Chưa chia lại đoạn vì workspace chưa sẵn sàng.");
    }
}
