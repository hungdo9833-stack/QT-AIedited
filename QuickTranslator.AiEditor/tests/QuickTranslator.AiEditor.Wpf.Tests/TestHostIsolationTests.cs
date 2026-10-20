using System.Windows;
using System.Windows.Media;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class TestHostIsolationTests
{
    [Fact]
    public Task Test_host_never_runs_real_startup_or_opens_user_stores() => StaHost.Run(async () =>
    {
        // Let any queued startup dispatch run first.
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Assert.False(App.RealStartupRan);
        Assert.Null(Application.Current.MainWindow);
        // A fixture must not pick up the user's real workspace layout (e.g. a saved Light theme).
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync();
        Assert.Equal(Color.FromRgb(15, 17, 21), ((SolidColorBrush)Application.Current.Resources["AppBackgroundBrush"]).Color);
    });
}
