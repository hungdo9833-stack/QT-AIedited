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
        chapter.Segments[0].UserDraft = "Anh ấy gọi 老公 rồi nhắc 上次在酒店.";
        chapter.Segments[0].DraftRevision = 1;
        fixture.Attach(chapter);
        string before = fixture.Service.GetSnapshot().Segments[0].EditableText;
        Window? dialog = null;
        Window? options = null;
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

            DataGrid chinese = Descendants<DataGrid>(dialog).Single(grid => grid.IsReadOnly);
            Button command = Descendants<Button>(dialog).Single(button => Equals(button.Content, "Dịch lại…"));

            Assert.True(command.IsEnabled);
            Assert.Contains("toàn bộ Project", command.ToolTip?.ToString());
            Assert.Equal(DataGridSelectionMode.Extended, chinese.SelectionMode);
            Assert.True(chinese.Items.Count >= 2);
            chinese.SelectedItems.Add(chinese.Items[0]);
            chinese.SelectedItems.Add(chinese.Items[1]);
            Assert.Equal(2, chinese.SelectedItems.Count);
            _ = dialog.Dispatcher.BeginInvoke(() => command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await UntilAsync(() => dialog.OwnedWindows.Count > 0);
            options = dialog.OwnedWindows.Cast<Window>().Single();
            string[][] labels = Descendants<ComboBox>(options).Select(combo => combo.Items.Cast<object>()
                .Select(item => item.GetType().GetProperty("Label")?.GetValue(item)?.ToString() ?? "").ToArray()).ToArray();
            Assert.Contains(labels, values => values.Any(label => label.Contains("Toàn bộ dự án")));
            Assert.Contains(labels, values => values.Any(label => label.Contains("toàn bộ đoạn")));
            Assert.Contains(labels, values => values.Any(label => label.Contains("Ollama")) &&
                                              values.Any(label => label.Contains("VietPhrase")) &&
                                              values.Any(label => label.Contains("Hán Việt")));
            options.Close();
            await UntilAsync(() => dialog.OwnedWindows.Count == 0);
            chinese.SelectedItems.Clear();
            _ = dialog.Dispatcher.BeginInvoke(() => command.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            await UntilAsync(() => dialog.OwnedWindows.Count > 0);
            options = dialog.OwnedWindows.Cast<Window>().Single();
            string[][] noSelectionLabels = Descendants<ComboBox>(options).Select(combo => combo.Items.Cast<object>()
                .Select(item => item.GetType().GetProperty("Label")?.GetValue(item)?.ToString() ?? "").ToArray()).ToArray();
            Assert.Contains(noSelectionLabels, values => values.Length == 1 && values[0].Contains("Toàn bộ dự án"));
            options.Close();
            Assert.Equal(before, fixture.Service.GetSnapshot().Segments[0].EditableText);
            Assert.Empty(fixture.Service.GetSnapshot().BatchReplacementHistory);
        }
        finally
        {
            if (options is not null && options.IsVisible) options.Close();
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

