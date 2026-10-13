using System.Windows.Controls;
using System.Windows.Threading;
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
}
