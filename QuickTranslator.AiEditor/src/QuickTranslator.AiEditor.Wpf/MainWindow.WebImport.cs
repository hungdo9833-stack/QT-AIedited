using System.Windows;
using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    internal Func<WebImportOptions, IWebNovelImporter> WebNovelImporterFactory =
        _ => throw new NotSupportedException("Bộ thu thập web chưa được cài.");

    internal Func<string, Task>? WebImportPreviewOpener;

    internal WebImportWindow CreateWebImportWindow()
    {
        if (_projectSession is null) throw new InvalidOperationException("Hãy mở hoặc tạo dự án trước khi nhập truyện từ web.");
        var window = new WebImportWindow(_projectSession, WebNovelImporterFactory,
            path => WebImportPreviewOpener?.Invoke(path) ?? OpenDetectedBulkImportAsync(path));
        if (IsLoaded) window.Owner = this;
        return window;
    }

    private void OnImportWeb(object sender, RoutedEventArgs e)
    {
        if (_projectSession is null || _uiOperation || _closing ||
            _projectTranslationCoordinator?.IsRunning == true) return;
        try { CreateWebImportWindow().ShowDialog(); }
        catch (Exception ex) { ShowError(ex); }
    }
}
