using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Reading;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Tests;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class SimplifiedUiRegressionTests
{
    [Fact]
    public Task Visible_modes_and_shortcuts_round_trip_without_losing_draft_or_reader_navigation() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Ba chế độ", 1);
        fixture.Attach(chapter);
        fixture.Service.UpdateDraft(chapter.Segments[0].Id, "Bản nháp cần giữ.");
        fixture.Set("_readerPreferences", new ReaderPreferences());
        fixture.Set("_readerReady", true);
        fixture.Window.Show();
        RadioButton Mode(string name) => (RadioButton)fixture.Window.FindName(name);
        RadioButton[] modes = [Mode("TwoPaneModeButton"), Mode("FourPaneModeButton"), Mode("ReaderModeButton")];

        for (int iteration = 0; iteration < 3; iteration++)
        {
            modes[2].IsChecked = true;
            AssertMode(2, WorkspaceId.Reader);
            Assert.Equal(Visibility.Visible, ((Grid)fixture.Window.FindName("ReaderView")).Visibility);
            Assert.Equal(Visibility.Visible, ((Border)fixture.Window.FindName("ChapterModeBar")).Visibility);
            Assert.True(fixture.Window.HandleShortcut(Key.D2, ModifierKeys.Control));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            AssertMode(1, WorkspaceId.Editor);
            Assert.Equal(EditorMode.Review, fixture.Window.CurrentEditorMode);
            Assert.True(fixture.Window.HandleShortcut(Key.D1, ModifierKeys.Control));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            AssertMode(0, WorkspaceId.Editor);
            Assert.Equal(Visibility.Visible, ((DockPanel)fixture.Window.FindName("EditorView")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((Grid)fixture.Window.FindName("ReaderView")).Visibility);
            Assert.Equal("Bản nháp cần giữ.", fixture.Service.GetSnapshot().Segments[0].UserDraft);
            foreach (RadioButton mode in modes)
            {
                Assert.Equal(DependencyProperty.UnsetValue, mode.ReadLocalValue(Control.ForegroundProperty));
                Assert.Equal(DependencyProperty.UnsetValue, mode.ReadLocalValue(Control.BackgroundProperty));
            }
        }
        Assert.Equal(0, fixture.Client.Calls);

        void AssertMode(int index, WorkspaceId workspace)
        {
            Assert.Single(modes, mode => mode.IsChecked == true);
            Assert.True(modes[index].IsChecked);
            Assert.Equal(workspace, fixture.Window.CurrentWorkspace);
            Assert.True(NavState.GetIsActive((Button)fixture.Window.FindName("TranslateNavButton")));
        }
    });

    [Fact]
    public Task Open_action_menu_tracks_busy_state_and_keeps_source_editing_reachable() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Attach(await fixture.AddChapterAsync("Menu", 1));
        fixture.Call("Refresh");
        fixture.Window.Show();
        var menu = (MenuItem)fixture.Window.FindName("EditorActionsMenu");
        var edit = (MenuItem)fixture.Window.FindName("EditSourceButton");
        menu.IsSubmenuOpen = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Visible, edit.Visibility);
        Assert.True(edit.IsEnabled);
        Assert.Contains(edit, menu.Items.Cast<object>());
        fixture.Set("_busy", true);
        fixture.Call("Refresh");
        Assert.False(edit.IsEnabled); // Updates while the popup remains open.
        Assert.False(((Button)fixture.Window.FindName("SaveCharactersButton")).IsEnabled);
        fixture.Set("_busy", false);
        fixture.Call("Refresh");
        Assert.True(edit.IsEnabled);
        edit.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(fixture.Get<bool>("_sourceEditMode"));
        Assert.Equal(Visibility.Visible, ((MenuItem)fixture.Window.FindName("CancelSourceEditButton")).Visibility);
        Assert.False(((MenuItem)fixture.Window.FindName("SaveSourceEditButton")).IsEnabled);
    });

    [Fact]
    public Task Resources_default_to_characters_and_keep_unsaved_inputs_across_routes() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Attach(await fixture.AddChapterAsync("Tài nguyên", 1));
        fixture.Call("LoadCharacterProject", new CharacterProject { Name = "Hồ sơ", Profiles =
            [new CharacterProfile { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan" }] });
        fixture.Window.NavigateTo(WorkspaceId.Resources);
        Assert.Equal(Visibility.Visible, ((Grid)fixture.Window.FindName("SettingsCharactersPanel")).Visibility);
        fixture.Text("CharacterProjectNameInput").Text = "Hồ sơ chưa lưu";
        fixture.Text("ContextNotesInput").Text = "Ngữ cảnh chưa lưu";
        var grid = (DataGrid)fixture.Window.FindName("CharactersGrid");
        grid.SelectedIndex = 0;
        fixture.Call("Refresh");
        Assert.True(((Button)fixture.Window.FindName("DeleteCharacterButton")).IsEnabled);
        fixture.Set("_busy", true);
        fixture.Call("Refresh");
        foreach (string name in new[] { "DeleteCharacterButton", "SaveCharactersButton", "PreviewCharactersButton" })
            Assert.False(((Button)fixture.Window.FindName(name)).IsEnabled);
        fixture.Set("_busy", false);
        fixture.Window.ActivateShellSection("dictionary");
        fixture.Window.NavigateTo(WorkspaceId.Settings);
        fixture.Window.NavigateTo(WorkspaceId.Reader);
        fixture.Window.NavigateTo(WorkspaceId.Resources);
        Assert.Equal("Hồ sơ chưa lưu", fixture.Text("CharacterProjectNameInput").Text);
        Assert.Equal("Ngữ cảnh chưa lưu", fixture.Text("ContextNotesInput").Text);
        Assert.Equal(0, fixture.Client.Calls);
    });

    [Theory]
    [InlineData(ProjectTranslationJobStatus.Ready, true, false)]
    [InlineData(ProjectTranslationJobStatus.Running, true, true)]
    [InlineData(ProjectTranslationJobStatus.PauseRequested, true, false)]
    [InlineData(ProjectTranslationJobStatus.Paused, true, true)]
    [InlineData(ProjectTranslationJobStatus.Interrupted, true, true)]
    [InlineData(ProjectTranslationJobStatus.Cancelling, true, false)]
    [InlineData(ProjectTranslationJobStatus.CompletedWithErrors, true, false)]
    [InlineData(ProjectTranslationJobStatus.Completed, false, false)]
    public Task Mini_queue_matches_detailed_progress_and_never_starts_from_details(
        ProjectTranslationJobStatus status, bool visible, bool actionable) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var ids = new List<Guid>();
        for (int i = 1; i <= 4; i++) ids.Add((await fixture.AddChapterAsync("Chương " + i, i)).Id);
        var coordinator = await fixture.TaskCall<ProjectTranslationCoordinator>("EnsureProjectTranslationCoordinatorAsync");
        var store = new ProjectTranslationJobStore(fixture.Project);
        var job = await store.SaveNewAsync(await coordinator.PlanAsync(ids));
        await coordinator.LoadAsync();
        job = coordinator.CurrentJob!;
        job.Status = status;
        job.Items[0].Status = ProjectTranslationItemStatus.Completed;
        job.Items[1].Status = ProjectTranslationItemStatus.Failed;
        job.Items[2].Status = ProjectTranslationItemStatus.Skipped;
        // CurrentJob returns a defensive copy. Inject a synthetic, idle snapshot to cover transient states
        // without launching a real worker (running/pause transitions are exercised separately below).
        typeof(ProjectTranslationCoordinator).GetField("_job", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, job);
        fixture.Call("RefreshShellQueueStatus");
        var bar = (Grid)fixture.Window.FindName("QueueMiniBar");
        var action = (Button)fixture.Window.FindName("QueueMiniActionButton");
        Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, bar.Visibility);
        if (visible)
        {
            Assert.Equal(75d, ((ProgressBar)fixture.Window.FindName("QueueMiniProgress")).Value);
            Assert.Equal(actionable ? Visibility.Visible : Visibility.Collapsed, action.Visibility);
        }
        foreach (WorkspaceId workspace in new[] { WorkspaceId.Resources, WorkspaceId.Reader, WorkspaceId.Editor })
        {
            fixture.Window.NavigateTo(workspace);
            Assert.Equal(visible ? Visibility.Visible : Visibility.Collapsed, bar.Visibility);
        }
        await fixture.TaskCall("OpenQueueStatusAsync");
        Assert.Equal(WorkspaceId.Queue, fixture.Window.CurrentWorkspace);
        Assert.Same(coordinator, fixture.Window.HostedQueueView!.Coordinator);
        Assert.Equal(status, job.Status);
        Assert.Equal(0, fixture.Client.Calls);
    });

    [Fact]
    public Task Mini_queue_pauses_at_checkpoint_and_workspace_changes_do_not_stop_running_job() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Checkpoint", 1);
        var nextChapter = await fixture.AddChapterAsync("Tiếp tục", 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LlmCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Respond = (_, _) => { started.TrySetResult(); return release.Task; };
        await fixture.TaskCall("OpenProjectTranslationQueueAsync", new[] { chapter.Id, nextChapter.Id });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
        fixture.Window.NavigateTo(WorkspaceId.Resources);
        fixture.Window.NavigateTo(WorkspaceId.Reader);
        Assert.True(coordinator.IsRunning);
        fixture.Call("OnQueueMiniToggle", fixture.Window.FindName("QueueMiniActionButton"), new RoutedEventArgs());
        release.TrySetResult(FakeClient.Completion("Bản dịch checkpoint."));
        for (int i = 0; i < 200 && coordinator.IsRunning; i++) await Task.Delay(10);
        Assert.False(coordinator.IsRunning);
        Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob!.Status);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("Tiếp tục", ((Button)fixture.Window.FindName("QueueMiniActionButton")).Content);
        Assert.True(((Button)fixture.Window.FindName("QueueMiniActionButton")).IsEnabled);
        await fixture.TaskCall("OpenQueueStatusAsync");
        Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob.Status);
        Assert.Equal(1, fixture.Client.Calls);
        fixture.Call("OnQueueMiniToggle", fixture.Window.FindName("QueueMiniActionButton"), new RoutedEventArgs());
        for (int i = 0; i < 300; i++)
        {
            await Task.Delay(10);
            coordinator = fixture.Get<ProjectTranslationCoordinator>("_projectTranslationCoordinator");
            if (coordinator.CurrentJob?.Status == ProjectTranslationJobStatus.Completed) break;
        }
        Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
        Assert.Equal(2, fixture.Client.Calls);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(Visibility.Collapsed, ((Grid)fixture.Window.FindName("QueueMiniBar")).Visibility);
    });

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public Task Mini_queue_pause_and_details_keep_all_gpu_workers_on_the_same_job(int workerCount) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var ids = new List<Guid>();
        for (int i = 0; i < workerCount + 1; i++) ids.Add((await fixture.AddChapterAsync("GPU " + i, i + 1)).Id);
        var environment = await fixture.TaskCall<ProjectTranslationEnvironment>("LoadProjectTranslationEnvironmentAsync",
            fixture.Project, CancellationToken.None);
        var releases = Enumerable.Range(0, workerCount)
            .Select(_ => new TaskCompletionSource<LlmCompletion>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var starts = Enumerable.Range(0, workerCount)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var clients = Enumerable.Range(0, workerCount).Select(index => new FakeClient
        {
            Respond = (_, _) => { starts[index].TrySetResult(); return releases[index].Task; }
        }).ToArray();
        var workers = Enumerable.Range(0, workerCount).Select(index => new ProjectTranslationWorkerDefinition(
            Guid.NewGuid(), environment.Backend! with { ProfileId = Guid.NewGuid(), ProfileName = "GPU " + index },
            clients[index], environment.ModelOptions)).ToArray();
        var coordinator = new ProjectTranslationCoordinator(fixture.Project, fixture.Repository, workers,
            _ => Task.FromResult(environment));
        fixture.Set("_projectTranslationCoordinator", coordinator);
        Task run = coordinator.StartAsync(await coordinator.PlanAsync(ids));
        try
        {
            await Task.WhenAll(starts.Select(start => start.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Window.NavigateTo(WorkspaceId.Reader);
            fixture.Window.NavigateTo(WorkspaceId.Resources);
            fixture.Call("RefreshShellQueueStatus");
            Assert.Equal(Visibility.Visible, ((Grid)fixture.Window.FindName("QueueMiniBar")).Visibility);
            await fixture.TaskCall("OpenQueueStatusAsync");
            Assert.Same(coordinator, fixture.Window.HostedQueueView!.Coordinator);
            Assert.Equal(workerCount, coordinator.GetWorkerProgress().Count);
            Assert.True(coordinator.IsRunning);
            fixture.Call("OnQueueMiniToggle", fixture.Window.FindName("QueueMiniActionButton"), new RoutedEventArgs());
        }
        finally
        {
            foreach (var release in releases) release.TrySetResult(FakeClient.Completion("Checkpoint GPU."));
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob!.Status);
        fixture.Call("RefreshShellQueueStatus");
        Assert.Equal(100d * workerCount / (workerCount + 1),
            ((ProgressBar)fixture.Window.FindName("QueueMiniProgress")).Value, 5);
        Assert.Equal(workerCount, clients.Sum(client => client.Calls));
    });

    [Theory]
    [InlineData(900)]
    [InlineData(1180)]
    [InlineData(1600)]
    public Task Wpf_layout_and_render_matrix_preserves_primary_controls(double width) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        await fixture.CreateProjectAsync();
        fixture.Attach(await fixture.AddChapterAsync(new string('W', 120), 1));
        fixture.Window.Width = width;
        fixture.Window.Height = 700;
        fixture.Window.ShowActivated = false;
        fixture.Window.ShowInTaskbar = false;
        fixture.Window.Show();
        fixture.Call("UpdateResponsiveShell", width);
        foreach (var theme in new[] { ShellTheme.Dark, ShellTheme.Light })
        {
            fixture.Call("ApplyShellTheme", theme);
            foreach ((WorkspaceId workspace, EditorMode mode) in new[]
                { (WorkspaceId.Editor, EditorMode.Translate), (WorkspaceId.Editor, EditorMode.Review),
                  (WorkspaceId.Reader, EditorMode.Translate), (WorkspaceId.Resources, EditorMode.Translate),
                  (WorkspaceId.Settings, EditorMode.Translate) })
            {
                fixture.Window.NavigateTo(workspace, mode);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                fixture.Window.UpdateLayout();
                if (workspace == WorkspaceId.Editor)
                {
                    AssertWithin("PrimaryActionButton", "EditorContextHeader");
                    AssertWithin("EditorActionsMenu", "EditorContextHeader");
                    if (mode == EditorMode.Translate)
                    {
                        var header = (FrameworkElement)fixture.Window.FindName("BilingualGridHeaderText");
                        var overflow = (FrameworkElement)fixture.Window.FindName("CommandOverflowToggle");
                        Assert.True(header.TransformToAncestor(fixture.Window).TransformBounds(new Rect(header.RenderSize)).Right <=
                            overflow.TransformToAncestor(fixture.Window).Transform(new Point()).X);
                    }
                }
                if (workspace is WorkspaceId.Editor or WorkspaceId.Reader)
                    AssertWithin("ReaderModeButton", "EditorWorkspaceRoot");
                foreach (int dpi in new[] { 96, 120, 144, 192 })
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(fixture.Window.ActualWidth * dpi / 96),
                        (int)Math.Ceiling(fixture.Window.ActualHeight * dpi / 96), dpi, dpi, PixelFormats.Pbgra32);
                    bitmap.Render(fixture.Window);
                    // Optional visual QA artifacts; raster DPI does not simulate an OS monitor DPI change.
                    string? capture = Environment.GetEnvironmentVariable("QTSTUDIO_UI_CAPTURE_DIR");
                    if (capture is not null && dpi == 96)
                    {
                        Directory.CreateDirectory(capture);
                        using var stream = File.Create(Path.Combine(capture, $"{workspace}-{mode}-{theme}-{width}-{dpi}.png"));
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        encoder.Save(stream);
                    }
                }
            }
        }
        fixture.Call("ApplyShellTheme", ShellTheme.Dark);
        fixture.Window.NavigateTo(WorkspaceId.Editor);
        fixture.Call("OnToggleNavRail", fixture.Window.FindName("NavRailToggleButton"), new RoutedEventArgs());
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        foreach (string name in new[] { "TranslateNavButton", "ResourcesNavButton", "PublishNavButton", "SettingsNavButton" })
        {
            var nav = (Button)fixture.Window.FindName(name);
            Assert.True(NavState.GetIsCollapsed(nav));
            AssertWithin(name, "ShellNavigation");
            Assert.Equal(Visibility.Collapsed, ((ContentPresenter)nav.Template.FindName("ButtonLabel", nav)).Visibility);
        }

        void AssertWithin(string childName, string parentName)
        {
            var child = (FrameworkElement)fixture.Window.FindName(childName);
            var parent = (FrameworkElement)fixture.Window.FindName(parentName);
            Assert.True(child.IsVisible, childName);
            Rect bounds = child.TransformToAncestor(parent).TransformBounds(new Rect(child.RenderSize));
            Assert.True(bounds.Left >= -1 && bounds.Right <= parent.ActualWidth + 1, $"{childName}: {bounds} / {parent.ActualWidth}");
            Assert.True(bounds.Top >= -1 && bounds.Bottom <= parent.ActualHeight + 1, childName);
        }
    });
}
