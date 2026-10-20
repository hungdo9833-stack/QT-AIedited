using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ProjectTranslationQueueWindow : Window
{
    private ProjectTranslationCoordinator _coordinator;
    private readonly Func<ProjectTranslationCoordinator,
        Task<(ProjectTranslationCoordinator Coordinator, ProjectTranslationJob Plan)>>? _replan;
    private readonly Func<Guid, Task>? _openChapter;
    private readonly Func<ProjectTranslationCoordinator, Task<ProjectTranslationCoordinator>>? _refreshCoordinator;
    private ProjectTranslationJob? _plan;
    private bool _commandBusy;
    private bool _closed;
    private bool _autoStartPending;
    private readonly ObservableCollection<QueueRow> _rows = [];
    private readonly Dictionary<Guid, QueueRow> _rowsByChapter = [];
    private readonly DispatcherTimer _refreshTimer;
    private int _rowObjectCreationCount;

    internal ProjectTranslationQueueWindow(string projectTitle, ProjectTranslationCoordinator coordinator,
        ProjectTranslationJob? plan, Func<ProjectTranslationCoordinator,
            Task<(ProjectTranslationCoordinator Coordinator, ProjectTranslationJob Plan)>>? replan = null,
        Func<Guid, Task>? openChapter = null,
        Func<ProjectTranslationCoordinator, Task<ProjectTranslationCoordinator>>? refreshCoordinator = null)
    {
        _coordinator = coordinator;
        _plan = plan;
        _replan = replan;
        _openChapter = openChapter;
        _refreshCoordinator = refreshCoordinator;
        InitializeComponent();
        DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.TextBlock.TextProperty, typeof(System.Windows.Controls.TextBlock))
            .AddValueChanged(CurrentText, (_, _) => Accessibility.Announce(CurrentText));
        DependencyPropertyDescriptor.FromProperty(System.Windows.Controls.TextBlock.TextProperty, typeof(System.Windows.Controls.TextBlock))
            .AddValueChanged(StatusText, (_, _) => Accessibility.Announce(StatusText));
        QueueGrid.ItemsSource = _rows;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;
        MainWindow.ClampWindowToWorkArea(this, 1120, 760);
        ProjectText.Text = projectTitle;
        _coordinator.ProgressChanged += OnProgressChanged;
        Closed += OnClosed;
        _autoStartPending = plan is not null;
        Loaded += OnLoaded;
        RefreshView();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (!_autoStartPending) return;
        _autoStartPending = false;
        await StartPlanAsync();
    }

    internal IReadOnlyList<QueueRow> CurrentRows => _rows;
    internal int RowObjectCreationCount => _rowObjectCreationCount;
    internal QueueRefreshMeasurement MeasureRefresh()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        RefreshView();
        stopwatch.Stop();
        return new(stopwatch.Elapsed, GC.GetAllocatedBytesForCurrentThread() - before, _rowObjectCreationCount);
    }

    private void OnProgressChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_closed && !_refreshTimer.IsEnabled) _refreshTimer.Start();
        });
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        RefreshView();
    }

    private void RefreshView()
    {
        if (_closed) return;
        ProjectTranslationViewSnapshot? view = _coordinator.GetViewSnapshot();
        ProjectTranslationJob? job = view?.Job ?? _plan;
        if (job is null)
        {
            _rows.Clear();
            _rowsByChapter.Clear();
            StatusText.Text = "Project chưa có translation job.";
            return;
        }
        bool planMode = _plan?.Status == ProjectTranslationJobStatus.Draft && view is null;
        HeadingText.Text = planMode ? "Chuẩn bị dịch" : "Tiến độ dịch";
        BackendText.Text = job.Workers.Count > 0 ? string.Join(" + ", job.Workers.Select(worker => worker.Name)) :
            job.Backend is null ? "Local · cấu hình mặc định" : $"{job.Backend.Kind} · {job.Backend.ProfileName}";
        ModelText.Text = job.Workers.Count > 0 ? string.Join(" / ", job.Workers.Select(worker => worker.ModelOptions.Model).Distinct()) : job.ModelOptions.Model;
        PromptText.Text = $"{job.PromptPreset.Name} · revision {job.PromptPreset.Revision}";
        ResourceText.Text = job.ProjectResourceRevision.ToString();
        Dictionary<Guid, string> workerNames = job.Workers.ToDictionary(worker => worker.WorkerId, worker => worker.Name);
        SynchronizeRows(job, workerNames);
        IReadOnlyList<ProjectTranslationWorkerProgress> workerProgress = planMode
            ? job.Workers.Select(worker => new ProjectTranslationWorkerProgress(worker.WorkerId,
                worker.Name, worker.Status, null, null, 0, 0, 0, 0, 0, 0, null)).ToArray()
            : view!.Workers;
        Dictionary<Guid, ProjectTranslationWorkerState> workerDefinitions = job.Workers
            .ToDictionary(worker => worker.WorkerId);
        WorkerCards.ItemsSource = workerProgress.Select(progress =>
        {
            workerDefinitions.TryGetValue(progress.WorkerId, out ProjectTranslationWorkerState? definition);
            return new WorkerCard(progress, definition);
        }).ToArray();
        ProjectTranslationProgress progress = planMode
            ? new(ProjectTranslationJobStatus.Draft, job.Items.Count, 0, 0, 0, job.Items.Count, null, null, 0, 0, null)
            : view!.Progress;
        int needsReview = job.Items.Count(item => item.Status == ProjectTranslationItemStatus.NeedsReview);
        int skipped = job.Items.Count(item => item.Status is ProjectTranslationItemStatus.Skipped or ProjectTranslationItemStatus.Cancelled);
        int running = job.Items.Count(item => item.Status == ProjectTranslationItemStatus.Running);
        int remaining = job.Items.Count(item => item.Status is ProjectTranslationItemStatus.Pending or
            ProjectTranslationItemStatus.Paused or ProjectTranslationItemStatus.Interrupted);
        TotalMetricText.Text = progress.TotalChapters.ToString("N0");
        CompletedMetricText.Text = progress.CompletedChapters.ToString("N0");
        RunningMetricText.Text = running.ToString("N0");
        RemainingMetricText.Text = remaining.ToString("N0");
        SummaryText.Text = $"{StatusLabel(progress.Status)} · {progress.CompletedChapters}/{progress.TotalChapters} xong · " +
            $"{progress.FailedChapters} lỗi · {needsReview} cần kiểm tra nguồn · {skipped} bỏ qua/hủy";
        ChapterProgress.Value = progress.TotalChapters == 0 ? 0 : 100d *
            (progress.CompletedChapters + progress.FailedChapters + progress.SkippedChapters) / progress.TotalChapters;
        CurrentText.Text = progress.CurrentChapterId is null ? "Không có chương đang gửi yêu cầu."
            : $"Đang dịch: {progress.CurrentChapterTitle} · đoạn {progress.CurrentCompletedSegments}/{progress.CurrentTotalSegments}";
        if (job.Workers.Count > 0)
        {
            ProjectTranslationAggregateMetrics metrics = planMode
                ? new(0, null, 0) : view!.Aggregate;
            string eta = metrics.EstimatedRemaining is TimeSpan estimated
                ? $"ETA {estimated:hh\\:mm\\:ss}" : "ETA đang học tốc độ";
            CurrentText.Text = $"Tốc độ {metrics.HanCharactersPerMinute:N0} chữ Hán/phút · {eta} · " +
                $"chi phí ước tính {metrics.EstimatedCost:N2}";
        }
        string? resumeHint = needsReview > 0 || progress.Status == ProjectTranslationJobStatus.Interrupted
            ? needsReview > 0
                ? "Một số chương đã đổi nguyên văn hoặc ranh giới. Hãy kiểm tra trước khi xử lý lại."
                : "Bấm “Tiếp tục dịch”; cấu hình hợp lệ hiện tại sẽ được áp dụng tự động."
            : null;
        StatusText.Text = planMode
            ? $"Đang chuẩn bị dịch {job.Items.Count:N0} chương bằng {Math.Max(1, job.Workers.Count)} GPU."
            : string.Join(" ", new[]
            {
                progress.Error ?? "Tiến độ được lưu sau từng segment; đóng/mở lại không tự Resume.", resumeHint
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
        StartButton.IsEnabled = !_commandBusy && planMode;
        PauseButton.IsEnabled = !_commandBusy && _coordinator.IsRunning;
        ResumeButton.IsEnabled = !_commandBusy && !_coordinator.IsRunning &&
            progress.Status is ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted;
        CancelButton.IsEnabled = !_commandBusy && _coordinator.IsRunning;
        RetryButton.IsEnabled = !_commandBusy && !_coordinator.IsRunning && progress.FailedChapters > 0;
        ReplanButton.IsEnabled = !_commandBusy && !planMode && !_coordinator.IsRunning && job.Items.Any(item =>
            item.Status == ProjectTranslationItemStatus.NeedsReview);
        RetryWorkerButton.IsEnabled = !_commandBusy && !_coordinator.IsRunning && job.Workers.Any(worker =>
            worker.Status is ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted);
        ReassignButton.IsEnabled = RetryWorkerButton.IsEnabled && job.Workers.Count == 2;
        StartButton.Visibility = planMode ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Visibility = _coordinator.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.Visibility = _coordinator.IsRunning ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.Visibility = !_coordinator.IsRunning && progress.Status is ProjectTranslationJobStatus.Paused or
            ProjectTranslationJobStatus.Interrupted ? Visibility.Visible : Visibility.Collapsed;
        TroubleExpander.Visibility = RetryButton.IsEnabled || ReplanButton.IsEnabled || RetryWorkerButton.IsEnabled ||
            ReassignButton.IsEnabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SynchronizeRows(ProjectTranslationJob job, IReadOnlyDictionary<Guid, string> workerNames)
    {
        bool structureChanged = job.Items.Count != _rows.Count || job.Items.Any(item => !_rowsByChapter.ContainsKey(item.ChapterId));
        if (structureChanged)
        {
            _rows.Clear();
            _rowsByChapter.Clear();
            foreach (ProjectTranslationItem item in job.Items.OrderBy(item => item.Order))
            {
                string worker = item.AssignedWorkerId is Guid workerId && workerNames.TryGetValue(workerId, out string? name)
                    ? name : "—";
                var row = new QueueRow(item, worker);
                _rows.Add(row);
                _rowsByChapter[item.ChapterId] = row;
                _rowObjectCreationCount++;
            }
            return;
        }
        foreach (ProjectTranslationItem item in job.Items)
        {
            string worker = item.AssignedWorkerId is Guid workerId && workerNames.TryGetValue(workerId, out string? name)
                ? name : "—";
            _rowsByChapter[item.ChapterId].Apply(item, worker);
        }
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

    private async void OnStart(object sender, RoutedEventArgs e) => await StartPlanAsync();

    private async Task StartPlanAsync()
    {
        if (_plan is null) return;
        ProjectTranslationJob plan = _plan;
        await RunQueueAsync(() => _coordinator.StartAsync(plan), "Hàng đợi đã kết thúc.", plan);
        if (_coordinator.CurrentJob is not null) _plan = null;
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "Đang chờ request hiện tại hoàn tất và lưu checkpoint…";
        await RunControlAsync(_coordinator.PauseAsync, "Đã Pause an toàn sau checkpoint hiện tại.");
    }

    private async void OnResume(object sender, RoutedEventArgs e)
        => await ResumeNowAsync();

    internal Task ResumeNowAsync() => RunQueueAsync(ResumeWithCurrentConfigurationAsync,
        "Hàng đợi đã xử lý xong phần còn lại.");

    private async Task ResumeWithCurrentConfigurationAsync()
    {
        if (_refreshCoordinator is not null)
        {
            ProjectTranslationCoordinator previous = _coordinator;
            ProjectTranslationCoordinator refreshed = await _refreshCoordinator(previous);
            if (!ReferenceEquals(previous, refreshed))
            {
                previous.ProgressChanged -= OnProgressChanged;
                _coordinator = refreshed;
                _coordinator.ProgressChanged += OnProgressChanged;
            }
        }
        await _coordinator.ResumeWithCurrentConfigurationAsync();
    }

    private async void OnCancel(object sender, RoutedEventArgs e) =>
        await RunControlAsync(_coordinator.StopAsync,
            "Đã dừng nhanh; checkpoint đã commit được giữ và có thể tiếp tục sau.");

    private async void OnRetry(object sender, RoutedEventArgs e) =>
        await RunQueueAsync(() => _coordinator.RetryFailedAsync(), "Đã Retry các chapter lỗi.");

    private async void OnReplan(object sender, RoutedEventArgs e)
    {
        if (ThemedDialog.Show(this,
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
            StatusText.Text = $"Đã chuẩn bị lại {_plan.Items.Count:N0} chương chưa hoàn tất. Bấm Dịch để bắt đầu.";
        }
        catch (Exception ex) { ReportError(ex); }
        finally { _commandBusy = false; if (!_closed) RefreshView(); }
    }

    private async void OnRetryWorker(object sender, RoutedEventArgs e)
    {
        ProjectTranslationWorkerProgress? worker = _coordinator.GetWorkerProgress().FirstOrDefault(value =>
            value.Status is ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted);
        if (worker is null) return;
        if (ThemedDialog.Show(this,
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
        ThemedDialog.Show(this, ex.Message, "Translation Queue", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _coordinator.ProgressChanged -= OnProgressChanged;
    }

    internal sealed class QueueRow : INotifyPropertyChanged
    {
        private int _order;
        private int _number;
        private string _title = "";
        private string _worker = "—";
        private ProjectTranslationItemStatus _status;
        private string _statusLabel = "";
        private int _completedSegments;
        private int _totalSegments;
        private string _segmentLabel = "";
        private int _errorSegments;
        private string _error = "";

        public QueueRow(ProjectTranslationItem item, string worker = "—")
        {
            ChapterId = item.ChapterId;
            Apply(item, worker);
        }

        public Guid ChapterId { get; }
        public int Order => _order;
        public int Number => _number;
        public string Title => _title;
        public string Worker => _worker;
        public ProjectTranslationItemStatus Status => _status;
        public string StatusLabel => _statusLabel;
        public int CompletedSegments => _completedSegments;
        public int TotalSegments => _totalSegments;
        public string SegmentLabel => _segmentLabel;
        public int ErrorSegments => _errorSegments;
        public string Error => _error;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Apply(ProjectTranslationItem item, string worker)
        {
            Set(ref _order, item.Order, nameof(Order));
            Set(ref _number, item.Number, nameof(Number));
            Set(ref _title, item.Title, nameof(Title));
            Set(ref _worker, worker, nameof(Worker));
            Set(ref _status, item.Status, nameof(Status));
            Set(ref _statusLabel, ItemStatusLabel(item), nameof(StatusLabel));
            Set(ref _completedSegments, item.CompletedSegments, nameof(CompletedSegments));
            Set(ref _totalSegments, item.TotalSegments, nameof(TotalSegments));
            Set(ref _segmentLabel, $"{item.CompletedSegments:N0}/{item.TotalSegments:N0}", nameof(SegmentLabel));
            Set(ref _errorSegments, item.ErrorSegments, nameof(ErrorSegments));
            string detail = string.Join(" · ", new[]
            {
                item.Error,
                item.PreflightWarning,
                EligibilityDiagnosticLabel(item.EligibilityDiagnostics.LastOrDefault())
            }.Where(value => !string.IsNullOrWhiteSpace(value)));
            Set(ref _error, detail, nameof(Error));
        }

        private void Set<T>(ref T field, T value, string propertyName)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    internal sealed record WorkerCard(Guid WorkerId, string Name, ProjectTranslationWorkerStatus Status,
        string BackendLabel, string Model, int ContextSize, int OutputBudget, string Segmentation,
        string? CurrentChapterTitle, int CompletedSegments, int TotalSegments,
        double HanCharactersPerMinute, double TokensPerSecond, long InputTokens, long GeneratedTokens,
        int QualityErrors, decimal EstimatedCost)
    {
        public WorkerCard(ProjectTranslationWorkerProgress progress, ProjectTranslationWorkerState? definition) : this(
            progress.WorkerId, progress.Name, progress.Status,
            definition is null ? "Worker mặc định" : $"{definition.Backend.Kind} · {definition.Backend.ProfileName}",
            definition?.ModelOptions.Model ?? "—", definition?.ModelOptions.ContextSize ?? 0,
            definition?.ModelOptions.MaximumOutputTokens ?? 0,
            definition is null ? "—" : $"{definition.SegmentationOptions.MinimumHanCharacters}/" +
                $"{definition.SegmentationOptions.TargetHanCharacters}/{definition.SegmentationOptions.MaximumHanCharacters}",
            progress.CurrentChapterTitle, progress.CompletedSegments, progress.TotalSegments,
            progress.HanCharactersPerMinute, progress.TokensPerSecond, progress.InputTokens,
            progress.OutputTokens, progress.ErrorSegments + progress.TruncatedOutputCount +
                progress.PossibleMissingContentCount + progress.TimeoutCount, progress.EstimatedCost) { }

        public string StatusLabel => WorkerStatusLabel(Status);
    }

    private static string StatusLabel(ProjectTranslationJobStatus status) => status switch
    {
        ProjectTranslationJobStatus.Draft => "Chưa bắt đầu", ProjectTranslationJobStatus.Ready => "Sẵn sàng",
        ProjectTranslationJobStatus.Running => "Đang dịch", ProjectTranslationJobStatus.PauseRequested => "Đang Pause",
        ProjectTranslationJobStatus.Paused => "Đã Pause", ProjectTranslationJobStatus.Cancelling => "Đang hủy",
        ProjectTranslationJobStatus.Cancelled => "Đã hủy", ProjectTranslationJobStatus.Interrupted => "Cần Resume",
        ProjectTranslationJobStatus.Completed => "Hoàn tất", _ => "Hoàn tất có lỗi"
    };

    private static string ItemStatusLabel(ProjectTranslationItem item) => item.Disposition switch
    {
        ProjectTranslationItemDisposition.AlreadyTranslated => "Đã có bản dịch",
        ProjectTranslationItemDisposition.TranslatedThisJob => "Đã dịch",
        ProjectTranslationItemDisposition.ProtectedContent => "Được bảo vệ",
        ProjectTranslationItemDisposition.MissingEligibility => "Chưa dịch · cần kiểm tra",
        _ => ItemStatusLabel(item.Status)
    };

    private static string ItemStatusLabel(ProjectTranslationItemStatus status) => status switch
    {
        ProjectTranslationItemStatus.Pending => "Đang chờ", ProjectTranslationItemStatus.Running => "Đang dịch",
        ProjectTranslationItemStatus.Paused => "Đã Pause", ProjectTranslationItemStatus.Interrupted => "Bị gián đoạn",
        ProjectTranslationItemStatus.Completed => "Hoàn tất", ProjectTranslationItemStatus.Failed => "Lỗi",
        ProjectTranslationItemStatus.Skipped => "Đã bỏ qua", ProjectTranslationItemStatus.NeedsReview => "Cần kiểm tra nguồn",
        _ => "Đã hủy"
    };

    private static string EligibilityDiagnosticLabel(TranslationEligibilityDiagnostic? diagnostic)
    {
        if (diagnostic is null) return "";
        return $"Eligibility {diagnostic.EligibleSegmentCount:N0}/{diagnostic.SegmentCount:N0}; " +
            $"chưa dịch {diagnostic.UntranslatedSegmentCount:N0}; draft {diagnostic.ProtectedByUserDraftCount:N0}; " +
            $"duyệt {diagnostic.ProtectedByApprovalCount:N0}; bảo vệ sửa {diagnostic.ProtectedByPreviousEditCount:N0}; " +
            $"attempt {diagnostic.ExistingAttemptCount:N0} " +
            $"(lỗi {diagnostic.FailedAttemptCount:N0}, invalid {diagnostic.InvalidOutputAttemptCount:N0}, " +
            $"hủy {diagnostic.CancelledAttemptCount:N0}, gián đoạn {diagnostic.InterruptedAttemptCount:N0}); " +
            $"mode {diagnostic.QueueMode}; preflight {(diagnostic.WorkerPreflightChangedSegmentation ? "có đổi phân đoạn" : "không đổi phân đoạn")}.";
    }

    private static string WorkerStatusLabel(ProjectTranslationWorkerStatus status) => status switch
    {
        ProjectTranslationWorkerStatus.Ready => "Sẵn sàng",
        ProjectTranslationWorkerStatus.Running => "Đang dịch",
        ProjectTranslationWorkerStatus.Pausing => "Đang tạm dừng",
        ProjectTranslationWorkerStatus.Paused => "Đã tạm dừng",
        ProjectTranslationWorkerStatus.Offline => "Mất kết nối",
        ProjectTranslationWorkerStatus.Interrupted => "Cần kết nối lại",
        ProjectTranslationWorkerStatus.Completed => "Hoàn thành",
        _ => "Đã dừng"
    };

    internal sealed record QueueRefreshMeasurement(TimeSpan Elapsed, long AllocatedBytes, int RowObjectCreations);
}
