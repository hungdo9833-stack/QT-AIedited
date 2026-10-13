using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private void OnOpenChapterManager(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null)
        {
            ShowError(new InvalidOperationException("Hãy mở Project trước khi quản lý chương."));
            return;
        }
        if (_chapterManagerWindow is { IsVisible: true })
        {
            _chapterManagerWindow.Activate();
            return;
        }
        ProjectSession session = _projectSession;
        _chapterManagerWindow = new(session, _projectCatalog, new(
            Open: LoadProjectChapterAsync,
            Rename: RenameProjectChapterAsync,
            SoftDelete: SoftDeleteFromManagerAsync,
            Restore: RestoreFromManagerAsync,
            Queue: OpenProjectTranslationQueueAsync)) { Owner = this };
        _chapterManagerWindow.Closed += (_, _) => _chapterManagerWindow = null;
        _chapterManagerWindow.Show();
    }

    private async Task SoftDeleteFromManagerAsync(IReadOnlyList<Guid> chapterIds)
    {
        if (_projectSession is null) throw new InvalidOperationException("Project đã đóng.");
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
        if (_projectSession is null) throw new InvalidOperationException("Project đã đóng.");
        ProjectSession session = _projectSession;
        ProjectChapterEntry[] selected = chapterIds.Distinct().Select(id =>
            session.Manifest.Chapters.SingleOrDefault(entry => entry.ChapterId == id && entry.DeletedAt is not null)
                ?? throw new InvalidOperationException("Lựa chọn đã thay đổi; hãy chọn lại chương trong History.")).ToArray();
        int[] duplicateNumbers = selected.GroupBy(entry => entry.Number).Where(group => group.Count() > 1)
            .Select(group => group.Key).ToArray();
        int[] activeNumbers = session.Manifest.Chapters.Where(entry => entry.DeletedAt is null)
            .Select(entry => entry.Number).Intersect(selected.Select(entry => entry.Number)).ToArray();
        if (duplicateNumbers.Length > 0 || activeNumbers.Length > 0)
            throw new InvalidOperationException("Không thể phục hồi vì số chương đang trùng: " +
                string.Join(", ", duplicateNumbers.Concat(activeNumbers).Distinct().Order()));
        bool completed = await RunWorkspaceTransitionAsync(async () =>
        {
            await _projectRepository.RestoreDeletedChaptersAsync(session, selected.Select(entry => entry.ChapterId).ToArray());
            RefreshProjectChapterList();
        });
        if (!completed) throw new OperationCanceledException("Chưa phục hồi vì workspace chưa sẵn sàng.");
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
}
