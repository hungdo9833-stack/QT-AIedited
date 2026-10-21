using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Tests;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class Phase4HostedWorkspaceUiTests : IDisposable
{
    private readonly List<string> _dialogs = [];

    public Phase4HostedWorkspaceUiTests() =>
        ThemedDialog.Responder = dialog => { _dialogs.Add(dialog.Message); return MessageBoxResult.OK; };

    public void Dispose() => ThemedDialog.Responder = null;

    [Fact]
    public Task Chapter_workspace_opens_the_selected_chapter_in_editor_and_exposes_bulk_import() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument first = await fixture.AddChapterAsync("Một", 1);
        ChapterDocument second = await fixture.AddChapterAsync("Hai", 2);
        fixture.Attach(first);

        fixture.Window.NavigateTo(WorkspaceId.Chapters);
        ChapterManagerView view = fixture.Window.HostedChapterManagerView!;
        Assert.True(view.HasImportTxtCommand);
        Assert.Equal(Visibility.Visible, ((Button)view.FindName("ImportTxtButton")).Visibility);
        var grid = (DataGrid)view.FindName("ChapterGrid");
        grid.SelectedItem = view.CurrentRows.Single(row => row.ChapterId == second.Id);
        ((Button)view.FindName("OpenButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        // The manifest is saved before the chapter is attached and the workspace switches: wait for all three.
        for (int count = 0; count < 500 && (fixture.Project.Manifest.CurrentChapterId != second.Id ||
            fixture.Service.GetSnapshot().Id != second.Id || fixture.Window.CurrentWorkspace != WorkspaceId.Editor); count++)
        {
            await Task.Delay(10);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Assert.True(_dialogs.Count == 0, string.Join(" | ", _dialogs));
        Assert.Equal(second.Id, fixture.Project.Manifest.CurrentChapterId);
        Assert.Equal(second.Id, fixture.Service.GetSnapshot().Id);
        Assert.Equal(WorkspaceId.Editor, fixture.Window.CurrentWorkspace);
    });

    [Fact]
    public Task Editor_uses_library_start_screen_without_project_and_hides_it_after_open() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Window.NavigateTo(WorkspaceId.Editor);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.NotNull(fixture.Window.StartScreenView);
        Assert.True(fixture.Window.StartScreenView!.StartScreenMode);
        Assert.Equal(Visibility.Visible, ((ContentControl)fixture.Window.FindName("StartScreenHost")).Visibility);

        await fixture.CreateProjectAsync();
        fixture.Window.NavigateTo(WorkspaceId.Editor);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Collapsed, ((ContentControl)fixture.Window.FindName("StartScreenHost")).Visibility);
    });

    [Fact]
    public Task Running_project_queue_survives_navigation_and_is_dropped_with_the_project_context() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument first = await fixture.AddChapterAsync("Một", 1);
        ChapterDocument second = await fixture.AddChapterAsync("Hai", 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<OllamaCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Respond = (_, _) => { started.TrySetResult(); return release.Task; };

        await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { first.Id, second.Id });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ProjectTranslationCoordinator coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        ProjectTranslationQueueView view = fixture.Window.HostedQueueView!;
        Assert.True(coordinator.IsRunning);
        Assert.Equal(1, view.ProgressSubscriptionCount);
        Assert.True(((Button)view.FindName("PauseButton")).IsEnabled);

        fixture.Window.NavigateTo(WorkspaceId.Editor);
        Assert.True(coordinator.IsRunning);
        Assert.Equal(0, view.ProgressSubscriptionCount);
        Assert.NotEqual("Tạm dừng", ((Button)fixture.Window.FindName("PrimaryActionButton")).Content);

        fixture.Window.NavigateTo(WorkspaceId.Queue);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(view, fixture.Window.HostedQueueView);
        Assert.Equal(1, view.ProgressSubscriptionCount);
        Assert.NotEmpty(view.CurrentRows);

        release.TrySetResult(FakeClient.Completion("Bản dịch hoàn tất."));
        for (int count = 0; count < 200 && coordinator.IsRunning; count++)
        {
            await Task.Delay(10);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Assert.False(coordinator.IsRunning);

        // Closing or switching a project runs these two steps (OnCloseProject, project switch, app close).
        fixture.Window.NavigateTo(WorkspaceId.Chapters);
        Assert.NotNull(fixture.Window.HostedChapterManagerView);
        fixture.Call("DropHostedProjectViews");
        fixture.Call("ClearProjectTranslationCoordinator");
        Assert.True(_dialogs.Count == 0, string.Join(" | ", _dialogs));
        Assert.Null(fixture.Window.HostedQueueView);
        Assert.Null(fixture.Window.HostedChapterManagerView);
        Assert.Equal(0, view.ProgressSubscriptionCount);
    });
}
