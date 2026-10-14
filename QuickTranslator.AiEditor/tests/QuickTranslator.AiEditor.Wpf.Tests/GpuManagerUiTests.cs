using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class GpuManagerUiTests
{
    [Fact]
    public Task Gpu_manager_exposes_profiles_credentials_host_key_and_controlled_setup_actions() => StaHost.Run(async () =>
    {
        string root = CreateTestRoot();
        using var backends = CreateBackendManager(root);
        await using var connections = new GpuConnectionManager(new NoConnectionService(), (_, _) => new StubClient());
        var window = new GpuManagerWindow(backends, connections);
        try
        {
            window.Measure(new Size(1040, 720));
            window.Arrange(new Rect(0, 0, 1040, 720));

            Assert.NotNull(window.FindName("ProfileList"));
            Assert.NotNull(window.FindName("HostInput"));
            Assert.NotNull(window.FindName("PasswordInput"));
            Assert.NotNull(window.FindName("PrivateKeyInput"));
            Assert.NotNull(window.FindName("FingerprintInput"));
            Assert.NotNull(window.FindName("HourlyPriceInput"));
            Assert.NotNull(window.FindName("TranslationModeInput"));
            Assert.NotNull(window.FindName("RuntimeTemperatureInput"));
            Assert.NotNull(window.FindName("RuntimeContextInput"));
            Assert.NotNull(window.FindName("RuntimeOutputInput"));
            Assert.NotNull(window.FindName("RuntimeTimeoutInput"));
            Assert.NotNull(window.FindName("RuntimeMinimumInput"));
            Assert.NotNull(window.FindName("RuntimeTargetInput"));
            Assert.NotNull(window.FindName("RuntimeMaximumInput"));
            Assert.NotNull(window.FindName("AdvancedConnectionExpander"));
            Assert.NotNull(window.FindName("ConnectionTypeInput"));
            Assert.NotNull(window.FindName("EndpointInput"));
            Assert.NotNull(window.FindName("TokenInput"));
            Assert.False(((Button)window.FindName("CancelSetupButton")).IsEnabled);
            Assert.Equal(Visibility.Collapsed, ((ProgressBar)window.FindName("ModelPullProgress")).Visibility);
            Assert.True(window.DesiredSize.Width <= 1040);
            Assert.True(window.DesiredSize.Height <= 720);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    });

    [Fact]
    public Task BL19_Selecting_remote_profile_auto_expands_advanced_connection_expander() => StaHost.Run(async () =>
    {
        string root = CreateTestRoot();
        using var backends = CreateBackendManager(root);
        await using var connections = new GpuConnectionManager(new NoConnectionService(), (_, _) => new StubClient());
        var window = new GpuManagerWindow(backends, connections);
        try
        {
            var expander = (Expander)window.FindName("AdvancedConnectionExpander");
            Assert.False(expander.IsExpanded);

            var list = (ListBox)window.FindName("ProfileList");
            var remote = new OllamaBackendProfile
            {
                Name = "GPU Remote Test",
                Kind = OllamaBackendKind.Remote,
                Endpoint = "http://127.0.0.1:11434",
                Model = "translategemma:12b"
            };
            await backends.SaveProfileAsync(remote, activate: false);
            list.SelectedItem = list.Items.Cast<OllamaBackendProfile>().FirstOrDefault(p => p.Id == remote.Id) ?? remote;
            window.GetType().GetMethod("OnProfileSelected", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [list, null!]);
            if (list.SelectedItem is OllamaBackendProfile { Kind: OllamaBackendKind.Remote })
                Assert.True(expander.IsExpanded);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    });

    private static string CreateTestRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-gpu-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static OllamaBackendManager CreateBackendManager(string root) => new(
        new OllamaProfileStore(Path.Combine(root, "profiles.json"), new TestProtector()),
        _ => new StubClient());

    private sealed class TestProtector : IOllamaSecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }

    private sealed class NoConnectionService : ISshConnectionService
    {
        public Task<ISshSession> ConnectAsync(GpuSshSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException<ISshSession>(new InvalidOperationException("test only"));
    }

    private sealed class StubClient : IOllamaBackendClient
    {
        public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<OllamaCompletion> TranslateAsync(string prompt, QuickTranslator.AiEditor.Core.Models.ModelOptions options,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
