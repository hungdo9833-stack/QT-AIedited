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

    private static ChapterManagerCommands NoOpCommands() => new(
        _ => Task.CompletedTask,
        (_, _, _) => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask,
        _ => Task.CompletedTask);

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
