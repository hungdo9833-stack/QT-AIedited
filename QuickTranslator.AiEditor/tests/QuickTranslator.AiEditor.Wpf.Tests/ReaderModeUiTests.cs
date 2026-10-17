using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Reading;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ReaderModeUiTests
{
    [Fact]
    public Task Reader_catalog_search_and_previous_next_use_project_catalog_chapter_ids() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument first = await fixture.AddChapterAsync("Mưa đêm", 1);
        ChapterDocument second = await fixture.AddChapterAsync("Sơn môn", 2);
        await fixture.AddChapterAsync("Bắc cảnh", 3);
        fixture.Attach(first);
        EnableReader(fixture);

        fixture.Call("RefreshReaderCatalog");
        var list = (ListBox)fixture.Window.FindName("ReaderChapterList");
        Assert.Equal(3, list.Items.Count);

        ((TextBox)fixture.Window.FindName("ReaderChapterSearchInput")).Text = "Sơn môn";
        fixture.Call("RefreshReaderCatalog");
        Assert.Single(list.Items);

        ((TextBox)fixture.Window.FindName("ReaderChapterSearchInput")).Clear();
        fixture.Call("RefreshReaderCatalog");
        await fixture.TaskCall("NavigateReaderChapterAsync", 1);
        Assert.Equal(second.Id, fixture.Service.GetSnapshot().Id);
    });

    [Fact]
    public Task Open_in_editor_targets_the_reader_segment_without_losing_the_draft() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        string source = string.Join("。", Enumerable.Repeat("甲乙丙丁戊己庚辛壬癸", 10)) + "。";
        var chapter = TranslationService.CreateChapter("Hai đoạn", source,
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 12, MaximumHanCharacters = 20 });
        Assert.True(chapter.Segments.Count >= 2);
        foreach (TranslationSegment segment in chapter.Segments)
            segment.UserDraft = $"Bản dịch {segment.Order}.";
        await fixture.Repository.AddChapterAsync(fixture.Project, chapter, 1);
        chapter = (await fixture.Repository.LoadChapterAsync(fixture.Project, chapter.Id)).Chapter;
        fixture.Attach(chapter);
        EnableReader(fixture);
        fixture.Call("RefreshReader", chapter);
        ReaderDocument reader = fixture.Get<ReaderDocument>("_readerDocument");
        ReaderSegmentSpan target = reader.Spans[1];

        fixture.Call("OpenReaderOffsetInEditor", target.Start + 2);

        Assert.Equal(target.SegmentId, fixture.Get<Guid?>("_selectedId"));
        Assert.Equal(Visibility.Collapsed, ((Grid)fixture.Window.FindName("ReaderView")).Visibility);
        Assert.Equal(Visibility.Visible, ((DockPanel)fixture.Window.FindName("EditorView")).Visibility);
        Assert.Equal($"Bản dịch {target.Order}.", fixture.Service.GetSnapshot().Segments
            .Single(segment => segment.Id == target.SegmentId).UserDraft);
    });

    [Fact]
    public Task Equal_text_in_two_chapters_rebuilds_reader_mapping_for_the_current_chapter() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument first = await fixture.AddChapterAsync("Một", 1);
        ChapterDocument second = await fixture.AddChapterAsync("Hai", 2);
        first.Segments[0].UserDraft = second.Segments[0].UserDraft = "Cùng một bản dịch.";
        fixture.Attach(first);
        EnableReader(fixture);
        fixture.Call("RefreshReader", first);

        fixture.Attach(second);
        fixture.Call("RefreshReader", second);
        var ranges = (System.Collections.IEnumerable)fixture.Get<object>("_readerRuns");
        object range = ranges.Cast<object>().First();
        var run = (Run)range.GetType().GetProperty("Run")!.GetValue(range)!;

        Assert.Equal(second.Segments[0].Id, run.Tag);
        Assert.Equal(second.Id, fixture.Get<Guid?>("_renderedReaderChapterId"));
    });

    [Fact]
    public Task Compact_reader_keeps_primary_commands_and_hides_optional_chrome() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        EnableReader(fixture);

        fixture.Call("UpdateResponsiveShell", 960d);

        Assert.Equal(235, ((ColumnDefinition)fixture.Window.FindName("ReaderCatalogColumn")).Width.Value);
        Assert.Equal(Visibility.Collapsed, ((Button)fixture.Window.FindName("ReaderBookmarkButton")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((Button)fixture.Window.FindName("FocusModeButton")).Visibility);
        Assert.Equal(Visibility.Visible, ((Button)fixture.Window.FindName("ReaderCatalogToggleButton")).Visibility);

        fixture.Call("UpdateResponsiveShell", 1600d);
        Assert.Equal(270, ((ColumnDefinition)fixture.Window.FindName("ReaderCatalogColumn")).Width.Value);
        Assert.Equal(Visibility.Visible, ((Button)fixture.Window.FindName("ReaderBookmarkButton")).Visibility);
        return Task.CompletedTask;
    });

    private static void EnableReader(WorkspaceFixture fixture)
    {
        fixture.Set("_readerPreferences", new ReaderPreferences());
        fixture.Set("_readerReady", true);
        ((RadioButton)fixture.Window.FindName("ReaderModeButton")).IsChecked = true;
        fixture.Call("LoadReaderAppearanceControls");
        fixture.Call("ApplyReaderAppearance");
    }
}
