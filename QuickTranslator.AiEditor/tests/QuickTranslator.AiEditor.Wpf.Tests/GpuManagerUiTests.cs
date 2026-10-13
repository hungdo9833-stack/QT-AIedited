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
        using var backends = new OllamaBackendManager(clientFactory: _ => new StubClient());
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
            Assert.True(window.DesiredSize.Width <= 1040);
            Assert.True(window.DesiredSize.Height <= 720);
        }
        finally { window.Close(); }
    });

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
