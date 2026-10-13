using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ChapterTitleTranslationUiTests
{
    [Fact]
    public Task Bulk_title_preview_exposes_method_retry_cancel_edit_and_apply_controls() => StaHost.Run(async () =>
    {
        string path = Path.Combine(Path.GetTempPath(), "qtaie-title-ui-" + Guid.NewGuid().ToString("N"), "draft.json");
        var sources = Enumerable.Range(1, 300).Select(number => new ChapterTitleTranslationSource(
            Guid.NewGuid(), number, $"第{number}章 标题{number}")).ToArray();
        var window = new ChapterTitleTranslationWindow(Guid.NewGuid(), "ui-test", sources,
            (method, _) => Task.FromResult(new ChapterTitleRuntime(new KeepChineseChapterTitleTranslationBackend(),
                new ModelOptions(), null, "none")), path);
        try
        {
            window.Measure(new Size(1120, 760));
            window.Arrange(new Rect(0, 0, 1120, 760));
            Assert.NotNull(window.FindName("MethodInput"));
            Assert.NotNull(window.FindName("OverwriteProtectedCheck"));
            Assert.NotNull(window.FindName("TranslateAllButton"));
            Assert.NotNull(window.FindName("RetryFailedButton"));
            Assert.NotNull(window.FindName("RetrySelectedButton"));
            Assert.NotNull(window.FindName("CancelButton"));
            Assert.NotNull(window.FindName("ApplyButton"));
            Assert.True(VirtualizingPanel.GetIsVirtualizing((DataGrid)window.FindName("TitleGrid")));
            Assert.True(window.DesiredSize.Width <= 1120);
            Assert.True(window.DesiredSize.Height <= 760);
        }
        finally
        {
            window.Close();
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        await Task.CompletedTask;
    });
}
