using System.Reflection;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class OllamaBackendUiTests
{
    [Fact]
    public Task Saved_remote_profile_is_restored_with_password_input_and_backend_status() => StaHost.Run(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-m65-wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new OllamaProfileStore(Path.Combine(root, "profiles.json"), new FakeProtector());
        OllamaBackendProfile local = OllamaBackendProfile.CreateDefaultLocal();
        var remote = new OllamaBackendProfile
        {
            Id = Guid.NewGuid(), Name = "EZYCloudX 3090 Ti", Kind = OllamaBackendKind.Remote,
            Endpoint = "https://gpu.example.com", Model = "translategemma:12b", Token = "private-token",
            RemoteDisclosureAccepted = true
        };
        await store.SaveAsync(new() { ActiveProfileId = remote.Id, Profiles = [local, remote] });
        var manager = new OllamaBackendManager(store, profile => new FakeBackendClient());
        var window = new MainWindow(null, initializeApplication: false, manager);
        try
        {
            Invoke(window, "InitializeSettingsUi");
            await (Task)Invoke(window, "EnsureOllamaBackendsLoadedAsync")!;

            Assert.Equal(2, ((ComboBox)window.FindName("OllamaProfileInput")).Items.Count);
            Assert.Equal("private-token", ((PasswordBox)window.FindName("OllamaTokenInput")).Password);
            Assert.Equal("https://gpu.example.com/api/chat", ((TextBox)window.FindName("OllamaEndpointInput")).Text);
            Assert.Contains("Remote", ((TextBlock)window.FindName("OllamaBackendStatus")).Text, StringComparison.Ordinal);
            Assert.Contains("EZYCloudX 3090 Ti", ((TextBlock)window.FindName("OllamaBackendStatus")).Text, StringComparison.Ordinal);
        }
        finally
        {
            Set(window, "_allowClose", true);
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public Task Active_request_blocks_backend_switch_before_profile_or_project_changes() => StaHost.Run(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-m65-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manager = new OllamaBackendManager(
            new OllamaProfileStore(Path.Combine(root, "profiles.json"), new FakeProtector()),
            profile => new FakeBackendClient());
        var window = new MainWindow(null, initializeApplication: false, manager);
        try
        {
            Invoke(window, "InitializeSettingsUi");
            await (Task)Invoke(window, "EnsureOllamaBackendsLoadedAsync")!;
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Set(window, "_activeRequest", pending.Task);

            Invoke(window, "OnNewOllamaProfile", window, new RoutedEventArgs());
            Invoke(window, "OnSaveOllamaProfile", window, new RoutedEventArgs());

            Assert.Equal(OllamaBackendProfile.DefaultLocalId, manager.ActiveProfile.Id);
            Assert.Contains("Pause", ((TextBlock)window.FindName("StatusText")).Text, StringComparison.OrdinalIgnoreCase);
            Assert.Single(manager.Profiles);
            pending.SetResult();
        }
        finally
        {
            Set(window, "_activeRequest", null);
            Set(window, "_allowClose", true);
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public Task Editing_remote_destination_requires_disclosure_again() => StaHost.Run(async () =>
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-m65-consent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var store = new OllamaProfileStore(Path.Combine(root, "profiles.json"), new FakeProtector());
        OllamaBackendProfile local = OllamaBackendProfile.CreateDefaultLocal();
        var remote = new OllamaBackendProfile
        {
            Id = Guid.NewGuid(), Name = "Remote", Kind = OllamaBackendKind.Remote,
            Endpoint = "https://gpu-a.example.com", Model = "translategemma:12b",
            RemoteDisclosureAccepted = true
        };
        await store.SaveAsync(new() { ActiveProfileId = remote.Id, Profiles = [local, remote] });
        var manager = new OllamaBackendManager(store, _ => new FakeBackendClient());
        var window = new MainWindow(null, initializeApplication: false, manager);
        try
        {
            Invoke(window, "InitializeSettingsUi");
            await (Task)Invoke(window, "EnsureOllamaBackendsLoadedAsync")!;
            ((TextBox)window.FindName("OllamaEndpointInput")).Text = "https://gpu-b.example.com";

            var edited = Assert.IsType<OllamaBackendProfile>(Invoke(window, "ReadOllamaProfileEditor"));

            Assert.False(edited.RemoteDisclosureAccepted);
            Assert.Equal("https://gpu-b.example.com/api/chat", edited.Endpoint);
        }
        finally
        {
            Set(window, "_allowClose", true);
            window.Close();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    private static object? Invoke(MainWindow window, string name, params object?[] args)
    {
        try { return typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }

    private static void Set(MainWindow window, string name, object? value) =>
        typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, value);

    private sealed class FakeProtector : IOllamaSecretProtector
    {
        public string Protect(string value) => "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value["protected:".Length..]));
    }

    private sealed class FakeBackendClient : IOllamaBackendClient
    {
        public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["translategemma:12b"]);
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OllamaCompletion("translated", true, "stop", new()));
        public void Dispose() { }
    }
}
