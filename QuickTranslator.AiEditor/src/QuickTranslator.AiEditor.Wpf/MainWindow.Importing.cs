using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private async Task OpenDetectedBulkImportAsync()
    {
        if (_projectSession is null)
        {
            ShowError(new InvalidOperationException("Hãy mở hoặc tạo Project trước khi nhập nhiều chương."));
            return;
        }

        var preview = new ImportPreviewWindow(_projectSession) { Owner = this };
        if (preview.ShowDialog() != true || preview.ConfirmedPlan is not ImportPreviewPlan plan) return;
        try
        {
            await RunWorkspaceTransitionAsync(async () =>
            {
                BulkImportReport report;
                while (true)
                {
                    report = await RunBulkImportProgressAsync(plan);
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
        }
        catch (BulkImportValidationException ex)
        {
            ShowError(new InvalidOperationException(ex.Message + Environment.NewLine + string.Join(Environment.NewLine, ex.Issues), ex));
        }
        catch (Exception ex) { ShowError(ex); }
    }

    internal Task<BulkImportReport> ImportDetectedPlanAsync(ImportPreviewPlan plan,
        IProgress<BulkImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_projectSession is null) throw new InvalidOperationException("Chưa mở Project.");
        var defaults = new BulkImportChapterDefaults(ReadSegmenterOptions(), ReadModelOptions(), _presetManager.Selected);
        return new SafeBulkImportService(_projectRepository).ImportAsync(_projectSession, plan, defaults,
            new BulkImportOptions(), progress, cancellationToken);
    }

    private Task<BulkImportReport> RunBulkImportProgressAsync(ImportPreviewPlan plan)
    {
        var window = new BulkImportProgressWindow { Owner = this };
        return window.RunAsync((progress, token) => ImportDetectedPlanAsync(plan, progress, token));
    }

    private void ShowBulkImportReport(BulkImportReport report)
    {
        string message = $"Đã nhập mới: {report.ImportedCount:N0}\n" +
            $"Đã có từ lần trước: {report.AlreadyImportedCount:N0}\n" +
            $"Chưa xử lý: {report.Chapters.Count(x => x.State == BulkImportChapterState.NotStarted):N0}\n" +
            $"Rollback: {report.Chapters.Count(x => x.State == BulkImportChapterState.RolledBack):N0}\n" +
            $"Cần recovery: {report.Chapters.Count(x => x.State == BulkImportChapterState.NeedsRecovery):N0}\n\n" +
            $"Source rescue: {report.SourceArchivePath}" + (report.Error is null ? "" : "\n\n" + report.Error);
        MessageBox.Show(this, message, report.Completed ? "Bulk import hoàn tất" : "Bulk import chưa hoàn tất",
            MessageBoxButton.OK, report.Completed ? MessageBoxImage.Information : MessageBoxImage.Warning);
        SetStatus(report.Completed
            ? $"Đã nhập {report.ImportedCount + report.AlreadyImportedCount:N0} chương; có thể đóng/mở lại Project để kiểm tra."
            : report.Error ?? "Bulk import chưa hoàn tất.");
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
            Background = new SolidColorBrush(Color.FromRgb(7, 17, 31)); Foreground = Brushes.Gainsboro;
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock { Text = "Đang nhập theo transaction chunk", FontSize = 18, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "Có thể hủy. Chunk đang ghi sẽ rollback; các chunk đã commit được giữ.", Foreground = new SolidColorBrush(Color.FromRgb(145, 164, 188)), Margin = new Thickness(4, 5, 4, 0) });
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
            _status.Text = "Đang hủy an toàn và chờ transaction kết thúc…";
            _cancellation.Cancel();
        }
    }
}
