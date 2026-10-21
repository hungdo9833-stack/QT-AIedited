using System.Text;
using System.Net;
using System.Net.Http.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GpuConnectionManagerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-m66-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Profile_store_round_trips_ssh_credentials_without_plaintext()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "profiles.json");
        var store = new OllamaProfileStore(path, new Protector());
        var remote = RemoteProfile();
        remote.Ssh!.Authentication = SshAuthenticationKind.PrivateKey;
        remote.Ssh.Password = null;
        remote.Ssh.PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-key-only\n-----END OPENSSH PRIVATE KEY-----";
        remote.Ssh!.PrivateKeyPassphrase = "key-secret";
        var catalog = new OllamaProfileCatalog { ActiveProfileId = remote.Id, Profiles = [remote] };

        await store.SaveAsync(catalog);
        OllamaProfileCatalog reopened = await store.LoadAsync();
        string json = await File.ReadAllTextAsync(path);

        Assert.Contains("synthetic-key-only", reopened.Profiles[0].Ssh!.PrivateKey, StringComparison.Ordinal);
        Assert.Equal("key-secret", reopened.Profiles[0].Ssh!.PrivateKeyPassphrase);
        Assert.DoesNotContain("synthetic-key-only", json, StringComparison.Ordinal);
        Assert.DoesNotContain("key-secret", json, StringComparison.Ordinal);
        Assert.Contains("protected:", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tunnel_is_loopback_scoped_and_is_released_on_disconnect()
    {
        var ssh = new FakeSshConnectionService();
        var clients = new List<FakeBackendClient>();
        await using var manager = new GpuConnectionManager(ssh, (_, endpoint) =>
        {
            Assert.Equal("127.0.0.1", endpoint.Host);
            Assert.Equal("/api/chat", endpoint.AbsolutePath);
            var client = new FakeBackendClient(); clients.Add(client); return client;
        });

        GpuConnectionHandle handle = await manager.ConnectAsync(RemoteProfile());
        Assert.True(handle.IsConnected);
        Assert.Equal(11434, ssh.Session!.RemotePort);

        await manager.DisconnectAsync(handle.Profile.Id);
        Assert.True(ssh.Session.Tunnel!.Disposed);
        Assert.True(ssh.Session.Disposed);
        Assert.True(clients.Single().Disposed);
    }

    [Fact]
    public async Task Llama_server_tunnel_uses_configured_port_and_client_health_check()
    {
        var ssh = new FakeSshConnectionService();
        var client = new FakeBackendClient();
        await using var manager = new GpuConnectionManager(ssh, (profile, endpoint) =>
        {
            Assert.Equal(BackendApi.OpenAiCompatible, profile.Api);
            Assert.Equal("http://127.0.0.1:43117/api/chat", endpoint.ToString());
            return client;
        });
        OllamaBackendProfile profile = RemoteProfile();
        profile.Api = BackendApi.OpenAiCompatible;
        profile.ParallelRequests = 4;
        profile.Ssh!.RemoteOllamaPort = 11436;

        await manager.ConnectAsync(profile);

        Assert.Equal(11436, ssh.Session!.RemotePort);
        Assert.Equal(1, client.CheckCalls);
    }

    [Fact]
    public async Task Lost_tunnel_reconnects_with_a_new_session_without_resending_any_prompt()
    {
        var ssh = new FakeSshConnectionService();
        await using var manager = new GpuConnectionManager(ssh, (_, _) => new FakeBackendClient(),
            healthInterval: TimeSpan.FromMilliseconds(10));
        OllamaBackendProfile profile = RemoteProfile();
        await manager.ConnectAsync(profile);

        ssh.Sessions[0].Connected = false;
        await SpinWaitAsync(() => ssh.Calls >= 2 && manager.GetStatus(profile.Id).State == GpuConnectionState.Connected,
            TimeSpan.FromSeconds(2));

        Assert.True(ssh.Sessions[0].Disposed);
        Assert.Equal(2, ssh.Calls);
        Assert.True(ssh.Sessions[1].IsConnected);
    }

    [Fact]
    public async Task Host_key_failure_is_blocking_and_never_leaks_credentials()
    {
        var service = new RejectingSshService();
        await using var manager = new GpuConnectionManager(service, (_, _) => new FakeBackendClient());
        OllamaBackendProfile profile = RemoteProfile();

        SshHostKeyVerificationException error = await Assert.ThrowsAsync<SshHostKeyVerificationException>(() => manager.ConnectAsync(profile));

        Assert.Equal(GpuConnectionState.TrustRequired, manager.GetStatus(profile.Id).State);
        Assert.DoesNotContain("ssh-secret", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ssh-secret", manager.GetStatus(profile.Id).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconnect_is_bounded_and_uses_exponential_backoff()
    {
        var service = new AlwaysFailSshService();
        var delays = new List<TimeSpan>();
        await using var manager = new GpuConnectionManager(service, (_, _) => new FakeBackendClient(),
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReconnectAsync(RemoteProfile(), 3));

        Assert.Equal(3, service.Calls);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Fact]
    public async Task Ssh_profile_never_uses_saved_endpoint_before_runtime_tunnel_is_attached()
    {
        Directory.CreateDirectory(_root);
        var store = new OllamaProfileStore(Path.Combine(_root, "manager.json"), new Protector());
        var remote = RemoteProfile();
        await store.SaveAsync(new() { ActiveProfileId = remote.Id, Profiles = [remote] });
        using var manager = new OllamaBackendManager(store);
        await manager.LoadAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TranslateAsync("must-not-leave", new()));

        var runtime = new FakeBackendClient();
        await manager.AttachRuntimeClientAsync(remote.Id, runtime);
        OllamaCompletion completion = await manager.TranslateAsync("synthetic", new());
        Assert.Equal("ok", completion.Text);
        await manager.DetachRuntimeClientAsync(remote.Id);
        Assert.False(runtime.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TranslateAsync("must-not-leave-2", new()));
    }

    private static OllamaBackendProfile RemoteProfile() => new()
    {
        Id = Guid.NewGuid(), Name = "EZYCloudX RTX 3090 Ti", Kind = OllamaBackendKind.Remote,
        Endpoint = "http://127.0.0.1:11434", Model = "translategemma:12b", RemoteDisclosureAccepted = true,
        HourlyPrice = 0.42m,
        Ssh = new() { Host = "gpu.example.test", Port = 2222, Username = "ubuntu", Password = "ssh-secret",
            Authentication = SshAuthenticationKind.Password, HostKeyFingerprint = "SHA256:abcdefghijklmnopqrstuv" }
    };

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static async Task SpinWaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + timeout;
        while (!condition() && DateTimeOffset.UtcNow < until) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class Protector : IOllamaSecretProtector
    {
        public string Protect(string value) => "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value[10..]));
    }

    private sealed class FakeBackendClient : IOllamaBackendClient
    {
        public bool Disposed { get; private set; }
        public int CheckCalls { get; private set; }
        public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default)
        { CheckCalls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(FakeClient.Completion("ok"));
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSshConnectionService : ISshConnectionService
    {
        public int Calls { get; private set; }
        public List<FakeSshSession> Sessions { get; } = [];
        public FakeSshSession? Session => Sessions.LastOrDefault();
        public Task<ISshSession> ConnectAsync(GpuSshSettings settings, CancellationToken cancellationToken = default)
        { Calls++; var session = new FakeSshSession(); Sessions.Add(session); return Task.FromResult<ISshSession>(session); }
    }

    private sealed class RejectingSshService : ISshConnectionService
    {
        public Task<ISshSession> ConnectAsync(GpuSshSettings settings, CancellationToken cancellationToken = default) =>
            Task.FromException<ISshSession>(new SshHostKeyVerificationException("host key mismatch", "SHA256:new", settings.HostKeyFingerprint));
    }

    private sealed class AlwaysFailSshService : ISshConnectionService
    {
        public int Calls { get; private set; }
        public Task<ISshSession> ConnectAsync(GpuSshSettings settings, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromException<ISshSession>(new IOException("synthetic disconnect")); }
    }

    private sealed class FakeSshSession : ISshSession
    {
        public bool Connected { get; set; } = true;
        public bool IsConnected => Connected && !Disposed;
        public bool Disposed { get; private set; }
        public int RemotePort { get; private set; }
        public FakeTunnel? Tunnel { get; private set; }
        public List<(SshOperation, string?)> Executed { get; } = [];
        public Dictionary<SshOperation, SshCommandResult> Results { get; } = [];
        public Task<SshCommandResult> ExecuteAsync(SshOperation operation, string? argument = null, CancellationToken cancellationToken = default)
        {
            _ = SshCommands.Resolve(operation, argument);
            Executed.Add((operation, argument));
            return Task.FromResult(Results.TryGetValue(operation, out var result) ? result : new SshCommandResult(0, "", ""));
        }
        public Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ISshTunnel> OpenOllamaTunnelAsync(int remotePort, CancellationToken cancellationToken = default)
        { RemotePort = remotePort; Tunnel = new(); return Task.FromResult<ISshTunnel>(Tunnel); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class FakeTunnel : ISshTunnel
    {
        public int LocalPort => 43117;
        public bool IsStarted => !Disposed;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class StubHttpHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
