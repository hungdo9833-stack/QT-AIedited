using System.Reflection;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Traditional;
using TextRange = QuickTranslator.AiEditor.Core.Traditional.TextRange;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class TraditionalHighlightUiTests
{
    // The window loads in Biên tập (two-pane): the four-pane tab is not in the visual tree yet, so its highlight
    // layers cannot exist at that moment. Selecting text later in Kiểm tra & Duyệt must still highlight the other panes.
    [Fact]
    public Task Four_pane_highlights_work_when_the_window_opened_in_two_pane_mode() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Window.Show();
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        fixture.Call("InitializeTraditionalHighlightLayers"); // what Window.Loaded does

        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        fixture.Text("SourceText").Text = "你好世界";
        fixture.Text("HanVietText").Text = "Nễ hảo thế giới";
        fixture.Text("VietPhraseText").Text = "Xin chào thế giới";

        var projection = new SelectionProjection(new TextRange(0, 2), new TextRange(0, 7), new TextRange(0, 8));
        fixture.Call("ShowTraditionalHighlights", TraditionalPane.VietPhrase, projection);

        TextBoxRangeHighlightAdorner chinese = Highlight(fixture, "_chineseHighlight");
        TextBoxRangeHighlightAdorner hanViet = Highlight(fixture, "_hanVietHighlight");
        Assert.Equal(new TextRange(0, 2), chinese.Range);
        Assert.Equal(new TextRange(0, 7), hanViet.Range);
        Assert.Null(Highlight(fixture, "_vietPhraseHighlight").Range); // the pane the user selected in keeps its own selection
        Assert.Same(AdornerLayer.GetAdornerLayer(fixture.Text("SourceText")), VisualTreeHelper.GetParent(chinese));

        // Leaving and re-entering the mode re-hosts the panes; highlights must still land in the live layer.
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Translate);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        fixture.Window.NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        fixture.Text("SourceText").Text = "你好世界"; // Refresh without a selected segment cleared the panes
        fixture.Text("HanVietText").Text = "Nễ hảo thế giới";
        fixture.Call("ShowTraditionalHighlights", TraditionalPane.Chinese, projection);
        TextBoxRangeHighlightAdorner hanVietAgain = Highlight(fixture, "_hanVietHighlight");
        Assert.Equal(new TextRange(0, 7), hanVietAgain.Range);
        Assert.Same(AdornerLayer.GetAdornerLayer(fixture.Text("HanVietText")), VisualTreeHelper.GetParent(hanVietAgain));
        fixture.Window.Close();
    });

    private static TextBoxRangeHighlightAdorner Highlight(WorkspaceFixture fixture, string field) =>
        (TextBoxRangeHighlightAdorner?)typeof(MainWindow).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(fixture.Window) ?? throw new Xunit.Sdk.XunitException(field + " chưa được tạo: ô này không thể tô sáng.");
}
