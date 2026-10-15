using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private async Task OpenDetectedBulkImportAsync(string? initialTxtPath = null)
    {
        if (_projectSession is null)
        {
            ShowError(new InvalidOperationException("Hãy mở hoặc tạo Project trước khi nhập nhiều chương."));
            return;
        }

        await EnsureOllamaBackendsLoadedAsync();
        var preview = new ImportPreviewWindow(_projectSession, CreateChapterTitleRuntimeAsync, initialTxtPath) { Owner = this };
        ClampWindowToWorkArea(preview, 1380, 860);
        if (preview.ShowDialog() != true || preview.ConfirmedPlan is not ImportPreviewPlan plan) return;
        Guid[] importedChapterIds = [];
        try
        {
            await RunWorkspaceTransitionAsync(async () =>
            {
                BulkImportReport report;
                while (true)
                {
                    report = await RunBulkImportProgressAsync(plan, preview.ReplaceSoftDeletedHistory);
                    importedChapterIds = report.Chapters.Where(chapter =>
                            chapter.State == BulkImportChapterState.Imported)
                        .OrderBy(chapter => chapter.Number).Select(chapter => chapter.ChapterId).ToArray();
                    RefreshProjectChapterList();
                    if (_projectSession is { RecoveryReport.RequiresDecision: false } &&
                        _projectSession.Manifest.CurrentChapterId is Guid current)
                        await LoadProjectChapterCoreAsync(current);
                    ShowBulkImportReport(report);
                    if (report.Completed || !report.CanResume ||
                        MessageBox.Show(this, "Tiếp tục ngay các chương chưa xử lý bằng đúng preview và stable ID này?",
                            "Tiếp tục bulk import", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        break;
                }
                if (!report.Completed && !report.CanResume && _projectSession?.RecoveryReport.RequiresDecision == true)
                    await ReviewProjectRecoveryAsync(_projectSession);
            });
            if (preview.ImportAndTranslate && importedChapterIds.Length > 0)
                await OpenProjectTranslationQueueAsync(importedChapterIds);
        }
        catch (BulkImportValidationException ex)
        {
            ShowError(new InvalidOperationException(ex.Message + Environment.NewLine + string.Join(Environment.NewLine, ex.Issues), ex));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnWindowPreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Any(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                           f.EndsWith(".qtproj", StringComparison.OrdinalIgnoreCase)))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private async void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        await HandleDroppedFilesAsync(files);
    }

    internal async Task<bool> HandleDroppedFilesAsync(IReadOnlyList<string> files)
    {
        if (files.Count == 0 || WorkActive || _uiOperation) return false;
        string? qtproj = files.FirstOrDefault(f => f.EndsWith(".qtproj", StringComparison.OrdinalIgnoreCase));
        if (qtproj is not null)
        {
            await OpenProjectPathAsync(qtproj);
            return true;
        }
        string? txt = files.FirstOrDefault(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));
        if (txt is null) return false;
        if (_projectSession is not null && IsVisible)
        {
            await OpenDetectedBulkImportAsync(txt);
            return true;
        }
        if (!BeginUiOperation()) return false;
        try
        {
            AssignOriginalText(await File.ReadAllTextAsync(txt, new UTF8Encoding(false, true)));
            TitleInput.Text = Path.GetFileNameWithoutExtension(txt);
            SetStatus($"Đã nạp tệp kéo-thả “{Path.GetFileName(txt)}”.");
            return true;
        }
        catch (Exception ex) { ShowError(ex); return false; }
        finally { EndUiOperation(); }
    }

    internal Task<BulkImportReport> ImportDetectedPlanAsync(ImportPreviewPlan plan,
        IProgress<BulkImportProgress>? progress = null, CancellationToken cancellationToken = default)
        => ImportDetectedPlanWithOptionsAsync(plan, false, progress, cancellationToken);

    private Task<BulkImportReport> ImportDetectedPlanWithOptionsAsync(ImportPreviewPlan plan,
        bool replaceSoftDeletedHistory, IProgress<BulkImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_projectSession is null) throw new InvalidOperationException("Chưa mở Project.");
        var defaults = new BulkImportChapterDefaults(ReadSegmenterOptions(), ReadModelOptions(), _presetManager.Selected);
        return new SafeBulkImportService(_projectRepository).ImportAsync(_projectSession, plan, defaults,
            new BulkImportOptions(ReplaceSoftDeletedHistory: replaceSoftDeletedHistory), progress, cancellationToken);
    }

    private Task<BulkImportReport> RunBulkImportProgressAsync(ImportPreviewPlan plan, bool replaceSoftDeletedHistory)
    {
        var window = new BulkImportProgressWindow { Owner = this };
        ClampWindowToWorkArea(window, 560, 320);
        return window.RunAsync((progress, token) =>
            ImportDetectedPlanWithOptionsAsync(plan, replaceSoftDeletedHistory, progress, token));
    }

    private void ShowBulkImportReport(BulkImportReport report)
    {
        if (report.Completed)
        {
            SetStatus(report.ImportedCount > 0
                ? $"Đã nhập {report.ImportedCount:N0} chương mới" +
                  (report.AlreadyImportedCount > 0 ? $"; bỏ qua {report.AlreadyImportedCount:N0} chương đã có." : ".")
                : $"Không có chương mới; {report.AlreadyImportedCount:N0} chương đã tồn tại.");
            return;
        }
        string message = report.Error ?? "Một số chương chưa được nhập. Bạn có thể thử tiếp tục mà không tạo bản trùng.";
        MessageBox.Show(this, message, "Nhập truyện chưa hoàn tất", MessageBoxButton.OK, MessageBoxImage.Warning);
        SetStatus(message);
    }

    private sealed class BulkImportProgressWindow : Window
    {
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 1, Height = 12, Margin = new Thickness(4, 14, 4, 8) };
        private readonly TextBlock _status = new() { Text = "Đang chuẩn bị source rescue…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
        private readonly Button _cancel = new() { Content = "Hủy an toàn", Margin = new Thickness(4), Padding = new Thickness(12, 7, 12, 7) };
        private readonly CancellationTokenSource _cancellation = new();
        private bool _completed;

        public BulkImportProgressWindow()
        {
            Title = "QT Studio · Safe Bulk Import";
            Width = 560; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
            SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
            SetResourceReference(ForegroundProperty, "TextBrush");
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "Đang nhập truyện", FontSize = 18, FontWeight = FontWeights.SemiBold });
            var subtitle = new TextBlock { Text = "Có thể hủy an toàn. Những chương đã nhập xong vẫn được giữ.", Margin = new Thickness(4, 5, 4, 0) };
            subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            panel.Children.Add(subtitle);
            panel.Children.Add(_bar); panel.Children.Add(_status); panel.Children.Add(_cancel); Content = panel;
            _cancel.Click += (_, _) => { _cancel.IsEnabled = false; _status.Text = "Đang hủy an toàn và chờ rollback chunk hiện tại…"; _cancellation.Cancel(); };
            Closing += OnClosing;
        }

        public Task<BulkImportReport> RunAsync(Func<IProgress<BulkImportProgress>, CancellationToken, Task<BulkImportReport>> action)
        {
            var completion = new TaskCompletionSource<BulkImportReport>(TaskCreationOptions.RunContinuationsAsynchronously);
            Loaded += async (_, _) =>
            {
                try
                {
                    var progress = new Progress<BulkImportProgress>(value =>
                    {
                        _bar.Maximum = Math.Max(1, value.TotalChunks); _bar.Value = value.CompletedChunks;
                        _status.Text = $"{value.Message}\n{value.ImportedChapters:N0}/{value.TotalChapters:N0} chương đã xác nhận.";
                    });
                    BulkImportReport report = await action(progress, _cancellation.Token);
                    completion.TrySetResult(report);
                }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { _completed = true; Close(); }
            };
            ShowDialog();
            return completion.Task;
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_completed) return;
            e.Cancel = true;
            _cancel.IsEnabled = false;
            _status.Text = "Đang hoàn tất phần đang ghi để dừng an toàn…";
            _cancellation.Cancel();
        }
    }
}
