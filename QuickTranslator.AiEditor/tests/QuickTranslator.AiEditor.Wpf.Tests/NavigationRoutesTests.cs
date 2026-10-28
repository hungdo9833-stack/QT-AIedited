using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class NavigationRoutesTests
{
    private static readonly string[] RequiredPaletteIds =
    [
        "open-resources-dictionary", "open-resources-characters", "open-resources-prompts",
        "open-settings-ai", "open-settings-data", "open-settings-appearance", "open-library", "open-queue",
        "open-review", "open-editor", "edit-source", "split-segment", "merge-segment", "move-boundary",
        "structure-history", "restore-structure", "segment-history", "show-prompt", "batch-replace",
        "export-chapter", "toggle-ai-compare", "undo-pronouns", "apply-pronouns"
    ];

    [Fact]
    public Task Every_in_window_route_has_one_matching_active_navigation_item() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var expected = new Dictionary<WorkspaceId, (string Nav, string Root)>
        {
            [WorkspaceId.Editor] = ("TranslateNavButton", "EditorWorkspaceRoot"),
            [WorkspaceId.Chapters] = ("ChaptersNavButton", "ChaptersWorkspaceRoot"),
            [WorkspaceId.Queue] = ("QueueNavButton", "QueueWorkspaceRoot"),
            [WorkspaceId.Publish] = ("PublishNavButton", "PublishWorkspaceRoot"),
            [WorkspaceId.Reader] = ("TranslateNavButton", "EditorWorkspaceRoot"),
            [WorkspaceId.Resources] = ("ResourcesNavButton", "ResourcesWorkspaceRoot"),
            [WorkspaceId.Settings] = ("SettingsNavButton", "SettingsWorkspaceRoot")
        };
        string[] names = ["TranslateNavButton", "ChaptersNavButton", "QueueNavButton", "PublishNavButton",
            "ReaderNavButton", "ResourcesNavButton", "SettingsNavButton"];
        string[] roots = ["EditorWorkspaceRoot", "ChaptersWorkspaceRoot", "QueueWorkspaceRoot",
            "PublishWorkspaceRoot", "ResourcesWorkspaceRoot", "SettingsWorkspaceRoot"];

        foreach ((WorkspaceId route, (string activeName, string rootName)) in expected)
        {
            fixture.Window.NavigateTo(route);
            Assert.Equal(route, fixture.Window.CurrentWorkspace);
            Button[] active = names.Select(name => (Button)fixture.Window.FindName(name))
                .Where(NavState.GetIsActive).ToArray();
            Assert.Single(active);
            Assert.Same(fixture.Window.FindName(activeName), active[0]);
            FrameworkElement[] visibleRoots = roots.Select(name => (FrameworkElement)fixture.Window.FindName(name))
                .Where(root => root.Visibility == Visibility.Visible).ToArray();
            Assert.Single(visibleRoots);
            Assert.Same(fixture.Window.FindName(rootName), visibleRoots[0]);
        }
        Assert.Equal(Enum.GetValues<WorkspaceId>().Length, names.Length);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Phase_four_routes_are_hosted_in_the_main_window() => StaHost.Run(async () =>
    {
        using (var fixture = new WorkspaceFixture())
        {
            fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
            fixture.Window.NavigateTo(WorkspaceId.Queue);
            fixture.Window.NavigateTo(WorkspaceId.Publish);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(WorkspaceId.Publish, fixture.Window.CurrentWorkspace);
            Assert.True(NavState.GetIsActive((Button)fixture.Window.FindName("PublishNavButton")));
        }

        using var projectFixture = new WorkspaceFixture();
        await projectFixture.CreateProjectAsync();
        projectFixture.Window.Show();
        projectFixture.Window.NavigateTo(WorkspaceId.Chapters);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        ChapterManagerView manager = projectFixture.Window.HostedChapterManagerView!;
        Assert.NotNull(manager);
        Assert.Same(manager, ((ContentControl)projectFixture.Window.FindName("ChaptersWorkspaceHost")).Content);
        Assert.Equal(WorkspaceId.Chapters, projectFixture.Window.CurrentWorkspace);
        Assert.True(NavState.GetIsActive((Button)projectFixture.Window.FindName("ChaptersNavButton")));
    });

    [Fact]
    public Task Sidebar_and_semantic_sections_match_phase_two_architecture() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        string[] labels = VisualChildren<Button>((DependencyObject)fixture.Window.FindName("ShellNavigation"))
            .Select(button => button.Content?.ToString() ?? "").ToArray();
        Assert.DoesNotContain("Nhập TXT", labels);
        Assert.DoesNotContain("Thư viện", labels);
        Assert.Equal(2, ((TabControl)fixture.Window.FindName("EditorTabs")).Items.Count);

        fixture.Window.ActivateShellSection("characters");
        Assert.Equal(WorkspaceId.Resources, fixture.Window.CurrentWorkspace);
        Assert.Equal("Characters", ((ListBoxItem)((ListBox)fixture.Window.FindName("ResourcesNavigation")).SelectedItem).Tag);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Refreshing_virtualized_rows_does_not_steal_focus_from_chapter_search() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        fixture.Window.Show();
        fixture.Window.Activate();
        await fixture.CreateProjectAsync();
        ChapterDocument chapter = await fixture.AddChapterAsync("Focus", 1);
        fixture.Attach(chapter);
        fixture.Get<WorkspaceLayoutPreferences>("_workspaceLayout").ProjectSidebarVisible = true;
        fixture.Call("ApplyProjectSidebarVisibility");
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        ((ItemsControl)fixture.Window.FindName("ChapterBlocksItems")).UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.Input);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.False(fixture.Get<bool>("_pendingEditorFocus"));

        TextBox search = fixture.Text("ChapterSearchInput");
        search.Focus();
        Keyboard.Focus(search);
        Assert.True(search.IsKeyboardFocusWithin);
        fixture.Call("Refresh");
        ((ItemsControl)fixture.Window.FindName("ChapterBlocksItems")).UpdateLayout();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(search.IsKeyboardFocusWithin);
    });

    [Fact]
    public Task Settings_dirty_state_and_resource_text_survive_workspace_switches() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Window.NavigateTo(WorkspaceId.Resources);
        TextBox context = fixture.Text("ContextNotesInput");
        context.Text = "Ghi chú chưa lưu qua nhiều workspace.";
        Assert.True(fixture.Get<bool>("_resourcesDirtyValue"));

        fixture.Call("ShowSettingsWorkspace", "Appearance");
        fixture.Call("ShowResourcesWorkspace", "Dictionary");
        Assert.True(fixture.Get<bool>("_resourcesDirtyValue"));
        Assert.Equal("Ghi chú chưa lưu qua nhiều workspace.", context.Text);
        Assert.False(fixture.Get<bool>("_configurationDirtyValue"));
        Assert.Equal("Mọi thay đổi đã được lưu", ((TextBlock)fixture.Window.FindName("SettingsDirtyText")).Text);
        Assert.Equal("Có thay đổi chưa lưu", ((TextBlock)fixture.Window.FindName("ResourcesDirtyText")).Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Command_palette_contains_every_progressively_disclosed_route_and_command() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        string[] ids = fixture.Window.GetCommandPaletteItems(true, "").Select(item => item.Id).ToArray();
        Assert.All(RequiredPaletteIds, id => Assert.Contains(id, ids));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Legacy_shortcuts_dispatch_to_semantic_routes_and_handlers() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        ChapterDocument chapter = TranslationService.CreateChapter("Phím tắt", "你好。世界。");
        TranslationSegment first = chapter.Segments[0];
        first.Status = SegmentStatus.AwaitingReview;
        first.UserDraft = "Bản nháp một.";
        const string secondSource = "再见。";
        var second = new TranslationSegment { Id = TextSegmenter.CreateStableId(chapter.Id, first.SourceText.Length, secondSource),
            Order = 2, StartUtf16 = first.SourceText.Length, SourceText = secondSource,
            HanCharacterCount = TextSegmenter.CountHanCharacters(secondSource), Status = SegmentStatus.Error };
        chapter.Segments.Add(second);
        chapter.OriginalText += secondSource;
        chapter.SelectedSegmentId = first.Id;
        fixture.Attach(chapter);

        Assert.True(fixture.Window.HandleShortcut(Key.D2, ModifierKeys.Control));
        Assert.Equal(EditorMode.Review, fixture.Window.CurrentEditorMode);
        Assert.True(fixture.Window.HandleShortcut(Key.D1, ModifierKeys.Control));
        Assert.Equal(EditorMode.Translate, fixture.Window.CurrentEditorMode);

        WorkspaceLayoutPreferences layout = fixture.Get<WorkspaceLayoutPreferences>("_workspaceLayout");
        Assert.True(fixture.Window.HandleShortcut(Key.B, ModifierKeys.Control));
        Assert.Equal(PanelPreference.PinnedShown,
            layout.PanelPreferences![PanelPreferenceKey.Create(WorkspaceId.Editor, EditorMode.Translate, PanelId.ProjectSidebar)]);
        Assert.True(fixture.Window.HandleShortcut(Key.I, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Equal(PanelPreference.PinnedHidden,
            layout.PanelPreferences![PanelPreferenceKey.Create(WorkspaceId.Editor, EditorMode.Translate, PanelId.Inspector)]);

        Assert.True(fixture.Window.HandleShortcut(Key.Down, ModifierKeys.Alt));
        Assert.Equal(second.Id, fixture.Get<Guid?>("_selectedId"));
        Assert.True(fixture.Window.HandleShortcut(Key.Up, ModifierKeys.Alt));
        Assert.Equal(first.Id, fixture.Get<Guid?>("_selectedId"));
        Assert.True(fixture.Window.HandleShortcut(Key.F8, ModifierKeys.None));
        Assert.Equal(second.Id, fixture.Get<Guid?>("_selectedId"));
        fixture.Window.NavigateSegment(-1);
        Assert.True(fixture.Window.HandleShortcut(Key.Return, ModifierKeys.Control));
        for (int i = 0; i < 20 && fixture.Get<Guid?>("_selectedId") != second.Id; i++)
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal(second.Id, fixture.Get<Guid?>("_selectedId"));

        Assert.True(fixture.Window.HandleShortcut(Key.P, ModifierKeys.Control, false));
        Assert.False(fixture.Window.LastPaletteCommandsMode);
        Assert.True(fixture.Window.HandleShortcut(Key.P, ModifierKeys.Control | ModifierKeys.Shift, false));
        Assert.True(fixture.Window.LastPaletteCommandsMode);
    });

    [Fact]
    public Task Pending_draft_survives_all_in_window_workspaces_until_autosave_tick() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Window.Show();
        ChapterDocument chapter = TranslationService.CreateChapter("Autosave routes", "你好。世界。");
        fixture.Attach(chapter);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        fixture.Text("DraftText").Text = "Bản nháp đi qua mọi workspace.";
        Assert.True(fixture.Service.HasUnsavedChanges);

        fixture.Window.NavigateTo(WorkspaceId.Settings);
        fixture.Window.NavigateTo(WorkspaceId.Resources);
        fixture.Window.NavigateTo(WorkspaceId.Reader);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        Assert.Equal("Bản nháp đi qua mọi workspace.", fixture.Text("DraftText").Text);
        Assert.True(fixture.Service.HasUnsavedChanges);

        fixture.Call("OnAutosave", null, EventArgs.Empty);
        for (int i = 0; i < 40 && fixture.Service.HasUnsavedChanges; i++)
        {
            await Task.Delay(10);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        Assert.False(fixture.Service.HasUnsavedChanges);
    });

    private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
}
