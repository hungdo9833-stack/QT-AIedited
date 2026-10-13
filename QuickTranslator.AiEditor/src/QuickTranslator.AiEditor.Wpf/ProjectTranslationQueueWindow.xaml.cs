using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ProjectTranslationQueueWindow : Window
{
    private readonly ProjectTranslationCoordinator _coordinator;
    private ProjectTranslationJob? _plan;
    private bool _commandBusy;
    private bool _closed;

    internal ProjectTranslationQueueWindow(string projectTitle, ProjectTranslationCoordinator coordinator,
        ProjectTranslationJob? plan)
    {
        _coordinator = coordinator;
        _plan = plan;
        InitializeComponent();
        ProjectText.Text = projectTitle;
        _coordinator.ProgressChanged += OnProgressChanged;
        Closed += OnClosed;
        RefreshView();
    }

    internal IReadOnlyList<QueueRow> CurrentRows => QueueGrid.ItemsSource as IReadOnlyList<QueueRow> ?? [];

    private void OnProgressChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(RefreshView);
    }

    private void RefreshView()
    {
        if (_closed) return;
        ProjectTranslationJob? job = _coordinator.CurrentJob ?? _plan;
        if (job is null)
        {
            QueueGrid.ItemsSource = null;
            StatusText.Text = "Project chưa có translation job.";
            return;
        }
        bool planMode = _plan?.Status == ProjectTranslationJobStatus.Draft && _coordinator.CurrentJob is null;
        HeadingText.Text = planMode ? "Queue Plan · kiểm tra trước khi chạy" : "Translation Queue · tiến độ thật";
        ModelText.Text = job.ModelOptions.Model;
        PromptText.Text = $"{job.PromptPreset.Name} · revision {job.PromptPreset.Revision}";
        ResourceText.Text = job.ProjectResourceRevision.ToString();
        QueueGrid.ItemsSource = job.Items.OrderBy(item => item.Order).Select(item => new QueueRow(item)).ToArray();
        ProjectTranslationProgress progress = planMode
            ? new(ProjectTranslationJobStatus.Draft, job.Items.Count, 0, 0, 0, job.Items.Count, null, null, 0, 0, null)
            : _coordinator.GetProgress();
        SummaryText.Text = $"{StatusLabel(progress.Status)} · {progress.CompletedChapters}/{progress.TotalChapters} xong · " +
            $"{progress.FailedChapters} lỗi · {progress.SkippedChapters} bỏ qua";
        ChapterProgress.Value = progress.TotalChapters == 0 ? 0 : 100d *
            (progress.CompletedChapters + progress.FailedChapters + progress.SkippedChapters) / progress.TotalChapters;
        CurrentText.Text = progress.CurrentChapterId is null ? "Không có chapter đang gửi request."
            : $"Đang dịch: {progress.CurrentChapterTitle} · segment {progress.CurrentCompletedSegments}/{progress.CurrentTotalSegments}";
        StatusText.Text = planMode
            ? $"Sẽ dịch tuần tự {job.Items.Count:N0} chapter. Start mới tạo job bền vững và gửi request đầu tiên."
            : progress.Error ?? "Checkpoint được lưu sau từng segment; đóng/mở lại không tự Resume.";
        StartButton.IsEnabled = !_commandBusy && planMode;
        PauseButton.IsEnabled = !_commandBusy && _coordinator.IsRunning;
        ResumeButton.IsEnabled = !_commandBusy && !_coordinator.IsRunning &&
            progress.Status is ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted;
        CancelButton.IsEnabled = !_commandBusy && _coordinator.IsRunning;
        RetryButton.IsEnabled = !_commandBusy && !_coordinator.IsRunning && progress.FailedChapters > 0;
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        ProjectTranslationJob plan = _plan;
        _plan = null;
        await RunCommandAsync(() => _coordinator.StartAsync(plan), "Hàng đợi đã kết thúc.");
    }

    private async void OnPause(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(_coordinator.PauseAsync, "Đã Pause sau khi lưu checkpoint request hiện tại.");

    private async void OnResume(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(() => _coordinator.ResumeAsync(), "Hàng đợi Resume đã kết thúc.");

    private async void OnCancel(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(_coordinator.CancelPendingAsync, "Đã hủy phần đang chờ; checkpoint đã commit vẫn được giữ.");

    private async void OnRetry(object sender, RoutedEventArgs e) =>
        await RunCommandAsync(() => _coordinator.RetryFailedAsync(), "Đã Retry các chapter lỗi.");

    private async Task RunCommandAsync(Func<Task> command, string success)
    {
        if (_commandBusy) return;
        _commandBusy = true;
        RefreshView();
        try
        {
            await command();
            if (!_closed) StatusText.Text = success;
        }
        catch (OperationCanceledException)
        {
            if (!_closed) StatusText.Text = "Thao tác bị hủy; checkpoint đã commit vẫn được giữ.";
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                StatusText.Text = "Translation Queue dừng: " + ex.Message;
                MessageBox.Show(this, ex.Message, "Translation Queue", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            _commandBusy = false;
            if (!_closed) RefreshView();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _coordinator.ProgressChanged -= OnProgressChanged;
    }

    internal sealed record QueueRow(int Order, int Number, string Title, ProjectTranslationItemStatus Status,
        string StatusLabel, int CompletedSegments, int TotalSegments, string SegmentLabel, int ErrorSegments, string Error)
    {
        public QueueRow(ProjectTranslationItem item) : this(item.Order, item.Number, item.Title, item.Status,
            ItemStatusLabel(item.Status), item.CompletedSegments, item.TotalSegments,
            $"{item.CompletedSegments:N0}/{item.TotalSegments:N0}", item.ErrorSegments, item.Error ?? "") { }
    }

    private static string StatusLabel(ProjectTranslationJobStatus status) => status switch
    {
        ProjectTranslationJobStatus.Draft => "Chưa bắt đầu", ProjectTranslationJobStatus.Ready => "Sẵn sàng",
        ProjectTranslationJobStatus.Running => "Đang dịch", ProjectTranslationJobStatus.PauseRequested => "Đang Pause",
        ProjectTranslationJobStatus.Paused => "Đã Pause", ProjectTranslationJobStatus.Cancelling => "Đang hủy",
        ProjectTranslationJobStatus.Cancelled => "Đã hủy", ProjectTranslationJobStatus.Interrupted => "Cần Resume",
        ProjectTranslationJobStatus.Completed => "Hoàn tất", _ => "Hoàn tất có lỗi"
    };

    private static string ItemStatusLabel(ProjectTranslationItemStatus status) => status switch
    {
        ProjectTranslationItemStatus.Pending => "Đang chờ", ProjectTranslationItemStatus.Running => "Đang dịch",
        ProjectTranslationItemStatus.Paused => "Đã Pause", ProjectTranslationItemStatus.Interrupted => "Bị gián đoạn",
        ProjectTranslationItemStatus.Completed => "Hoàn tất", ProjectTranslationItemStatus.Failed => "Lỗi",
        ProjectTranslationItemStatus.Skipped => "Bỏ qua an toàn", ProjectTranslationItemStatus.NeedsReview => "Cần kiểm tra revision",
        _ => "Đã hủy"
    };
}
