using System.IO;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class GpuManagerUiTests
{
    [Fact]
    public Task Saving_the_active_gpu_profile_reloads_runtime_model_for_new_requests() => StaHost.Run(async () =>
    {
        string root = CreateTestRoot();
        using var backends = CreateBackendManager(root);
        await using var connections = new GpuConnectionManager(new NoConnectionService(), (_, _) => new StubClient());
        var window = new GpuManagerWindow(backends, connections);
        try
        {
            int activeProfileChanges = 0;
            backends.ActiveProfileChanged += (_, _) => activeProfileChanges++;
            ((TextBox)window.FindName("ModelInput")).Text = "huihui_ai/hy-mt1.5-abliterated:7b";

            ((Button)window.FindName("SaveProfileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntilAsync(() => ((TextBlock)window.FindName("StatusText")).Text.Contains("Đã lưu", StringComparison.Ordinal));

            Assert.Equal(1, activeProfileChanges);
            Assert.Equal("huihui_ai/hy-mt1.5-abliterated:7b",
                backends.ActiveProfile.Runtime!.ModelOptions.Model);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    });

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
            Assert.NotNull(window.FindName("DownloadModelInput"));
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
            Assert.False(((Expander)window.FindName("AdvancedRuntimeExpander")).IsExpanded);
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
    public Task Selecting_remote_profile_keeps_advanced_sections_collapsed_until_requested() => StaHost.Run(async () =>
    {
        string root = CreateTestRoot();
        using var backends = CreateBackendManager(root);
        await using var connections = new GpuConnectionManager(new NoConnectionService(), (_, _) => new StubClient());
        var window = new GpuManagerWindow(backends, connections);
        try
        {
            var expander = (Expander)window.FindName("AdvancedConnectionExpander");
            Assert.False(expander.IsExpanded);

            var download = (TextBox)window.FindName("DownloadModelInput");
            var configured = (TextBox)window.FindName("ModelInput");
            download.Text = "llama3.2:3b";
            Assert.Equal("translategemma:12b", configured.Text);

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
            {
                Assert.False(expander.IsExpanded);
                Assert.False(((Expander)window.FindName("AdvancedRuntimeExpander")).IsExpanded);
            }
        }
        finally { window.Close(); Directory.Delete(root, true); }
    });

    [Fact]
    public Task Importing_an_rdp_file_fills_the_ssh_fields_and_drops_the_old_host_fingerprint() => StaHost.Run(async () =>
    {
        string root = CreateTestRoot();
        using var backends = CreateBackendManager(root);
        await using var connections = new GpuConnectionManager(new NoConnectionService(), (_, _) => new StubClient());
        var window = new GpuManagerWindow(backends, connections);
        try
        {
            var autoButton = (Button)window.FindName("AutoSetupButton");
            Assert.NotNull(window.FindName("ImportRdpButton"));
            Assert.True(autoButton.IsEnabled);

            window.GetType().GetMethod("OnNewProfile", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(window, [null!, null!]);
            ((TextBox)window.FindName("FingerprintInput")).Text = "SHA256:abcdefghijklmnopqrstuv";
            ((TextBox)window.FindName("HostInput")).Text = "old.example.test";

            window.ApplyRdpFile(RdpConnectionFile.Parse(
                "full address:s:203.0.113.7:33890\nusername:s:WIN-GPU\\Administrator"), @"C:\gpu.rdp");

            Assert.Equal("203.0.113.7", ((TextBox)window.FindName("HostInput")).Text);
            Assert.Equal("33890", ((TextBox)window.FindName("PortInput")).Text);
            Assert.Equal("Administrator", ((TextBox)window.FindName("UsernameInput")).Text);
            Assert.Equal("", ((TextBox)window.FindName("FingerprintInput")).Text);
            Assert.True(((Expander)window.FindName("AdvancedConnectionExpander")).IsExpanded);
            Assert.Contains("33890", ((TextBlock)window.FindName("StatusText")).Text);
        }
        finally { window.Close(); Directory.Delete(root, true); }
    });

    private static string CreateTestRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-gpu-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("GPU Manager did not finish saving the profile.");
            await Task.Delay(10);
        }
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
