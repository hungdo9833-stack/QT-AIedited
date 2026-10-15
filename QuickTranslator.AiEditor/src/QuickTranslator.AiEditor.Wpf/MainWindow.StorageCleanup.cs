using System.Windows;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private void OnCleanupProjectStorage(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null)
        {
            SetStatus("Hãy mở Project trước khi dọn dữ liệu thừa.");
            return;
        }
        if (WorkActive || _activeRequest is { IsCompleted: false } || _projectTranslationCoordinator?.IsRunning == true)
        {
            SetStatus("Không thể dọn dữ liệu khi tác vụ dịch hoặc Queue đang ghi. Hãy Pause và chờ lưu xong.");
            return;
        }
        if (_service?.HasUnsavedChanges == true)
        {
            SetStatus("Hãy bấm Lưu trước khi dọn dữ liệu thừa.");
            return;
        }
        if (!BeginUiOperation(allowDuringTranslation: false)) return;
        try
        {
            var window = new ProjectStorageCleanupWindow(_projectRepository, _projectSession) { Owner = this };
            ClampWindowToWorkArea(window, 760, 560);
            window.ShowDialog();
            SetStatus(window.LastResult ?? "Đã đóng công cụ dọn dữ liệu thừa.");
        }
        catch (Exception error) { ShowError(error); }
        finally { EndUiOperation(); }
    }
}
