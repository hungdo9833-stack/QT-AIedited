using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ProductionShellUiTests
{
    [Fact]
    public Task Translation_workspace_defaults_to_two_panes_and_preserves_draft_when_switching_four_panes() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        await fixture.CreateProjectAsync();
        ChapterDocument chapter = await fixture.AddChapterAsync("Chương UI", 1);
        fixture.Attach(chapter);
        fixture.Call("Refresh");

        Assert.True(((RadioButton)fixture.Window.FindName("EditorModeButton")).IsChecked);
        Assert.Equal(Visibility.Visible, ((DockPanel)fixture.Window.FindName("EditorView")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((Grid)fixture.Window.FindName("ReaderView")).Visibility);
        Assert.Equal(0, ((TabControl)fixture.Window.FindName("EditorTabs")).SelectedIndex);

        fixture.Call("ShowTranslationWorkspace", true);
        Assert.Equal(1, ((TabControl)fixture.Window.FindName("EditorTabs")).SelectedIndex);
        TextBox draft = fixture.Text("DraftText");
        draft.Text = "Bản Việt chỉnh trong chế độ bốn ô.";
        Guid selected = fixture.Get<Guid?>("_selectedId")!.Value;

        fixture.Call("ShowTranslationWorkspace", false);
        Assert.Equal(0, ((TabControl)fixture.Window.FindName("EditorTabs")).SelectedIndex);
        Assert.Equal(selected, fixture.Get<Guid?>("_selectedId"));
        object block = ((ItemsControl)fixture.Window.FindName("ChapterBlocksItems")).Items[0];
        Assert.Equal("Bản Việt chỉnh trong chế độ bốn ô.", block.GetType().GetProperty("Text",
            BindingFlags.Instance | BindingFlags.Public)!.GetValue(block));
    });

    [Fact]
    public Task Inspector_uses_current_project_glossary_context_and_characters() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        await fixture.CreateProjectAsync();
        ChapterDocument chapter = await fixture.AddChapterAsync("Inspector", 1);
        fixture.Call("SetGlossaryTerms", (object)new[] { new ConfirmedTerm("王秀兰", "Vương Tú Lan") });
        fixture.Text("ContextNotesInput").Text = "Bối cảnh tổng hợp M5";
        fixture.Call("LoadCharacterProject", new CharacterProject { Id = fixture.Project.Manifest.ProjectId, Name = "M5",
            Profiles = [new() { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan", PrimaryNarrativePronoun = "nàng" }] });
        fixture.Attach(chapter);
        fixture.Call("Refresh");

        Assert.Single(((ItemsControl)fixture.Window.FindName("InspectorGlossaryItems")).Items);
        Assert.Single(((ItemsControl)fixture.Window.FindName("InspectorCharacterItems")).Items);
        Assert.Contains("Bối cảnh tổng hợp M5", ((TextBlock)fixture.Window.FindName("InspectorContextText")).Text);
        Assert.Contains("王秀兰", ((TextBlock)fixture.Window.FindName("InspectorDictionaryText")).Text);
    });

    [Fact]
    public Task Workspace_layout_round_trips_without_project_schema_changes() => StaHost.Run(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-m5-layout-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new WorkspaceLayoutStore(Path.Combine(root, "layout.json"));
            var expected = new WorkspaceLayoutPreferences { Theme = ShellTheme.Light, InspectorVisible = false,
                FourPaneMode = true, InspectorWidth = 360, TwoPaneSourceStar = 1.4, SegmentNavigatorWidth = 280 };
            await store.SaveAsync(expected);
            WorkspaceLayoutPreferences loaded = await store.LoadAsync();
            Assert.Equal(ShellTheme.Light, loaded.Theme);
            Assert.False(loaded.InspectorVisible);
            Assert.True(loaded.FourPaneMode);
            Assert.Equal(360, loaded.InspectorWidth);
            Assert.DoesNotContain(Directory.EnumerateFiles(root),
                path => path.EndsWith(".qtproj", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    });

    [Fact]
    public Task Library_cards_measure_at_full_hd_and_2k_workspaces() => StaHost.Run(() =>
    {
        var cards = Enumerable.Range(1, 12).Select(index => new ProjectLibraryCard(Guid.NewGuid(),
            "Truyện tổng hợp " + index, "Tác giả", ProjectStatus.Translating, $@"C:\Synthetic\{index}.qtproj",
            null, 100, index, index / 12d, "Chương " + index, DateTimeOffset.UtcNow, false, null)).ToArray();
        var window = new ProjectLibraryWindow(cards, cards[0].ProjectId);
        try
        {
            window.Measure(new Size(1180, 720)); window.Arrange(new Rect(0, 0, 1180, 720)); window.UpdateLayout();
            Assert.True(((ListBox)window.FindName("ProjectList")).ActualWidth <= 1180);
            window.Measure(new Size(1600, 900)); window.Arrange(new Rect(0, 0, 1600, 900)); window.UpdateLayout();
            Assert.Equal(12, ((ListBox)window.FindName("ProjectList")).Items.Count);
            Assert.True(((Button)window.FindName("ContinueButton")).IsEnabled);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Dark_and_light_themes_update_shell_and_child_windows() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var cards = new[] { new ProjectLibraryCard(Guid.NewGuid(), "Truyện theme", "",
            ProjectStatus.Editing, @"C:\Synthetic\theme.qtproj", null, 10, 2, 0.2,
            "Chương 2", DateTimeOffset.UtcNow, false, null) };
        var library = new ProjectLibraryWindow(cards, cards[0].ProjectId);
        try
        {
            fixture.Call("ApplyShellTheme", ShellTheme.Light);
            Assert.Equal(Color.FromRgb(244, 247, 251), ((SolidColorBrush)fixture.Window.Background).Color);
            Assert.Equal(Color.FromRgb(244, 247, 251), ((SolidColorBrush)library.Background).Color);
            Assert.Equal("Dark", ((Button)fixture.Window.FindName("ThemeToggleButton")).Content);

            fixture.Call("ApplyShellTheme", ShellTheme.Dark);
            Assert.Equal(Color.FromRgb(7, 17, 31), ((SolidColorBrush)fixture.Window.Background).Color);
            Assert.Equal(Color.FromRgb(7, 17, 31), ((SolidColorBrush)library.Background).Color);
        }
        finally
        {
            fixture.Call("ApplyShellTheme", ShellTheme.Dark);
            library.Close();
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Full_hd_at_150_percent_collapses_catalog_and_restores_it_when_space_returns() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Call("UpdateResponsiveShell", 960d);
        Assert.Equal(Visibility.Collapsed, ((Border)fixture.Window.FindName("ProjectSidebar")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((Border)fixture.Window.FindName("TranslationInspector")).Visibility);
        Assert.Equal(148, ((Border)fixture.Window.FindName("ShellNavigation")).Width);
        Assert.Equal(190, ((ComboBox)fixture.Window.FindName("ProjectSwitcher")).Width);
        Assert.Equal(Visibility.Collapsed,
            ((Button)fixture.Window.FindName("ProjectSidebarToggleButton")).Visibility);
        Assert.True(fixture.Window.MinWidth <= 960);
        Assert.True(fixture.Window.MinHeight <= 540);

        await (Task)fixture.Call("SaveWorkspaceLayoutAsync")!;

        fixture.Call("UpdateResponsiveShell", 1280d);
        Assert.Equal(Visibility.Collapsed, ((Border)fixture.Window.FindName("ProjectSidebar")).Visibility);
        Assert.Equal("Hiện mục lục", ((Button)fixture.Window.FindName("ProjectSidebarToggleButton")).Content);

        fixture.Call("UpdateResponsiveShell", 1600d);
        Assert.Equal(Visibility.Visible, ((Border)fixture.Window.FindName("ProjectSidebar")).Visibility);
        Assert.Equal(Visibility.Visible, ((Border)fixture.Window.FindName("TranslationInspector")).Visibility);
        Assert.Equal(Visibility.Visible,
            ((Button)fixture.Window.FindName("ProjectSidebarToggleButton")).Visibility);
        Assert.Equal("Ẩn mục lục", ((Button)fixture.Window.FindName("ProjectSidebarToggleButton")).Content);
    });

    [Fact]
    public Task M2_import_to_M3_catalog_to_M4_plan_opens_the_same_chapter_in_M5_editor() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        await fixture.CreateProjectAsync();
        string source = string.Concat(Enumerable.Range(1, 5)
            .Select(index => $"第{index}章 标题{index}\n原文{index}。\n"));
        ImportPreviewPlan plan = ImportPreviewEditor.Create(
            new ChapterDetector().Detect(source, "m5-flow.txt")).Current;
        var importTask = (Task<BulkImportReport>)fixture.Call(
            "ImportDetectedPlanAsync", plan, null, CancellationToken.None)!;
        BulkImportReport report = await importTask;
        Assert.True(report.Completed);

        ProjectCatalog catalog = fixture.Get<ProjectCatalog>("_projectCatalog");
        ProjectCatalogSnapshot snapshot = catalog.GetSnapshot(fixture.Project);
        Assert.Equal(5, snapshot.Chapters.Count());
        Guid[] queued = snapshot.Chapters.Take(3).Select(chapter => chapter.ChapterId).ToArray();
        await fixture.TaskCall("OpenProjectTranslationQueueAsync", queued);
        var queueWindow = fixture.Get<ProjectTranslationQueueWindow>("_projectTranslationWindow");
        try
        {
            Assert.Equal(3, queueWindow.CurrentRows.Count);
            Assert.Equal(0, fixture.Client.Calls);
            await fixture.TaskCall("LoadProjectChapterAsync", snapshot.Chapters[0].ChapterId);
            fixture.Call("ShowTranslationWorkspace", false);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.Equal(0, ((TabControl)fixture.Window.FindName("EditorTabs")).SelectedIndex);
            Assert.Contains("原文1", fixture.Text("ChapterInput").Text);
            Assert.Contains("标题1", ((TextBlock)fixture.Window.FindName("ShellChapterText")).Text);
            Assert.Equal(snapshot.Chapters[0].ChapterId, fixture.Service.GetSnapshot().Id);
        }
        finally { queueWindow.Close(); }
    });

    [Fact]
    public Task Rapid_layout_changes_are_serialized_without_file_lock_errors() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        string path = Path.Combine(fixture.Root, "layout", "workspace-layout.json");
        fixture.Set("_workspaceLayoutStore", new WorkspaceLayoutStore(path));
        fixture.Set("_workspaceLayoutReady", true);
        Task[] saves = Enumerable.Range(0, 12)
            .Select(_ => (Task)fixture.Call("SaveWorkspaceLayoutAsync")!).ToArray();
        await Task.WhenAll(saves);

        WorkspaceLayoutPreferences loaded = await new WorkspaceLayoutStore(path).LoadAsync();
        Assert.Equal(ShellTheme.Dark, loaded.Theme);
        Assert.DoesNotContain("Chưa lưu được bố cục", ((TextBlock)fixture.Window.FindName("StatusText")).Text,
            StringComparison.OrdinalIgnoreCase);
    });
}
