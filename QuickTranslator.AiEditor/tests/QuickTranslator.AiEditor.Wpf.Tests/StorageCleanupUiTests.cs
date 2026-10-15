using System.Windows.Controls;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class StorageCleanupUiTests
{
    [Fact]
    public Task Project_menu_exposes_storage_cleanup_without_terminal() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();

        var item = Assert.IsType<MenuItem>(fixture.Window.FindName("CleanupProjectStorageMenuItem"));

        Assert.Equal("Dọn dữ liệu thừa…", item.Header);
        Assert.True(item.IsEnabled);
    });
}
