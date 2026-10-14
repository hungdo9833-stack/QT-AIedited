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
    public Task Chapter_manager_selection_opens_plan_without_sending_request_then_start_completes() => StaHost.Run(async () =>
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
        var window = fixture.Get<ProjectTranslationQueueWindow>("_projectTranslationWindow");

        Assert.Equal(3, window.CurrentRows.Count);
        Assert.True(((Button)window.FindName("StartButton")).IsEnabled);
        Assert.False(((Button)window.FindName("ReplanButton")).IsEnabled);
        Assert.Equal(0, fixture.Client.Calls);
        ((Button)window.FindName("StartButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        for (int count = 0; count < 100 && (coordinator.CurrentJob is null || coordinator.IsRunning); count++)
        {
            await Task.Delay(20);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }

        Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob?.Status);
        Assert.Equal(3, fixture.Client.Calls);
        Assert.Contains("3/3", ((TextBlock)window.FindName("SummaryText")).Text);
        window.Close();
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
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        ProjectTranslationJob plan = await coordinator.PlanAsync(new[] { first.Id, second.Id });
        Task run = coordinator.StartAsync(plan);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.TaskCall("LoadProjectChapterAsync", second.Id));
        Task pauseForSwitch = fixture.TaskCall("WaitForCurrentWorkAsync");
        release.TrySetResult(FakeClient.Completion("Checkpoint trước khi chuyển."));
        await pauseForSwitch;
        await run;

        Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob?.Status);
        Assert.False(coordinator.IsRunning);
        Assert.NotNull((await fixture.Repository.LoadChapterAsync(fixture.Project, first.Id)).Chapter.Segments[0].LatestAiAttempt);
        fixture.Get<ProjectTranslationQueueWindow>("_projectTranslationWindow").Close();
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
        var window = fixture.Get<ProjectTranslationQueueWindow>("_projectTranslationWindow");
        ((Button)window.FindName("StartButton")).RaiseEvent(new System.Windows.RoutedEventArgs(Button.ClickEvent));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
        window.Close();
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
        var window = fixture.Get<ProjectTranslationQueueWindow>("_projectTranslationWindow");
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        ProjectTranslationJob plan = await coordinator.PlanAsync(new[] { first.Id, second.Id });
        Task run = coordinator.StartAsync(plan);
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
        await run;
        Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob?.Status);
        window.Close();
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
}
