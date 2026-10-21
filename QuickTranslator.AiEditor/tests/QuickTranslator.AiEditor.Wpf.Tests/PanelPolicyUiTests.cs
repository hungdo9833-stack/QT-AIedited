using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class PanelPolicyUiTests
{
    [Fact]
    public Task Workspace_and_mode_choose_the_expected_docked_panels() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Panel", 1);
        fixture.Attach(chapter);
        fixture.Call("UpdateResponsiveShell", 1600d);

        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        AssertPanels(Visibility.Visible, Visibility.Collapsed, Visibility.Visible);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        AssertPanels(Visibility.Collapsed, Visibility.Visible, Visibility.Visible);

        foreach (WorkspaceId workspace in new[] { WorkspaceId.Settings, WorkspaceId.Resources, WorkspaceId.Reader })
        {
            fixture.Window.NavigateTo(workspace);
            AssertPanels(Visibility.Collapsed, Visibility.Collapsed, Visibility.Collapsed);
        }

        void AssertPanels(Visibility sidebar, Visibility navigator, Visibility inspector)
        {
            Assert.Equal(sidebar, Element("ProjectSidebar").Visibility);
            Assert.Equal(navigator, Element("SegmentNavigator").Visibility);
            Assert.Equal(inspector, Element("TranslationInspector").Visibility);
        }
        FrameworkElement Element(string name) => (FrameworkElement)fixture.Window.FindName(name);
    });

    [Fact]
    public Task Narrow_width_keeps_toggles_and_inspector_opens_as_a_keyboard_dismissible_flyout() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        await fixture.CreateProjectAsync();
        fixture.Attach(await fixture.AddChapterAsync("Flyout", 1));
        fixture.Window.Show();
        fixture.Window.Activate();
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        fixture.Call("UpdateResponsiveShell", 1000d);

        Button sidebarToggle = FindButton("ProjectSidebarToggleButton");
        Button inspectorToggle = FindButton("InspectorToggleButton");
        Assert.Equal(Visibility.Visible, sidebarToggle.Visibility);
        Assert.Equal(Visibility.Visible, inspectorToggle.Visibility);
        Assert.True(sidebarToggle.IsEnabled);
        Assert.True(inspectorToggle.IsEnabled);

        inspectorToggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Dispatcher.Yield(DispatcherPriority.Input);
        var popup = (Popup)fixture.Window.FindName("InspectorFlyout");
        var host = (ContentControl)fixture.Window.FindName("InspectorFlyoutHost");
        Assert.True(popup.IsOpen);
        Assert.Same(fixture.Window.FindName("TranslationInspector"), host.Content);

        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(fixture.Window), 0, Key.Escape)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        fixture.Call("OnPanelFlyoutPreviewKeyDown", fixture.Window.FindName("InspectorFlyoutBorder"), key);
        await Dispatcher.Yield(DispatcherPriority.Input);
        Assert.False(popup.IsOpen);
        Assert.True(inspectorToggle.IsKeyboardFocusWithin);

        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        Button navigatorToggle = FindButton("SegmentNavigatorToggleButton");
        Assert.Equal(Visibility.Visible, navigatorToggle.Visibility);
        Assert.True(navigatorToggle.IsEnabled);

        Button FindButton(string name) => (Button)fixture.Window.FindName(name);
    });

    [Fact]
    public Task Translate_sidebar_preference_is_mode_scoped_and_round_trips() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Attach(await fixture.AddChapterAsync("Preference", 1));
        string path = Path.Combine(fixture.Root, "layout", "panel-layout.json");
        fixture.Set("_workspaceLayoutStore", new WorkspaceLayoutStore(path));
        fixture.Set("_workspaceLayoutReady", true);
        fixture.Call("UpdateResponsiveShell", 1600d);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);

        Assert.True(fixture.Window.HandleShortcut(Key.B, ModifierKeys.Control));
        var layout = fixture.Get<WorkspaceLayoutPreferences>("_workspaceLayout");
        string translateKey = PanelPreferenceKey.Create(WorkspaceId.Editor, EditorMode.Translate, PanelId.ProjectSidebar);
        string reviewKey = PanelPreferenceKey.Create(WorkspaceId.Editor, EditorMode.Review, PanelId.ProjectSidebar);
        Assert.Equal(PanelPreference.PinnedHidden, layout.PanelPreferences![translateKey]);
        Assert.False(layout.PanelPreferences.ContainsKey(reviewKey));

        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)fixture.Window.FindName("ProjectSidebar")).Visibility);
        await fixture.TaskCall("SaveWorkspaceLayoutAsync");
        WorkspaceLayoutPreferences loaded = await new WorkspaceLayoutStore(path).LoadAsync();
        Assert.Equal(PanelPreference.PinnedHidden, loaded.PanelPreferences![translateKey]);
        Assert.False(loaded.PanelPreferences.ContainsKey(reviewKey));
    });

    [Fact]
    public Task Old_layout_json_loads_and_legacy_inspector_false_hides_translate_inspector() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        string path = Path.Combine(fixture.Root, "old-layout.json");
        const string oldJson = """
        {
          "schemaVersion": 1,
          "theme": "Dark",
          "inspectorVisible": false,
          "projectSidebarVisible": true,
          "fourPaneMode": false,
          "inspectorWidth": 300,
          "projectSidebarWidth": 290,
          "twoPaneSourceStar": 1,
          "segmentNavigatorWidth": 250,
          "fourPaneLeftStar": 1,
          "fourPaneTopStar": 1
        }
        """;
        await File.WriteAllTextAsync(path, oldJson, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        WorkspaceLayoutPreferences loaded = await new WorkspaceLayoutStore(path).LoadAsync();
        Assert.Null(loaded.PanelPreferences);
        Assert.False(loaded.InspectorVisible);

        fixture.Attach(TranslationService.CreateChapter("Cũ", "你好。"));
        fixture.Set("_workspaceLayout", loaded);
        fixture.Call("UpdateResponsiveShell", 1600d);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        Assert.Equal(Visibility.Collapsed,
            ((FrameworkElement)fixture.Window.FindName("TranslationInspector")).Visibility);
    });

    [Fact]
    public Task Settings_resize_does_not_restore_the_project_sidebar() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Window.NavigateTo(WorkspaceId.Settings);
        fixture.Call("UpdateResponsiveShell", 1600d);
        Assert.Equal(Visibility.Collapsed,
            ((FrameworkElement)fixture.Window.FindName("ProjectSidebar")).Visibility);
    });

    [Fact]
    public Task Deferred_editor_focus_is_cleared_when_there_is_no_selected_segment() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Window.Show();
        fixture.Window.NavigateTo(WorkspaceId.Settings);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        await Dispatcher.Yield(DispatcherPriority.ContextIdle);
        Assert.Null(fixture.Get<Guid?>("_selectedId"));
        Assert.False(fixture.Get<bool>("_pendingEditorFocus"));
    });
}
