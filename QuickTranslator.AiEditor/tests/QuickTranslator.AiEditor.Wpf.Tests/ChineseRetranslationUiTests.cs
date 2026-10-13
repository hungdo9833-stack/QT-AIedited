using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ChineseRetranslationUiTests
{
    [Fact]
    public Task Chinese_scan_exposes_retranslate_command_without_writing_the_chapter() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        ChapterDocument chapter = TranslationService.CreateChapter("Chương kiểm tra", "老公说了一句话。");
        chapter.Segments[0].UserDraft = "Anh ấy gọi 老公 rồi rời đi.";
        chapter.Segments[0].DraftRevision = 1;
        fixture.Attach(chapter);
        string before = fixture.Service.GetSnapshot().Segments[0].EditableText;
        Window? dialog = null;
        Task? showing = null;
        try
        {
            fixture.Window.ShowInTaskbar = false;
            fixture.Window.Left = -30000;
            fixture.Window.Show();
            showing = fixture.Window.Dispatcher.InvokeAsync(() => fixture.Call("ShowTextCleanerWindow", true)).Task;
            await UntilAsync(() => fixture.Window.OwnedWindows.Count > 0);
            dialog = fixture.Window.OwnedWindows.Cast<Window>().Single();
            await UntilAsync(() => Descendants<DataGrid>(dialog).Any(grid => grid.IsReadOnly && grid.Items.Count > 0));

            Button command = Descendants<Button>(dialog).Single(button => Equals(button.Content, "Dịch lại…"));

            Assert.True(command.IsEnabled);
            Assert.Contains("toàn bộ segment", command.ToolTip?.ToString());
            Assert.Equal(before, fixture.Service.GetSnapshot().Segments[0].EditableText);
            Assert.Empty(fixture.Service.GetSnapshot().BatchReplacementHistory);
        }
        finally
        {
            if (dialog is not null) dialog.Close();
            if (showing is not null) await showing;
        }
    });

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("UI condition was not reached.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
}

