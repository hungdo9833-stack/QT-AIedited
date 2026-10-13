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
        var store = new BackendProfileStore(path, new Protector());
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
    public async Task Setup_uses_only_controlled_operations_and_requires_confirmation()
    {
        var session = new FakeSshSession();
        var setup = new RemoteOllamaSetupService();

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.ExecuteConfirmedAsync(session,
            RemoteSetupAction.InstallOllamaUbuntu, confirmed: false));
        await setup.ExecuteConfirmedAsync(session, RemoteSetupAction.PullModel, confirmed: true, "translategemma:12b");
        await Assert.ThrowsAsync<ArgumentException>(() => setup.ExecuteConfirmedAsync(session,
            RemoteSetupAction.PullModel, confirmed: true, "safe; curl bad.example"));

        Assert.Equal([(SshOperation.PullOllamaModel, "translategemma:12b")], session.Executed);
    }

    [Fact]
    public async Task Model_pull_streams_real_byte_progress_only_through_loopback()
    {
        const string body = "{\"status\":\"pulling layer\",\"completed\":524288,\"total\":1048576}\n" +
                            "{\"status\":\"success\"}\n";
        var handler = new StubHttpHandler((request, _) =>
        {
            Assert.Equal("http://127.0.0.1:43117/api/pull", request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Post, request.Method);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        });
        var observed = new List<OllamaModelPullProgress>();
        var service = new RemoteOllamaModelPullService(() => new HttpClient(handler, disposeHandler: false));

        await service.PullAsync(new Uri("http://127.0.0.1:43117/api/chat"), "translategemma:12b",
            new InlineProgress<OllamaModelPullProgress>(observed.Add));

        Assert.Equal(2, observed.Count);
        Assert.Equal(50, observed[0].Percentage);
        Assert.Equal(1048576, observed[0].TotalBytes);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PullAsync(
            new Uri("http://gpu.example.test:11434/api/chat"), "translategemma:12b"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PullAsync(
            new Uri("http://127.0.0.1:43117/api/chat"), "safe;curl-bad"));
    }

    [Fact]
    public async Task Model_pull_honors_cancellation()
    {
        var handler = new StubHttpHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var service = new RemoteOllamaModelPullService(() => new HttpClient(handler, disposeHandler: false));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PullAsync(
            new Uri("http://127.0.0.1:43117/api/chat"), "translategemma:12b", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task Inspection_parses_ubuntu_gpu_disk_service_and_models()
    {
        var session = new FakeSshSession
        {
            Results =
            {
                [SshOperation.ReadOperatingSystem] = new(0, "ID=ubuntu\nPRETTY_NAME=\"Ubuntu 24.04 LTS\"", ""),
                [SshOperation.ReadNvidiaGpu] = new(0, "NVIDIA RTX 3090 Ti, 24564", ""),
                [SshOperation.ReadRootDisk] = new(0, "/dev/vda 1000000 100 800000 1% /", ""),
                [SshOperation.FindOllama] = new(0, "/usr/local/bin/ollama", ""),
                [SshOperation.ReadOllamaService] = new(0, "active", ""),
                [SshOperation.ListOllamaModels] = new(0, "NAME ID SIZE MODIFIED\ntranslategemma:12b abc 8GB now", "")
            }
        };

        RemoteGpuInspection result = await new RemoteOllamaSetupService().InspectAsync(session);

        Assert.Equal("Ubuntu 24.04 LTS", result.OperatingSystem);
        Assert.Equal("NVIDIA RTX 3090 Ti", result.GpuName);
        Assert.Equal(24564L * 1024 * 1024, result.VramBytes);
        Assert.Equal(800000L * 1024, result.FreeDiskBytes);
        Assert.True(result.OllamaInstalled);
        Assert.True(result.OllamaServiceActive);
        Assert.Equal(["translategemma:12b"], result.Models);
        Assert.True(result.SupportsAutomaticSetup);
    }

    [Fact]
    public async Task Ssh_profile_never_uses_saved_endpoint_before_runtime_tunnel_is_attached()
    {
        Directory.CreateDirectory(_root);
        var store = new BackendProfileStore(Path.Combine(_root, "manager.json"), new Protector());
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
        public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
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
