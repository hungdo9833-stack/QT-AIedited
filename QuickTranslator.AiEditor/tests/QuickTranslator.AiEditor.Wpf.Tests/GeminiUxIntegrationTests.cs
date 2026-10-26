using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Traditional;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class GeminiUxIntegrationTests
{
    [Fact]
    public void Editor_split_uses_the_caret_at_a_sentence_boundary_or_suggests_the_nearest_one()
    {
        const string source = "第一句。第二句。第三句。";
        int firstBoundary = TextSegmenter.GetSentenceBoundaries(source)[0];

        MainWindow.SplitBoundaryDecision exact = MainWindow.ResolveSplitBoundary(source, firstBoundary);
        Assert.Equal(firstBoundary, exact.Boundary);
        Assert.False(exact.RequiresConfirmation);

        MainWindow.SplitBoundaryDecision suggested = MainWindow.ResolveSplitBoundary(source, firstBoundary - 2);
        Assert.Equal(firstBoundary, suggested.Boundary);
        Assert.True(suggested.RequiresConfirmation);
        Assert.DoesNotContain("UTF-16", suggested.Preview, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Source_edit_only_requires_a_blocking_confirmation_when_translation_data_is_at_risk()
    {
        SourceEditPreview safe = Preview(protectedSegments: 0, replacesMost: false);
        SourceEditPreview protectedEdit = Preview(protectedSegments: 2, replacesMost: false);
        SourceEditPreview replacement = Preview(protectedSegments: 0, replacesMost: true);

        Assert.False(MainWindow.RequiresSourceEditConfirmation(safe));
        Assert.True(MainWindow.RequiresSourceEditConfirmation(protectedEdit));
        Assert.True(MainWindow.RequiresSourceEditConfirmation(replacement));
    }

    [Fact]
    public Task Responsive_shell_and_section_navigation_use_the_real_workspace_controls() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Attach(await fixture.AddChapterAsync("Responsive", 1));

        fixture.Call("UpdateResponsiveShell", 1600d);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)fixture.Window.FindName("ProjectSidebar")).Visibility);
        Assert.Equal(Visibility.Visible, ((FrameworkElement)fixture.Window.FindName("TranslationInspector")).Visibility);
        fixture.Call("UpdateResponsiveShell", 1020d);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)fixture.Window.FindName("ProjectSidebar")).Visibility);
        Assert.Equal(Visibility.Collapsed, ((FrameworkElement)fixture.Window.FindName("TranslationInspector")).Visibility);

        fixture.Call("UpdateResponsiveShell", 1600d);
        fixture.Call("ActivateShellSection", "character");
        Assert.Equal(WorkspaceId.Resources, fixture.Window.CurrentWorkspace);
        Assert.Equal("Characters", ((ListBoxItem)((ListBox)fixture.Window.FindName("ResourcesNavigation")).SelectedItem).Tag);
        fixture.Call("ActivateShellSection", "reader");
        Assert.Equal(WorkspaceId.Reader, fixture.Window.CurrentWorkspace);
        fixture.Call("ActivateShellSection", "translate");
        Assert.Equal(WorkspaceId.Editor, fixture.Window.CurrentWorkspace);
    });

    [Fact]
    public Task Combo_items_are_readable_and_autosave_does_not_replace_the_primary_error() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        Assert.All(((ComboBox)fixture.Window.FindName("ProjectSwitcher")).Items.Cast<object>(), item =>
            Assert.DoesNotContain("ProjectSwitchItem {", item.ToString(), StringComparison.Ordinal));

        ChapterDocument chapter = await fixture.AddChapterAsync("Chương kiểm thử UX", 1);
        chapter.Segments[0].Attempts.Add(new TranslationAttempt
        {
            AiText = "Bản dịch thử nghiệm", ModelOptions = new() { Model = "fake-model" },
            Prompt = "prompt", StartedAt = DateTimeOffset.UtcNow, Outcome = AttemptOutcome.Completed
        });
        fixture.Attach(chapter);
        fixture.Call("Refresh");
        object? history = ((ComboBox)fixture.Window.FindName("HistoryList")).SelectedItem;
        Assert.NotNull(history);
        Assert.Contains("AI lần 1", history!.ToString());

        fixture.Call("ShowError", new InvalidOperationException("Lỗi giả lập cần giữ."));
        fixture.Call("SetAutosaveStatus", "✓ Đã tự lưu", false);
        Assert.StartsWith("Lỗi:", ((TextBlock)fixture.Window.FindName("StatusText")).Text);
        Assert.Contains("Đã tự lưu", ((TextBlock)fixture.Window.FindName("AutosaveStatusText")).Text);
    });

    [Fact]
    public Task Approve_and_advance_moves_to_the_next_segment_and_modal_bounds_are_clamped() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = TranslationService.CreateChapter("Hai đoạn",
            string.Join("。", Enumerable.Repeat("甲乙丙丁戊己庚辛壬癸", 8)) + "。",
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 12, MaximumHanCharacters = 20 });
        Assert.True(chapter.Segments.Count >= 2);
        chapter.Segments[0].UserDraft = "Đoạn 1 đã dịch.";
        chapter.Segments[1].UserDraft = "Đoạn 2 đã dịch.";
        chapter.Segments[1].Status = SegmentStatus.Error;
        await fixture.Repository.AddChapterAsync(fixture.Project, chapter, 1);
        chapter = (await fixture.Repository.LoadChapterAsync(fixture.Project, chapter.Id)).Chapter;
        fixture.Attach(chapter);
        fixture.Set("_selectedId", chapter.Segments[0].Id);
        fixture.Call("Refresh");

        await fixture.Window.ApproveSegmentCoreAsync(advanceToNext: true);
        Assert.Equal(SegmentStatus.Approved, fixture.Service.GetSnapshot().Segments[0].Status);
        Assert.Equal(chapter.Segments[1].Id, fixture.Get<Guid?>("_selectedId"));
        Assert.True(fixture.Window.JumpToNextIssueSegment());

        var modal = new Window { Width = 1380, Height = 860, MinWidth = 900, MinHeight = 650 };
        MainWindow.ClampWindowToWorkArea(modal, 1380, 860, 960, 500);
        Assert.True(modal.Width <= 944);
        Assert.True(modal.Height <= 484);
        Assert.True(modal.MinHeight <= 484);
    });

    [Fact]
    public Task Four_pane_layout_expands_visible_panes_and_filters_segments_by_state() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        var chapter = TranslationService.CreateChapter("Chương lọc đoạn",
            string.Join("。", Enumerable.Repeat("甲乙丙丁戊己庚辛壬癸", 8)) + "。",
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 12, MaximumHanCharacters = 20 });
        chapter.Segments[0].UserDraft = "Đoạn đã duyệt.";
        chapter.Segments[0].Status = SegmentStatus.Approved;
        chapter.Segments[1].UserDraft = "Đoạn lỗi.";
        chapter.Segments[1].Status = SegmentStatus.Error;
        fixture.Attach(chapter);
        fixture.Call("ShowTranslationWorkspace", true);

        fixture.Get<HashSet<TraditionalPane>>("_hiddenTraditionalPanes").Add(TraditionalPane.HanViet);
        fixture.Call("ApplyTraditionalPaneLayout");
        Assert.Equal(3, Grid.GetColumnSpan((UIElement)fixture.Window.FindName("ChinesePane")));

        var filter = (ComboBox)fixture.Window.FindName("SegmentStateFilter");
        var segmentList = (ListBox)fixture.Window.FindName("SegmentList");
        filter.SelectedIndex = 3;
        fixture.Call("Refresh");
        Assert.Single(segmentList.Items);
        filter.SelectedIndex = 0;
        fixture.Call("Refresh");
        Assert.Equal(chapter.Segments.Count, segmentList.Items.Count);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Bilingual_grid_updates_draft_in_place_and_live_selection_updates_inspector() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương song ngữ", "苏雨看着林哲。");
        fixture.Attach(chapter);
        fixture.Call("Refresh");

        var items = (ItemsControl)fixture.Window.FindName("ChapterBlocksItems");
        object first = items.Items[0]!;
        Assert.Equal("苏雨看着林哲。", first.GetType().GetProperty("SourceText")!.GetValue(first));
        first.GetType().GetProperty("Text")!.SetValue(first, "Tô Vũ nhìn Lâm Triết.");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Same(first, items.Items[0]);
        Assert.Equal("Tô Vũ nhìn Lâm Triết.", fixture.Service.GetSnapshot().Segments[0].EditableText);

        fixture.Call("UpdateInspectorLiveSelection", "苏雨");
        Assert.Equal(Visibility.Visible,
            ((FrameworkElement)fixture.Window.FindName("InspectorLiveSelectionBorder")).Visibility);
        Assert.Contains("【苏雨】", ((TextBlock)fixture.Window.FindName("InspectorLiveSelectionText")).Text);
    });

    [Fact]
    public Task Gemini_key_entry_is_available_in_main_settings_without_PowerShell() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var pass = (TextBox)fixture.Window.FindName("GeminiApiKeyInput");
        var status = (TextBlock)fixture.Window.FindName("GeminiCredentialStatus");
        Assert.NotNull(pass);
        Assert.NotNull(status);
        Assert.Equal(string.Empty, pass.Text);
        Assert.True(pass.AcceptsReturn);
        return Task.CompletedTask;
    });

    private static SourceEditPreview Preview(int protectedSegments, bool replacesMost) => new(
        1, new string('0', 64), new string('1', 64), "新正文。", new SegmenterOptions(),
        new SourceTextChange(0, 1, 0, 1, "旧", "新"), 1, 1, 0, 1, protectedSegments, replacesMost);
}
