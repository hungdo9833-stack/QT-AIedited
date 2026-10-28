using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Tests;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ProjectTranslationQueueUiTests
{
    [Fact]
    public Task Queueing_a_chapter_while_the_queue_is_paused_appends_it() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LlmCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Respond = (call, _) =>
        {
            if (call == 1) { started.TrySetResult(); return release.Task; }
            return Task.FromResult(FakeClient.Completion("Bản dịch tiếp theo."));
        };
        await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { first.Id });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        Task pause = coordinator.PauseAsync();
        release.TrySetResult(FakeClient.Completion("Checkpoint."));
        await pause;
        Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob?.Status);

        await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { second.Id });

        coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator"); // Rebuilt for the saved job.
        ProjectTranslationItem appended = Assert.Single(coordinator.CurrentJob!.Items, item => item.ChapterId == second.Id);
        Assert.Equal(2, appended.Order);
        Assert.Equal(ProjectTranslationItemStatus.Pending, appended.Status);
        Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob.Status); // Waits for Tiếp tục.
    });

    [Theory]
    [InlineData(MessageBoxResult.Yes, 2)]
    [InlineData(MessageBoxResult.No, 1)]
    [InlineData(MessageBoxResult.Cancel, 1)]
    public Task Queueing_a_translated_chapter_asks_whether_to_retranslate_it(MessageBoxResult answer, int expectedCalls) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Một", 1);
        var questions = new List<string>();
        ThemedDialog.Responder = dialog => { questions.Add(dialog.Message); return answer; };
        try
        {
            async Task RunQueueAsync()
            {
                await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { chapter.Id });
                ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
                for (int count = 0; count < 200 && coordinator.IsRunning; count++) await Task.Delay(20);
            }

            await RunQueueAsync();
            Assert.Empty(questions); // Nothing translated yet: no question.
            Assert.Equal(1, fixture.Client.Calls);

            await RunQueueAsync();

            Assert.Contains("đã có bản dịch", Assert.Single(questions), StringComparison.Ordinal);
            Assert.Equal(expectedCalls, fixture.Client.Calls); // Yes translates the chapter again; No skips it; Cancel queues nothing.
        }
        finally { ThemedDialog.Responder = null; }
    });


    [Fact]
    public Task Queueing_an_error_chapter_without_completed_segments_offers_full_retranslation() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument chapter = await AddFailedChapterAsync(fixture);
        ProjectChapterEntry entry = Assert.Single(fixture.Project.Manifest.Chapters);
        Assert.Equal(ProjectChapterState.Error, entry.State);
        Assert.Equal(0, entry.CompletedSegmentCount);
        var questions = new List<string>();
        ThemedDialog.Responder = dialog => { questions.Add(dialog.Message); return MessageBoxResult.Cancel; };
        try
        {
            await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { chapter.Id });

            Assert.Contains("Dịch lại toàn bộ?", Assert.Single(questions), StringComparison.Ordinal);
            Assert.Equal(0, fixture.Client.Calls);
            Assert.Null(fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator").CurrentJob);
        }
        finally { ThemedDialog.Responder = null; }
    });

    [Theory]
    [InlineData(ProjectTranslationItemStatus.Pending, MessageBoxResult.Yes)]
    [InlineData(ProjectTranslationItemStatus.Pending, MessageBoxResult.No)]
    [InlineData(ProjectTranslationItemStatus.Pending, MessageBoxResult.Cancel)]
    [InlineData(ProjectTranslationItemStatus.Interrupted, MessageBoxResult.Yes)]
    public Task Queueing_a_waiting_error_chapter_offers_full_retranslation(
        ProjectTranslationItemStatus itemStatus, MessageBoxResult answer) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument chapter = await AddFailedChapterAsync(fixture);
        ProjectTranslationCoordinator initial = await fixture.TaskCall<ProjectTranslationCoordinator>(
            "EnsureProjectTranslationCoordinatorAsync");
        var store = new ProjectTranslationJobStore(fixture.Project);
        ProjectTranslationJob saved = await store.SaveNewAsync(await initial.PlanAsync([chapter.Id]));
        saved.Status = ProjectTranslationJobStatus.Paused;
        saved.Items[0].Status = itemStatus;
        saved.Items[0].ChapterQueueRunId = chapter.Queue.RunId;
        await store.SaveAsync(saved);
        await initial.LoadAsync();
        var questions = new List<string>();
        ThemedDialog.Responder = dialog => { questions.Add(dialog.Message); return answer; };
        try
        {
            await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { chapter.Id });

            Assert.Contains("Dịch lại toàn bộ?", Assert.Single(questions), StringComparison.Ordinal);
            ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
            ProjectTranslationItem item = Assert.Single(coordinator.CurrentJob!.Items);
            Assert.Equal(0, fixture.Client.Calls);
            Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob.Status);
            Assert.Equal(answer == MessageBoxResult.Yes, item.RetranslateAll);
            if (answer == MessageBoxResult.Yes)
            {
                Assert.Null(item.ChapterQueueRunId);
                Assert.Equal(ProjectTranslationItemStatus.Pending, item.Status);
                await coordinator.ResumeAsync();
                Assert.Equal(1, fixture.Client.Calls);
                Assert.Equal(ProjectTranslationItemStatus.Completed, Assert.Single(coordinator.CurrentJob!.Items).Status);
            }
            else
            {
                Assert.Equal(chapter.Queue.RunId, item.ChapterQueueRunId);
                Assert.Equal(itemStatus, item.Status);
            }
        }
        finally { ThemedDialog.Responder = null; }
    });

    [Fact]
    public Task Adding_chapters_while_running_keeps_coordinator_and_owned_worker_client() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument first = await fixture.AddChapterAsync("Một", 1);
        ChapterDocument second = await fixture.AddChapterAsync("Hai", 2);
        ProjectTranslationEnvironment environment = await fixture.TaskCall<ProjectTranslationEnvironment>(
            "LoadProjectTranslationEnvironmentAsync", fixture.Project, CancellationToken.None);
        var client = new BlockingDisposableClient();
        var worker = new ProjectTranslationWorkerDefinition(Guid.NewGuid(), environment.Backend!, client, environment.ModelOptions);
        var coordinator = new ProjectTranslationCoordinator(fixture.Project, fixture.Repository, [worker],
            _ => Task.FromResult(environment));
        fixture.Set("_projectTranslationCoordinator", coordinator);
        List<IDisposable> owned = fixture.Get<List<IDisposable>>("_projectTranslationOwnedClients");
        owned.Add(client);
        Task run = coordinator.StartAsync(await coordinator.PlanAsync([first.Id]));
        try
        {
            await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { second.Id });

            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.TaskCall<ProjectTranslationCoordinator>(
                "RebuildParallelCoordinatorAsync", coordinator.CurrentJob, true, true, false));
            Assert.Same(coordinator, fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator"));
            Assert.True(coordinator.IsRunning);
            Assert.False(client.Disposed);
            Assert.Same(client, Assert.Single(owned));
            Assert.Equal(first.Id, Assert.Single(coordinator.CurrentJob!.Items).ChapterId);
            Assert.Equal("Hàng đợi đang dịch; hãy Tạm dừng rồi thêm chương.",
                ((TextBlock)fixture.Window.FindName("StatusText")).Text);

            // Clearing (e.g. while the window closes) must not throw nor dispose the running worker's client.
            fixture.Call("ClearProjectTranslationCoordinator");
            Assert.True(coordinator.IsRunning);
            Assert.False(client.Disposed);
        }
        finally
        {
            client.Release.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
    });

    private static async Task<ChapterDocument> AddFailedChapterAsync(WorkspaceFixture fixture)
    {
        ChapterDocument chapter = await fixture.AddChapterAsync("Lỗi", 1);
        TranslationSegment segment = Assert.Single(chapter.Segments);
        segment.Status = SegmentStatus.Error;
        segment.Attempts.Add(new TranslationAttempt
        {
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1), FinishedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.Failed, ModelOptions = new(), PromptVersion = "p0-synthetic",
            Prompt = "synthetic", Error = "synthetic failure"
        });
        chapter.Queue = new QueueCheckpoint
        {
            RunId = Guid.NewGuid(), Status = QueueStatus.Interrupted, Mode = QueueMode.MissingSegments,
            CandidateIds = [segment.Id], NextIndex = 1
        };
        await fixture.Repository.UpdateChapterAsync(fixture.Project, chapter);
        return chapter;
    }

    private sealed class BlockingDisposableClient : ILlmClient, IDisposable
    {
        public bool Disposed { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            ObjectDisposedException.ThrowIf(Disposed, this);
            return FakeClient.Completion("Bản dịch.");
        }

        public void Dispose() => Disposed = true;
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1000)]
    [InlineData(5000)]
    public Task Queue_refresh_reuses_rows_and_preserves_selection_for_large_projects(int chapterCount) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.TaskCall("EnsureProjectTranslationCoordinatorAsync");
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        ProjectTranslationJob plan = SyntheticPlan(fixture.Project.Manifest.ProjectId, chapterCount);
        var window = new ProjectTranslationQueueWindow("Benchmark tổng hợp", coordinator, plan);
        try
        {
            var grid = (DataGrid)window.FindName("QueueGrid");
            object source = grid.ItemsSource;
            int selectedIndex = chapterCount / 2;
            grid.SelectedIndex = selectedIndex;
            object selected = grid.SelectedItem;
            ProjectTranslationQueueView.QueueRow selectedRow = window.CurrentRows[selectedIndex];

            _ = window.MeasureRefresh(); // warm up templates, formatters and JIT
            ProjectTranslationQueueView.QueueRefreshMeasurement measurement = window.MeasureRefresh();

            Assert.Same(source, grid.ItemsSource);
            Assert.Same(selected, grid.SelectedItem);
            Assert.Same(selectedRow, window.CurrentRows[selectedIndex]);
            Assert.Equal(chapterCount, window.RowObjectCreationCount);
            // Row reuse and allocations are deterministic; wall-clock time depends on machine load and belongs in
            // tools/QuickTranslator.AiEditor.QueueWorkflow.Benchmark, so it is not asserted here.
            Assert.True(measurement.AllocatedBytes < 8_000_000,
                $"Refresh {chapterCount:N0} rows allocated {measurement.AllocatedBytes:N0} bytes.");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Chapter_manager_selection_starts_saved_queue_without_second_start_click() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapters = new[]
        {
            await fixture.AddChapterAsync("Một", 1),
            await fixture.AddChapterAsync("Hai", 2),
            await fixture.AddChapterAsync("Ba", 3)
        };

        await fixture.TaskCall("OpenProjectTranslationQueueAsync", chapters.Select(chapter => chapter.Id).ToArray());
        var window = fixture.Window.HostedQueueView!;

        Assert.Equal(3, window.CurrentRows.Count);
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        for (int count = 0; count < 100 && (coordinator.CurrentJob is null || coordinator.IsRunning); count++)
        {
            await Task.Delay(20);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        await Task.Delay(150);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob?.Status);
        Assert.Equal(3, fixture.Client.Calls);
        Assert.NotEqual(Guid.Empty, coordinator.CurrentJob!.JobId);
        Assert.All(window.CurrentRows, row => Assert.Equal("Đã dịch", row.StatusLabel));
        ((DataGrid)window.FindName("QueueGrid")).SelectedIndex = 0;
        await Dispatcher.Yield(DispatcherPriority.DataBind);
        Assert.Contains("Eligibility", ((TextBlock)window.FindName("SelectedDiagnosticText")).Text,
            StringComparison.Ordinal);
        Assert.False(((Button)window.FindName("StartButton")).IsEnabled);
        Assert.Contains("3/3", ((TextBlock)window.FindName("SummaryText")).Text);
    });

    [Fact]
    public Task Running_project_queue_blocks_editor_open_and_context_switch_pauses_safely() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LlmCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Respond = (call, _) =>
        {
            if (call == 1) { started.TrySetResult(); return release.Task; }
            return Task.FromResult(FakeClient.Completion("Bản dịch tiếp theo."));
        };
        await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { first.Id, second.Id });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.TaskCall("LoadProjectChapterAsync", second.Id));
        Task pauseForSwitch = fixture.TaskCall("WaitForCurrentWorkAsync");
        release.TrySetResult(FakeClient.Completion("Checkpoint trước khi chuyển."));
        await pauseForSwitch;

        Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob?.Status);
        Assert.False(coordinator.IsRunning);
        Assert.NotNull((await fixture.Repository.LoadChapterAsync(fixture.Project, first.Id)).Chapter.Segments[0].LatestAiAttempt);
    });

    [Fact]
    public Task Start_button_keeps_pause_and_cancel_available_while_request_is_running() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LlmCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Respond = (_, _) =>
        {
            started.TrySetResult();
            return release.Task;
        };

        await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { first.Id, second.Id });
        var window = fixture.Window.HostedQueueView!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.True(((Button)window.FindName("PauseButton")).IsEnabled);
        Assert.True(((Button)window.FindName("CancelButton")).IsEnabled);

        ((Button)window.FindName("PauseButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
        release.TrySetResult(FakeClient.Completion("Checkpoint trước khi Pause."));
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        for (int count = 0; count < 100 && coordinator.IsRunning; count++)
        {
            await Task.Delay(20);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob?.Status);
        Assert.False(coordinator.IsRunning);
        fixture.Call("RefreshShellQueueStatus");
        // The mini queue is the visible pause/resume control across workspaces.
        var queueButton = (Button)fixture.Window.FindName("QueueMiniActionButton");
        Assert.Null(fixture.Window.FindName("QueueResumeBanner"));
        Assert.Equal("Tiếp tục", queueButton.Content);
        Assert.True(queueButton.IsEnabled);
        Assert.Contains("Đã tạm dừng", ((TextBlock)fixture.Window.FindName("QueueMiniSummary")).Text);
    });

    [Fact]
    public Task BL15_Completed_and_non_queued_chapters_can_be_opened_and_edited_while_project_queue_runs_in_background() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        var third = await fixture.AddChapterAsync("Ba", 3);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource<LlmCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Respond = (call, _) =>
        {
            if (call == 1) return Task.FromResult(FakeClient.Completion("Chương một đã dịch xong."));
            secondStarted.TrySetResult();
            return releaseSecond.Task;
        };

        await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { first.Id, second.Id });
        var window = fixture.Window.HostedQueueView!;
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(coordinator.IsRunning);
        Assert.True(coordinator.IsChapterLockedByRunningQueue(second.Id));
        Assert.False(coordinator.IsChapterLockedByRunningQueue(first.Id));
        Assert.False(coordinator.IsChapterLockedByRunningQueue(third.Id));

        // Open and approve completed chapter 1 while chapter 2 is still actively translating in background
        await window.OpenChapterRowAsync(first.Id);
        Assert.Equal(first.Id, fixture.Service.GetSnapshot().Id);
        await fixture.Window.ApproveSegmentCoreAsync(advanceToNext: false);
        Assert.Equal(SegmentStatus.Approved, fixture.Service.GetSnapshot().Segments[0].Status);

        releaseSecond.TrySetResult(FakeClient.Completion("Chương hai dịch xong."));
        for (int count = 0; count < 100 && coordinator.IsRunning; count++)
        {
            await Task.Delay(20);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob?.Status);
    });

    [Fact]
    public Task BL16_ShowTextCleanerWindow_opens_modeless_without_locking_editor() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        ChapterDocument chapter = TranslationService.CreateChapter("Chương modeless", "老公说了一句话。");
        chapter.Segments[0].UserDraft = "Còn sót chữ 老公 trong bản dịch.";
        fixture.Attach(chapter);
        fixture.Window.ShowInTaskbar = false;
        fixture.Window.Left = -30000;
        fixture.Window.Show();

        fixture.Call("ShowTextCleanerWindow", true);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        Assert.False(fixture.Get<bool>("_uiOperation"));
        Assert.Single(fixture.Window.OwnedWindows);
        var cleaner = fixture.Window.OwnedWindows.Cast<Window>().Single();
        Assert.True(cleaner.IsVisible);
        cleaner.Close();
    });

    [Fact]
    public Task Queue_workspace_rehosts_without_duplicate_progress_subscription() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.TaskCall("EnsureProjectTranslationCoordinatorAsync");
        var view = new ProjectTranslationQueueView("Workspace test",
            fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator"), null);
        var content = new ContentControl { Content = view };
        var host = TestWindow(content);
        try
        {
            host.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, view.ProgressSubscriptionCount);
            content.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, view.ProgressSubscriptionCount);
            content.Content = view;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, view.ProgressSubscriptionCount);
            content.Content = null;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, view.ProgressSubscriptionCount);
            content.Content = view;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(1, view.ProgressSubscriptionCount);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task Run_controls_are_exclusive_primary_and_cancel_is_only_in_overflow() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.TaskCall("EnsureProjectTranslationCoordinatorAsync");
        var view = new ProjectTranslationQueueView("State matrix",
            fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator"), null);
        var host = TestWindow(view);
        try
        {
            host.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var start = (Button)view.FindName("StartButton");
            var resume = (Button)view.FindName("ResumeButton");
            var pause = (Button)view.FindName("PauseButton");
            Style primary = (Style)Application.Current.FindResource("PrimaryButton");
            AssertRunState(view, start, resume, pause, primary, true, false, ProjectTranslationJobStatus.Draft, start);
            AssertRunState(view, start, resume, pause, primary, false, true, ProjectTranslationJobStatus.Running, pause);
            AssertRunState(view, start, resume, pause, primary, false, false, ProjectTranslationJobStatus.Paused, resume);
            AssertRunState(view, start, resume, pause, primary, false, false, ProjectTranslationJobStatus.Interrupted, resume);
            Assert.Equal(Visibility.Collapsed, ((Button)view.FindName("CancelButton")).Visibility);
        }
        finally { host.Close(); }
    });

    private static void AssertRunState(ProjectTranslationQueueView view, Button start, Button resume, Button pause,
        Style primary, bool planMode, bool running, ProjectTranslationJobStatus status, Button expected)
    {
        view.UpdateRunControlVisibility(planMode, running, status);
        Button[] visible = new[] { start, resume, pause }
            .Where(button => button.Visibility == Visibility.Visible).ToArray();
        Assert.Single(visible);
        Assert.Same(expected, visible[0]);
        Assert.Single(visible, button => ReferenceEquals(button.Style, primary));
        expected.IsEnabled = false;
        Assert.True(ToolTipService.GetShowOnDisabled(expected));
        Assert.False(string.IsNullOrWhiteSpace(expected.ToolTip?.ToString()));
        expected.IsEnabled = true;
    }

    private static Window TestWindow(object content) => new()
    {
        Content = content, Width = 1000, Height = 700, ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
    };

    private static ProjectTranslationJob SyntheticPlan(Guid projectId, int chapterCount)
    {
        const string fingerprint = "0000000000000000000000000000000000000000000000000000000000000000";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ProjectTranslationJob
        {
            ProjectId = projectId,
            Status = ProjectTranslationJobStatus.Draft,
            CreatedAt = now,
            UpdatedAt = now,
            Items = Enumerable.Range(1, chapterCount).Select(number => new ProjectTranslationItem
            {
                ChapterId = Guid.NewGuid(),
                Number = number,
                Order = number,
                Title = "Chương tổng hợp " + number,
                SourceFingerprint = fingerprint,
                ProtectedFingerprint = fingerprint,
                TotalSegments = 12,
                CompletedSegments = number % 12
            }).ToList()
        };
    }
}
