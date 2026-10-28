using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ProjectSidebarUiTests
{
    [Fact]
    public Task ShellToolbarIsCollapsedAndChapterListUsesExtendedSelection() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        Assert.Equal(Visibility.Collapsed, ((Border)fixture.Window.FindName("GlobalToolbar")).Visibility);
        var list = (ListBox)fixture.Window.FindName("ProjectChapterList");
        Assert.Equal(SelectionMode.Extended, list.SelectionMode);
        Assert.Equal(new[] { "Đổi tên", "Xóa", "Dịch lại", "Chèn chương phía trên", "Chèn chương phía dưới", "Nhân bản" },
            list.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ContextMenusWithSeparatorsOpenWithoutThrowing() => StaHost.Run(async () =>
    {
        // Regression: a themed ItemContainerStyle for MenuItem was applied to Separator and crashed the app on right click.
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeSourceContextMenus");
        fixture.Window.ShowInTaskbar = false; fixture.Window.Left = -30000;
        fixture.Window.Show();
        var list = (ListBox)fixture.Window.FindName("ProjectChapterList");
        var source = (TextBox)fixture.Window.FindName("SourceText");
        foreach (ContextMenu menu in new[] { list.ContextMenu!, source.ContextMenu! })
        {
            Assert.Contains(menu.Items.Cast<object>(), item => item is Separator);
            menu.PlacementTarget = (UIElement)(ReferenceEquals(menu, list.ContextMenu) ? list : source);
            menu.IsOpen = true; // The crash happened here, while the popup measured its items.
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            menu.IsOpen = false;
        }
    });

    [Fact]
    public Task MultipleSelectionEnablesOnlyBulkContextCommands() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeProjectWorkspace");
        await fixture.CreateProjectAsync();
        await fixture.AddChapterAsync("Một", 1);
        await fixture.AddChapterAsync("Hai", 2);
        fixture.Call("RefreshProjectChapterList");
        var list = (ListBox)fixture.Window.FindName("ProjectChapterList");
        fixture.Set("_suppressProjectChapterOpen", true);
        try
        {
            list.SelectedItems.Clear();
            list.SelectedItems.Add(list.Items[0]);
            list.SelectedItems.Add(list.Items[1]);
        }
        finally { fixture.Set("_suppressProjectChapterOpen", false); }
        fixture.Call("UpdateProjectChapterCommands");

        Assert.False(((MenuItem)fixture.Window.FindName("ProjectRenameChapterButton")).IsEnabled);
        Assert.False(((MenuItem)fixture.Window.FindName("ProjectDuplicateChapterButton")).IsEnabled);
        Assert.True(((MenuItem)fixture.Window.FindName("ProjectDeleteChapterButton")).IsEnabled);
        MenuItem[] commands = list.ContextMenu!.Items.OfType<MenuItem>().ToArray();
        Assert.False(commands.Single(item => Equals(item.Header, "Đổi tên")).IsEnabled);
        Assert.True(commands.Single(item => Equals(item.Header, "Xóa")).IsEnabled);
        Assert.True(commands.Single(item => Equals(item.Header, "Dịch lại")).IsEnabled);
        Assert.False(commands.Single(item => Equals(item.Header, "Chèn chương phía trên")).IsEnabled);
        Assert.False(commands.Single(item => Equals(item.Header, "Chèn chương phía dưới")).IsEnabled);
        Assert.False(commands.Single(item => Equals(item.Header, "Nhân bản")).IsEnabled);
    });

    [Fact]
    public Task InsertedDraftRegistersAtRememberedReadingPosition() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("InitializeProjectWorkspace");
        await fixture.CreateProjectAsync();
        ChapterDocument first = await fixture.AddChapterAsync("Một", 1);
        ChapterDocument second = await fixture.AddChapterAsync("Hai", 2);

        await fixture.TaskCall("BeginChapterDraftAsync", 2, "Đang chèn chương.");
        Assert.Equal(2, fixture.Get<int?>("_pendingProjectChapterNumber"));
        Assert.Equal("", fixture.Text("ChapterInput").Text);
        Assert.Equal("Chương 2", fixture.Text("TitleInput").Text);

        ChapterDocument draft = TranslationService.CreateChapter("Chèn", "天地玄黄。");
        ChapterDocument registered = await fixture.TaskCall<ChapterDocument>("RegisterChapterIfNeededAsync", draft);
        Assert.Null(fixture.Get<int?>("_pendingProjectChapterNumber"));
        Assert.Equal(new[] { first.Id, registered.Id, second.Id }, fixture.Project.Manifest.Chapters
            .Where(item => item.DeletedAt is null).OrderBy(item => item.Number).Select(item => item.ChapterId));
    });
}
