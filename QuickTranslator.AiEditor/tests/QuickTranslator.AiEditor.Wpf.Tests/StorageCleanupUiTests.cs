using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class LegacyStorageUiRemovalTests
{
    [Fact]
    public Task Project_menu_has_no_recovery_cleanup_or_backup_commands() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();

        Assert.Null(fixture.Window.FindName("CleanupProjectStorageMenuItem"));
        Assert.DoesNotContain("Sao lưu ZIP", fixture.Window.ToString(), StringComparison.OrdinalIgnoreCase);
    });
}
