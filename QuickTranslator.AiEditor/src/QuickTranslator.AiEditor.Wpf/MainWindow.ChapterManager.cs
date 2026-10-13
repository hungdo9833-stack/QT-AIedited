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
        bool completed = await RunWorkspaceTransitionAsync(async () =>
        {
            foreach (Guid id in selected) await _projectRepository.SoftDeleteChapterAsync(session, id);
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
            foreach (ProjectChapterEntry entry in selected)
                await _projectRepository.RestoreDeletedChapterAsync(session, entry.ChapterId);
            RefreshProjectChapterList();
        });
        if (!completed) throw new OperationCanceledException("Chưa phục hồi vì workspace chưa sẵn sàng.");
    }
}
