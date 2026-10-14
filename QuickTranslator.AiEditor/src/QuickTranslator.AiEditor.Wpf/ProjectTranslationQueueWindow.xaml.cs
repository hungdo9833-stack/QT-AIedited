using System.Windows;
using System.Windows.Input;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ProjectTranslationQueueWindow : Window
{
    private ProjectTranslationCoordinator _coordinator;
    private readonly Func<ProjectTranslationCoordinator,
        Task<(ProjectTranslationCoordinator Coordinator, ProjectTranslationJob Plan)>>? _replan;
    private readonly Func<Guid, Task>? _openChapter;
    private ProjectTranslationJob? _plan;
    private bool _commandBusy;
    private bool _closed;

    internal ProjectTranslationQueueWindow(string projectTitle, ProjectTranslationCoordinator coordinator,
        ProjectTranslationJob? plan, Func<ProjectTranslationCoordinator,
            Task<(ProjectTranslationCoordinator Coordinator, ProjectTranslationJob Plan)>>? replan = null,
        Func<Guid, Task>? openChapter = null)
    {
        _coordinator = coordinator;
        _plan = plan;
        _replan = replan;
        _openChapter = openChapter;
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 1120, 760);
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
        BackendText.Text = job.Workers.Count > 0 ? string.Join(" + ", job.Workers.Select(worker => worker.Name)) :
            job.Backend is null ? "Local · legacy job" : $"{job.Backend.Kind} · {job.Backend.ProfileName}";
        ModelText.Text = job.Workers.Count > 0 ? string.Join(" / ", job.Workers.Select(worker => worker.ModelOptions.Model).Distinct()) : job.ModelOptions.Model;
        PromptText.Text = $"{job.PromptPreset.Name} · revision {job.PromptPreset.Revision}";
        ResourceText.Text = job.ProjectResourceRevision.ToString();
        Dictionary<Guid, string> workerNames = job.Workers.ToDictionary(worker => worker.WorkerId, worker => worker.Name);
        QueueGrid.ItemsSource = job.Items.OrderBy(item => item.Order).Select(item => new QueueRow(item,
            item.AssignedWorkerId is Guid workerId && workerNames.TryGetValue(workerId, out string? name) ? name : "—")).ToArray();
        IReadOnlyList<ProjectTranslationWorkerProgress> workerProgress = planMode
            ? job.Workers.Select(worker => new ProjectTranslationWorkerProgress(worker.WorkerId,
                worker.Name, worker.Status, null, null, 0, 0, 0, 0, 0, 0, null)).ToArray()
            : _coordinator.GetWorkerProgress();
        Dictionary<Guid, ProjectTranslationWorkerState> workerDefinitions = job.Workers
            .ToDictionary(worker => worker.WorkerId);
        WorkerCards.ItemsSource = workerProgress.Select(progress =>
        {
            workerDefinitions.TryGetValue(progress.WorkerId, out ProjectTranslationWorkerState? definition);
            return new WorkerCard(progress, definition);
        }).ToArray();
        ProjectTranslationProgress progress = planMode
            ? new(ProjectTranslationJobStatus.Draft, job.Items.Count, 0, 0, 0, job.Items.Count, null, null, 0, 0, null)
            : _coordinator.GetProgress();
        int needsReview = job.Items.Count(item => item.Status == ProjectTranslationItemStatus.NeedsReview);
        int skipped = job.Items.Count(item => item.Status is ProjectTranslationItemStatus.Skipped or ProjectTranslationItemStatus.Cancelled);
        SummaryText.Text = $"{StatusLabel(progress.Status)} · {progress.CompletedChapters}/{progress.TotalChapters} xong · " +
            $"{progress.FailedChapters} lỗi · {needsReview} cần tạo lại · {skipped} bỏ qua/hủy";
        ChapterProgress.Value = progress.TotalChapters == 0 ? 0 : 100d *
            (progress.CompletedChapters + progress.FailedChapters + progress.SkippedChapters) / progress.TotalChapters;
        CurrentText.Text = progress.CurrentChapterId is null ? "Không có chapter đang gửi request."
            : $"Đang dịch: {progress.CurrentChapterTitle} · segment {progress.CurrentCompletedSegments}/{progress.CurrentTotalSegments}";
        if (job.Workers.Count > 0)
        {
            ProjectTranslationAggregateMetrics metrics = _coordinator.GetAggregateMetrics();
            string eta = metrics.EstimatedRemaining is TimeSpan remaining
                ? $"ETA {remaining:hh\\:mm\\:ss}" : "ETA đang học tốc độ";
            CurrentText.Text = $"Tổng {metrics.HanCharactersPerMinute:N0} chữ Hán/phút · {eta} · " +
                $"chi phí QT Studio ước tính {metrics.EstimatedCost:N2}";
        }
        string? recoveryHint = needsReview > 0 || progress.Status == ProjectTranslationJobStatus.Interrupted
            ? "Bấm “Tạo lại phần chưa xong” để chốt cấu hình hiện tại; checkpoint đã hoàn tất vẫn được giữ."
            : null;
        StatusText.Text = planMode
            ? $"Sẽ dịch {job.Items.Count:N0} chapter bằng {Math.Max(1, job.Workers.Count)} worker. Start mới tạo job bền vững và gửi request đầu tiên."
            : string.Join(" ", new[]
            {
                progress.Error ?? "Checkpoint được lưu sau từng segment; đóng/mở lại không tự Resume.", recoveryHint
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        StartButton.IsEnabled = !_commandBusy && planMode;
        PauseButton.IsEnabled = !_commandBusy && _coordinator.IsRunning;
        ResumeButton.IsEnabled = !_commandBusy && !_coordinator.IsRunning &&
            progress.Status is ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted;
        CancelButton.IsEnabled = !_commandBusy && _coordinator.IsRunning;
        RetryButton.IsEnabled = !_commandBusy && !_coordinator.IsRunning && progress.FailedChapters > 0;
        ReplanButton.IsEnabled = !_commandBusy && !planMode && !_coordinator.IsRunning && job.Items.Any(item =>
            item.Status is not (ProjectTranslationItemStatus.Completed or ProjectTranslationItemStatus.Skipped));
        RetryWorkerButton.IsEnabled = !_commandBusy && !_coordinator.IsRunning && job.Workers.Any(worker =>
            worker.Status is ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted);
        ReassignButton.IsEnabled = RetryWorkerButton.IsEnabled && job.Workers.Count == 2;
    }

    private async void OnQueueGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (QueueGrid.SelectedItem is not QueueRow row || _openChapter is null) return;
        await OpenChapterRowAsync(row.ChapterId);
    }

    internal async Task OpenChapterRowAsync(Guid chapterId)
    {
        if (_openChapter is null) return;
        if (_coordinator.IsChapterLockedByRunningQueue(chapterId))
        {
            StatusText.Text = "Chương này đang chờ hoặc đang dịch trong Queue. Hãy chọn chương đã hoàn tất/lỗi hoặc bấm Pause.";
            return;
        }
        try
        {
            await _openChapter(chapterId);
            Owner?.Activate();
            StatusText.Text = "Đã mở chương trong Editor (hàng đợi nền vẫn an toàn).";
        }
        catch (Exception ex) { ReportError(ex); }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        ProjectTranslationJob plan = _plan;
        _plan = null;
        await RunQueueAsync(() => _coordinator.StartAsync(plan), "Hàng đợi đã kết thúc.", plan);
    }

    private async void OnPause(object sender, RoutedEventArgs e) =>
        await RunControlAsync(_coordinator.PauseAsync, "Đã Pause sau khi lưu checkpoint request hiện tại.");

    private async void OnResume(object sender, RoutedEventArgs e) =>
        await RunQueueAsync(() => _coordinator.ResumeAsync(), "Hàng đợi Resume đã kết thúc.");

    private async void OnCancel(object sender, RoutedEventArgs e) =>
        await RunControlAsync(_coordinator.CancelPendingAsync, "Đã hủy phần đang chờ; checkpoint đã commit vẫn được giữ.");

    private async void OnRetry(object sender, RoutedEventArgs e) =>
        await RunQueueAsync(() => _coordinator.RetryFailedAsync(), "Đã Retry các chapter lỗi.");

    private async void OnReplan(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
            "Tạo hàng đợi mới cho toàn bộ chapter chưa hoàn tất bằng card/model, prompt và tài nguyên hiện tại?\n\n" +
            "Các chapter và segment đã hoàn tất vẫn được giữ nguyên.",
            "Tạo lại phần chưa xong", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (_commandBusy) return;
        _commandBusy = true;
        RefreshView();
        try
        {
            if (_replan is null) _plan = await _coordinator.ReplanUnfinishedAsync();
            else
            {
                ProjectTranslationCoordinator previous = _coordinator;
                (ProjectTranslationCoordinator Coordinator, ProjectTranslationJob Plan) replacement =
                    await _replan(previous);
                previous.ProgressChanged -= OnProgressChanged;
                _coordinator = replacement.Coordinator;
                _coordinator.ProgressChanged += OnProgressChanged;
                _plan = replacement.Plan;
            }
            StatusText.Text = $"Đã tạo Queue Plan mới cho {_plan.Items.Count:N0} chapter chưa hoàn tất. Kiểm tra snapshot rồi bấm Start.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { _commandBusy = false; if (!_closed) RefreshView(); }
    }

    private async void OnRetryWorker(object sender, RoutedEventArgs e)
    {
        ProjectTranslationWorkerProgress? worker = _coordinator.GetWorkerProgress().FirstOrDefault(value =>
            value.Status is ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted);
        if (worker is null) return;
        if (MessageBox.Show(this,
            $"Retry worker “{worker.Name}”? Chapter có request Remote không chắc chắn sẽ được gửi lại sau khi bạn đã kiểm tra checkpoint.",
            "Xác nhận Retry Remote", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await RunControlAsync(() => _coordinator.RetryWorkerAsync(worker.WorkerId, true),
            "Worker đã sẵn sàng; bấm Resume để tiếp tục.");
    }

    private async void OnReassignPending(object sender, RoutedEventArgs e)
    {
        ProjectTranslationWorkerProgress[] workers = _coordinator.GetWorkerProgress().ToArray();
        ProjectTranslationWorkerProgress? offline = workers.FirstOrDefault(value =>
            value.Status is ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted);
        ProjectTranslationWorkerProgress? healthy = workers.FirstOrDefault(value => value.WorkerId != offline?.WorkerId);
        if (offline is null || healthy is null) return;
        await RunControlAsync(() => _coordinator.ReassignPendingAsync(offline.WorkerId, healthy.WorkerId),
            $"Chỉ chapter Pending chưa gửi request được chuyển sang {healthy.Name}.");
    }

    private async Task RunQueueAsync(Func<Task> launch, string success, ProjectTranslationJob? recoverPlan = null)
    {
        if (_commandBusy) return;
        _commandBusy = true;
        RefreshView();
        Task run;
        try
        {
            run = launch();
        }
        catch (Exception ex)
        {
            if (recoverPlan is not null && _coordinator.CurrentJob is null) _plan = recoverPlan;
            _commandBusy = false;
            ReportError(ex);
            RefreshView();
            return;
        }
        _commandBusy = false;
        RefreshView();
        try
        {
            await run;
            if (!_closed) StatusText.Text = success;
        }
        catch (OperationCanceledException)
        {
            if (!_closed) StatusText.Text = "Thao tác bị hủy; checkpoint đã commit vẫn được giữ.";
        }
        catch (Exception ex)
        {
            if (recoverPlan is not null && _coordinator.CurrentJob is null) _plan = recoverPlan;
            ReportError(ex);
        }
        finally { if (!_closed) RefreshView(); }
    }

    private async Task RunControlAsync(Func<Task> command, string success)
    {
        if (_commandBusy) return;
        _commandBusy = true;
        RefreshView();
        try
        {
            await command();
            if (!_closed) StatusText.Text = success;
        }
        catch (Exception ex) { ReportError(ex); }
        finally { _commandBusy = false; if (!_closed) RefreshView(); }
    }

    private void ReportError(Exception ex)
    {
        if (_closed) return;
        StatusText.Text = "Translation Queue dừng: " + ex.Message;
        MessageBox.Show(this, ex.Message, "Translation Queue", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _coordinator.ProgressChanged -= OnProgressChanged;
    }

    internal sealed record QueueRow(Guid ChapterId, int Order, int Number, string Title, string Worker, ProjectTranslationItemStatus Status,
        string StatusLabel, int CompletedSegments, int TotalSegments, string SegmentLabel, int ErrorSegments, string Error)
    {
        public QueueRow(ProjectTranslationItem item, string worker = "—") : this(item.ChapterId, item.Order, item.Number, item.Title, worker, item.Status,
            ItemStatusLabel(item.Status), item.CompletedSegments, item.TotalSegments,
            $"{item.CompletedSegments:N0}/{item.TotalSegments:N0}", item.ErrorSegments,
            item.Error ?? item.PreflightWarning ?? "") { }
    }

    internal sealed record WorkerCard(Guid WorkerId, string Name, ProjectTranslationWorkerStatus Status,
        string BackendLabel, string Model, int ContextSize, int OutputBudget, string Segmentation,
        string? CurrentChapterTitle, int CompletedSegments, int TotalSegments,
        double HanCharactersPerMinute, double TokensPerSecond, long InputTokens, long GeneratedTokens,
        int QualityErrors, decimal EstimatedCost)
    {
        public WorkerCard(ProjectTranslationWorkerProgress progress, ProjectTranslationWorkerState? definition) : this(
            progress.WorkerId, progress.Name, progress.Status,
            definition is null ? "Legacy worker" : $"{definition.Backend.Kind} · {definition.Backend.ProfileName}",
            definition?.ModelOptions.Model ?? "—", definition?.ModelOptions.ContextSize ?? 0,
            definition?.ModelOptions.MaximumOutputTokens ?? 0,
            definition is null ? "—" : $"{definition.SegmentationOptions.MinimumHanCharacters}/" +
                $"{definition.SegmentationOptions.TargetHanCharacters}/{definition.SegmentationOptions.MaximumHanCharacters}",
            progress.CurrentChapterTitle, progress.CompletedSegments, progress.TotalSegments,
            progress.HanCharactersPerMinute, progress.TokensPerSecond, progress.InputTokens,
            progress.OutputTokens, progress.ErrorSegments + progress.TruncatedOutputCount +
                progress.PossibleMissingContentCount + progress.TimeoutCount, progress.EstimatedCost) { }
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
