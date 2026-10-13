using System.IO;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Projects;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ImportPreviewUiTests
{
    [Fact]
    public Task Dark_preview_loads_detector_output_and_blocks_project_number_collisions() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.AddChapterAsync("Đã có", 1);
        string path = Path.Combine(fixture.Root, "multi.txt");
        await File.WriteAllTextAsync(path, "版权信息\r\n第1章 冲突\r\n甲。\r\n第2章 新章\r\n乙。");
        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await window.LoadSourceAsync(path, SourceTextEncoding.Auto);
            Assert.Equal(2, window.CurrentPlan!.Chapters.Count());
            Assert.Equal("版权信息\r\n第1章 冲突\r\n甲。\r\n第2章 新章\r\n乙。", window.CurrentPlan.ReconstructSource());
            Assert.False(((Button)window.FindName("ConfirmButton")).IsEnabled);
            Assert.NotNull(window.FindName("TranslateTitlesCheck"));
            Assert.NotNull(window.FindName("TitleMethodInput"));
            Assert.NotNull(window.FindName("TranslateAllTitlesButton"));
            Assert.NotNull(window.FindName("RetryFailedTitlesButton"));
            Assert.NotNull(window.FindName("RetrySelectedTitlesButton"));
            Assert.Contains("Số 1", ((TextBlock)window.FindName("FooterHintText")).Text +
                string.Join(" ", ((ListBox)window.FindName("IssuesList")).Items.Cast<object>()), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(System.Windows.Media.Color.FromRgb(7, 17, 31),
                ((System.Windows.Media.SolidColorBrush)window.Background).Color);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MainWindow_production_import_path_persists_fifty_chapters_and_reopens_project() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        string source = string.Concat(Enumerable.Range(1, 50).Select(i => $"第{i}章 标题{i}\n原文{i}。\n"));
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source, "fifty.txt")).Current;

        var task = (Task<BulkImportReport>)fixture.Call("ImportDetectedPlanAsync", plan, null, CancellationToken.None)!;
        BulkImportReport report = await task;

        Assert.True(report.Completed);
        Assert.Equal(50, report.ImportedCount);
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(fixture.Project.ManifestPath)).Session;
        Assert.Equal(50, reopened.Manifest.ChapterCount);
        Assert.Empty(reopened.RecoveryReport.Orphans);
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Equal(source, plan.ReconstructSource());
    });

    [Fact]
    public Task Preview_requires_explicit_legacy_encoding_without_replacement_characters() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        string path = Path.Combine(fixture.Root, "gbk.txt");
        await File.WriteAllBytesAsync(path, [0xb5, 0xda, 0x31, 0xd5, 0xc2, 0x0a, 0xd6, 0xd0, 0xce, 0xc4, 0xa1, 0xa3]);
        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await Assert.ThrowsAsync<SourceDecodingException>(() => window.LoadSourceAsync(path, SourceTextEncoding.Auto));
            await window.LoadSourceAsync(path, SourceTextEncoding.Gbk);
            Assert.DoesNotContain('\uFFFD', window.CurrentPlan!.Source.Text);
            Assert.Equal("GBK (CP936)", window.CurrentPlan.Source.EncodingName);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Preview_displays_unordered_chapters_by_number_and_reports_automatic_duplicate_resolution() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        string path = Path.Combine(fixture.Root, "unordered.txt");
        await File.WriteAllTextAsync(path,
            "第3章 三\n正文三。\n第1章 一（前）\n正文一前。\n第2章 二\n正文二。\n第1章 一（后）\n正文一后。\n");
        var window = new ImportPreviewWindow(fixture.Project);
        try
        {
            await window.LoadSourceAsync(path, SourceTextEncoding.Auto);

            Assert.Equal([1, 2, 3], window.CurrentPlan!.ImportableChapters.Select(chapter => chapter.Number));
            Assert.Equal(1, window.CurrentPlan.AutomaticallySkippedDuplicateCount);
            Assert.True(((Button)window.FindName("ConfirmButton")).IsEnabled);
            Assert.Contains("tự sắp xếp", ((TextBlock)window.FindName("PreviewStatusText")).Text,
                StringComparison.CurrentCultureIgnoreCase);
            Assert.Contains("bỏ qua 1 bản trùng", ((TextBlock)window.FindName("PreviewStatusText")).Text,
                StringComparison.CurrentCultureIgnoreCase);

            var grid = (DataGrid)window.FindName("PreviewGrid");
            int?[] displayedNumbers = grid.Items.Cast<object>()
                .Select(row => row.GetType().GetProperty("NumberLabel")?.GetValue(row)?.ToString())
                .Where(value => int.TryParse(value, out _))
                .Select(value => (int?)int.Parse(value!)).ToArray();
            Assert.Equal([1, 1, 2, 3], displayedNumbers);
        }
        finally { window.Close(); }
    });
}
