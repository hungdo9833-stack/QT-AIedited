using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ChapterManagerUiTests
{
    [Fact]
    public Task Renders_ten_thousand_manifest_rows_with_recycling_without_chapter_body_reads() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        AddManifestEntries(fixture.Project, 10000);
        await fixture.Repository.SaveManifestAsync(fixture.Project);
        var catalog = fixture.Get<ProjectCatalog>("_projectCatalog");
        var window = new ChapterManagerWindow(fixture.Project, catalog, NoOpCommands());
        try
        {
            Assert.Equal(10000, window.CurrentRows.Count);
            Assert.True(window.UsesRecyclingVirtualization);
            Assert.Equal(10000, ((DataGrid)window.FindName("ChapterGrid")).Items.Count);
            Assert.False(((Button)window.FindName("QueueButton")).IsEnabled);
            Assert.True(((Button)window.FindName("TranslateAllTitlesButton")).IsEnabled);
            Assert.False(((Button)window.FindName("TranslateSelectedTitlesButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Search_filter_multiselect_and_m4_queue_command_are_ready() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        AddManifestEntries(fixture.Project, 50);
        fixture.Project.Manifest.Chapters[24].State = ProjectChapterState.Approved;
        fixture.Project.Manifest.Chapters[24].CompletedSegmentCount = 10;
        await fixture.Repository.SaveManifestAsync(fixture.Project);
        var window = new ChapterManagerWindow(fixture.Project, fixture.Get<ProjectCatalog>("_projectCatalog"), NoOpCommands());
        try
        {
            ((TextBox)window.FindName("SearchInput")).Text = "Chương 25";
            Assert.Single(window.CurrentRows);
            Assert.Equal(25, window.CurrentRows[0].Number);
            ((TextBox)window.FindName("SearchInput")).Clear();
            var grid = (DataGrid)window.FindName("ChapterGrid");
            grid.SelectedItems.Add(grid.Items[0]); grid.SelectedItems.Add(grid.Items[1]);
            Assert.Contains("Đã chọn 2", ((TextBlock)window.FindName("SelectionText")).Text);
            Assert.True(((Button)window.FindName("QueueButton")).IsEnabled);
            Assert.True(((Button)window.FindName("TranslateSelectedTitlesButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task M2_import_commit_appears_automatically_in_open_chapter_manager() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var window = new ChapterManagerWindow(fixture.Project, fixture.Get<ProjectCatalog>("_projectCatalog"), NoOpCommands());
        try
        {
            Assert.Empty(window.CurrentRows);
            string source = string.Concat(Enumerable.Range(1, 5).Select(i => $"第{i}章 标题{i}\n原文{i}。\n"));
            ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source, "five.txt")).Current;
            var task = (Task<BulkImportReport>)fixture.Call("ImportDetectedPlanAsync", plan, null, CancellationToken.None)!;
            BulkImportReport report = await task;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.True(report.Completed);
            Assert.Equal(5, window.CurrentRows.Count);
            Assert.Equal(Enumerable.Range(1, 5), window.CurrentRows.Select(row => row.Number));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Open_chapter_rename_delete_and_restore_keep_editor_and_catalog_consistent() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Tên cũ", 1);
        fixture.Attach(chapter);

        await fixture.TaskCall("RenameProjectChapterAsync", chapter.Id, "Tên mới", "新名");
        Assert.Equal("Tên mới", fixture.Service.GetSnapshot().Title);
        Assert.Equal("Tên mới", fixture.Get<ProjectCatalog>("_projectCatalog").GetSnapshot(fixture.Project)
            .Chapters.Single().VietnameseTitle);

        await fixture.TaskCall("SoftDeleteFromManagerAsync", new[] { chapter.Id });
        Assert.Empty(fixture.Get<ProjectCatalog>("_projectCatalog").Query(fixture.Project, new()));
        Assert.Single(fixture.Get<ProjectCatalog>("_projectCatalog").Query(fixture.Project,
            new(Deletion: ProjectCatalogDeletionFilter.Deleted)));

        await fixture.TaskCall("RestoreFromManagerAsync", new[] { chapter.Id });
        Assert.Single(fixture.Get<ProjectCatalog>("_projectCatalog").Query(fixture.Project, new()));
    });

    [Fact]
    public Task BL17_BL20_Chapter_range_selection_and_command_palette_filter_chapters_and_commands() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        AddManifestEntries(fixture.Project, 50);
        await fixture.Repository.SaveManifestAsync(fixture.Project);
        var window = new ChapterManagerWindow(fixture.Project, fixture.Get<ProjectCatalog>("_projectCatalog"), NoOpCommands());
        try
        {
            int count = window.SelectChapterRange(10, 25);
            Assert.Equal(16, count);
            Assert.Contains("Đã chọn 16", ((TextBlock)window.FindName("SelectionText")).Text);

            var chapterPalette = fixture.Window.GetCommandPaletteItems(commandsMode: false, "Chương 25");
            Assert.Single(chapterPalette);
            Assert.Contains("0025", chapterPalette[0].Title);

            var commandPalette = fixture.Window.GetCommandPaletteItems(commandsMode: true, "Ctrl+Enter");
            Assert.Single(commandPalette);
            Assert.Equal("approve-next", commandPalette[0].Id);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Workspace_view_rehosts_without_duplicate_catalog_subscription() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.AddChapterAsync("Một", 1);
        var view = new ChapterManagerView(fixture.Project, fixture.Get<ProjectCatalog>("_projectCatalog"), NoOpCommands());
        var content = new ContentControl { Content = view };
        var host = TestWindow(content);
        try
        {
            host.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            for (int cycle = 0; cycle < 2; cycle++)
            {
                content.Content = null;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(0, view.CatalogSubscriptionCount);
                content.Content = view;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(1, view.CatalogSubscriptionCount);
            }
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task Selection_primary_tooltips_and_optional_import_follow_workspace_rules() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.AddChapterAsync("Một", 1);
        await fixture.AddChapterAsync("Hai", 2);
        int imports = 0;
        var view = new ChapterManagerView(fixture.Project, fixture.Get<ProjectCatalog>("_projectCatalog"),
            NoOpCommands(() => { imports++; return Task.CompletedTask; }));
        var host = TestWindow(view);
        try
        {
            host.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = (DataGrid)view.FindName("ChapterGrid");
            var open = (Button)view.FindName("OpenButton");
            var queue = (Button)view.FindName("QueueButton");
            var import = (Button)view.FindName("ImportTxtButton");
            Style primary = (Style)Application.Current.FindResource("PrimaryButton");

            grid.SelectedItems.Add(grid.Items[0]);
            Assert.Same(primary, open.Style);
            Assert.NotSame(primary, queue.Style);
            grid.SelectedItems.Add(grid.Items[1]);
            Assert.NotSame(primary, open.Style);
            Assert.Same(primary, queue.Style);
            Assert.Equal(Visibility.Visible, import.Visibility);
            import.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, imports);

            Button[] buttons = [open, queue, (Button)view.FindName("RenameButton"),
                (Button)view.FindName("DeleteButton"), (Button)view.FindName("RestoreButton")];
            Assert.All(buttons.Where(button => button.IsVisible && !button.IsEnabled), button =>
                Assert.False(string.IsNullOrWhiteSpace(button.ToolTip?.ToString())));
        }
        finally { host.Close(); }

        var withoutImport = new ChapterManagerView(fixture.Project,
            fixture.Get<ProjectCatalog>("_projectCatalog"), NoOpCommands());
        Assert.Equal(Visibility.Collapsed, ((Button)withoutImport.FindName("ImportTxtButton")).Visibility);
    });

    private static Window TestWindow(object content) => new()
    {
        Content = content, Width = 1000, Height = 700, ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000
    };

    private static ChapterManagerCommands NoOpCommands(Func<Task>? importTxt = null) => new(
        _ => Task.CompletedTask,
        (_, _, _) => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        importTxt);

    private static void AddManifestEntries(ProjectSession session, int count)
    {
        DateTimeOffset updated = DateTimeOffset.UtcNow;
        for (int index = 0; index < count; index++)
        {
            int number = index + 1;
            Guid id = Guid.NewGuid();
            session.Manifest.Chapters.Add(new()
            {
                ChapterId = id,
                Number = number,
                VietnameseTitle = "Chương " + number,
                ChineseTitle = "第" + number + "章",
                RelativePath = $"Chapters/{id:D}.qtaie.json",
                State = ProjectChapterState.Untranslated,
                SegmentCount = 10,
                UpdatedAt = updated.AddSeconds(index)
            });
        }
    }
}
