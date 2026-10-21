using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class EditorUxPhase1Tests
{
    [Fact]
    public Task Editor_has_at_most_one_visible_primary_button_in_representative_states() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        fixture.Window.Show();
        Style primary = (Style)fixture.Window.FindResource("PrimaryButton");

        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        fixture.Call("Refresh");
        AssertPrimaryCount(fixture.Window, primary);

        ChapterDocument chapter = TranslationService.CreateChapter("Chương chưa dịch", "你好。世界。");
        fixture.Attach(chapter);
        fixture.Call("Refresh");
        AssertPrimaryCount(fixture.Window, primary);

        chapter = fixture.Service.GetSnapshot();
        chapter.Segments[0].Status = SegmentStatus.AwaitingReview;
        chapter.Segments[0].UserDraft = "Bản nháp.";
        fixture.Attach(chapter);
        fixture.Call("Refresh");
        AssertPrimaryCount(fixture.Window, primary);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    });

    [Fact]
    public Task Visible_disabled_editor_actions_explain_themselves() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Window.Show();
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        ((Expander)fixture.Window.FindName("ReviewChecksPanel")).IsExpanded = true;
        fixture.Call("Refresh");
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        FrameworkElement[] roots =
        [
            (FrameworkElement)fixture.Window.FindName("EditorContextHeader"),
            (FrameworkElement)((Button)fixture.Window.FindName("TranslateButton")).Parent,
            (FrameworkElement)fixture.Window.FindName("ReviewChecksPanel")
        ];
        foreach (Button button in roots.SelectMany(VisualChildren<Button>).Where(button => button.IsVisible && !button.IsEnabled))
        {
            Assert.True(ToolTipService.GetShowOnDisabled(button));
            Assert.False(string.IsNullOrWhiteSpace(button.ToolTip?.ToString()));
            Assert.False(string.IsNullOrWhiteSpace(System.Windows.Automation.AutomationProperties.GetHelpText(button)));
        }
    });

    [Fact]
    public Task Semantic_editor_routes_restore_keyboard_focus() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeChapterView");
        fixture.Window.Show();
        fixture.Window.Activate();
        ChapterDocument chapter = TranslationService.CreateChapter("Focus", "你好。世界。");
        fixture.Attach(chapter);
        fixture.Call("Refresh");

        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.True(fixture.Text("DraftText").IsKeyboardFocusWithin);

        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var blockItems = (ItemsControl)fixture.Window.FindName("ChapterBlocksItems");
        Assert.True(blockItems.Items.Count > 0, "Two-pane block list is empty.");
        Assert.True(VisualChildren<TextBox>(blockItems).Any(),
            $"No generated text boxes; generator={blockItems.ItemContainerGenerator.Status}, visible={blockItems.IsVisible}.");
        TextBox? editor = System.Windows.Input.Keyboard.FocusedElement as TextBox;
        Assert.NotNull(editor);
        Assert.Equal(chapter.SelectedSegmentId, editor!.Tag);
        Assert.True(editor!.IsKeyboardFocusWithin);
    });

    [Fact]
    public Task Quality_route_opens_review_checks() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("ActivateShellSection", "quality");
        Assert.Equal(WorkspaceId.Editor, fixture.Window.CurrentWorkspace);
        Assert.Equal(EditorMode.Review, fixture.Window.CurrentEditorMode);
        Assert.True(((Expander)fixture.Window.FindName("ReviewChecksPanel")).IsExpanded);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Switching_editor_modes_preserves_pending_draft_without_saving() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Window.Show();
        ChapterDocument chapter = TranslationService.CreateChapter("Autosave", "你好。世界。");
        fixture.Attach(chapter);
        fixture.Call("Refresh");
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

        TextBox draft = fixture.Text("DraftText");
        draft.Text = "Bản nháp vẫn đang chờ autosave.";
        Assert.True(fixture.Service.HasUnsavedChanges);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);

        Assert.True(fixture.Service.HasUnsavedChanges);
        Assert.Equal("Bản nháp vẫn đang chờ autosave.", fixture.Text("DraftText").Text);
    });

    private static void AssertPrimaryCount(Window window, Style primary)
    {
        Button[] visible = VisualChildren<Button>(window)
            .Where(button => button.IsVisible && ReferenceEquals(button.Style, primary)).ToArray();
        Assert.True(visible.Length <= 1, "Primary buttons visible: " + string.Join(", ", visible.Select(button => button.Name)));
    }

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
