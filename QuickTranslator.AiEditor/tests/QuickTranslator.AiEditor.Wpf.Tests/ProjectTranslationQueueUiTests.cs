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
            Assert.True(measurement.Elapsed < TimeSpan.FromMilliseconds(750),
                $"Refresh {chapterCount:N0} rows took {measurement.Elapsed.TotalMilliseconds:N1} ms.");
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
        var release = new TaskCompletionSource<OllamaCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        var release = new TaskCompletionSource<OllamaCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        Assert.Equal(Visibility.Visible, ((Border)fixture.Window.FindName("QueueResumeBanner")).Visibility);
        Assert.Contains("Còn", ((TextBlock)fixture.Window.FindName("QueueResumeSummaryText")).Text);
        Assert.Equal("Hàng đợi · có thể tiếp tục", ((Button)fixture.Window.FindName("ShellQueueButton")).Content);
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
        var releaseSecond = new TaskCompletionSource<OllamaCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
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
