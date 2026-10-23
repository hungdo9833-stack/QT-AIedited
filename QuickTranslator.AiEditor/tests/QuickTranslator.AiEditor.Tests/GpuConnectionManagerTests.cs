using System.Text;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
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
        var store = new GpuProfileStore(path, new Protector());
        var remote = RemoteProfile();
        remote.Ssh!.Authentication = SshAuthenticationKind.PrivateKey;
        remote.Ssh.Password = null;
        remote.Ssh.PrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nsynthetic-key-only\n-----END OPENSSH PRIVATE KEY-----";
        remote.Ssh!.PrivateKeyPassphrase = "key-secret";
        var catalog = new GpuProfileCatalog { ActiveProfileId = remote.Id, Profiles = [remote] };

        await store.SaveAsync(catalog);
        GpuProfileCatalog reopened = await store.LoadAsync();
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
            Assert.Equal("http://127.0.0.1:43117/api/chat", endpoint.ToString());
            return client;
        });
        GpuBackendProfile profile = RemoteProfile();
        profile.ParallelRequests = 4;
        profile.Ssh!.ServerPort = 11436;

        await manager.ConnectAsync(profile);

        Assert.Equal(11436, ssh.Session!.RemotePort);
        Assert.Equal(1, client.CheckCalls);
    }

    [Fact]
    public async Task Lost_tunnel_reconnects_with_a_new_session_without_resending_any_prompt()
    {
        var ssh = new FakeSshConnectionService();
        var delay = new ControlledDelay();
        await using var manager = new GpuConnectionManager(ssh, (_, _) => new FakeBackendClient(),
            delay.WaitAsync);
        GpuBackendProfile profile = RemoteProfile();
        await manager.ConnectAsync(profile);

        DelayCall firstCheck = await delay.NextAsync();
        ssh.Sessions[0].Connected = false;
        firstCheck.Release.SetResult();
        await delay.NextAsync(); // The same monitor reaches its next check after reconnecting.

        Assert.Equal(GpuConnectionState.Connected, manager.GetStatus(profile.Id).State);
        Assert.True(ssh.Sessions[0].Disposed);
        Assert.Equal(2, ssh.Calls);
        Assert.True(ssh.Sessions[1].IsConnected);
    }

    [Fact]
    public async Task Disconnect_during_health_check_prevents_publication_and_monitor_start()
    {
        var entered = Signal();
        var release = Signal();
        CancellationToken healthToken = default;
        var client = new FakeBackendClient
        {
            Check = token => { healthToken = token; entered.SetResult(); return release.Task; }
        };
        var ssh = new FakeSshConnectionService();
        var delay = new ControlledDelay();
        await using var manager = new GpuConnectionManager(ssh, (_, _) => client, delay.WaitAsync);
        GpuBackendProfile profile = RemoteProfile();
        var states = new List<GpuConnectionState>();
        manager.StatusChanged += (_, status) => states.Add(status.State);

        Task<GpuConnectionHandle> connect = manager.ConnectAsync(profile);
        await entered.Task;
        await manager.DisconnectAsync(profile.Id);
        Assert.True(healthToken.IsCancellationRequested);
        release.SetResult(); // Deliberately ignore cancellation inside the health check.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);

        Assert.False(manager.TryGetConnection(profile.Id, out _));
        Assert.Equal(GpuConnectionState.Disconnected, manager.GetStatus(profile.Id).State);
        Assert.DoesNotContain(GpuConnectionState.Connected, states);
        Assert.Equal(0, delay.Calls);
        AssertDisposedOnce(ssh.Sessions.Single(), client);
    }

    [Fact]
    public async Task Dispose_during_connect_drains_active_and_waiting_connects_and_is_idempotent()
    {
        var entered = Signal();
        var release = Signal();
        var client = new FakeBackendClient { Check = _ => { entered.SetResult(); return release.Task; } };
        var ssh = new FakeSshConnectionService();
        var delay = new ControlledDelay();
        var manager = new GpuConnectionManager(ssh, (_, _) => client, delay.WaitAsync);
        GpuBackendProfile profile = RemoteProfile();
        Task<GpuConnectionHandle> active = manager.ConnectAsync(profile);
        await entered.Task;
        Task<GpuConnectionHandle> waiting = manager.ConnectAsync(profile);

        Task disposal = manager.DisposeAsync().AsTask();
        Task secondDisposal = manager.DisposeAsync().AsTask();
        Assert.Same(disposal, secondDisposal);
        Assert.False(disposal.IsCompleted);
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Task.WhenAll(disposal, secondDisposal);
        await manager.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.ConnectAsync(profile));
        Assert.False(manager.TryGetConnection(profile.Id, out _));
        Assert.Equal(0, delay.Calls);
        AssertDisposedOnce(ssh.Sessions.Single(), client);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Shutdown_awaits_monitor_before_disposing_its_connection(bool dispose)
    {
        var ssh = new FakeSshConnectionService();
        var client = new FakeBackendClient();
        var delay = new ControlledDelay(ignoreCancellation: true);
        await using var manager = new GpuConnectionManager(ssh, (_, _) => client, delay.WaitAsync);
        GpuBackendProfile profile = RemoteProfile();
        await manager.ConnectAsync(profile);
        DelayCall check = await delay.NextAsync();

        Task shutdown = dispose ? manager.DisposeAsync().AsTask() : manager.DisconnectAsync(profile.Id);
        Task concurrentShutdown = dispose ? manager.DisposeAsync().AsTask() : manager.DisconnectAsync(profile.Id);
        Assert.True(check.Token.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        Assert.False(concurrentShutdown.IsCompleted);
        Assert.False(client.Disposed);
        check.Release.SetResult();
        await Task.WhenAll(shutdown, concurrentShutdown);

        Assert.Equal(1, delay.Calls);
        Assert.Equal(1, ssh.Calls);
        Assert.False(manager.TryGetConnection(profile.Id, out _));
        AssertDisposedOnce(ssh.Sessions.Single(), client);
    }

    [Fact]
    public async Task Reconnect_after_disconnect_uses_a_new_generation_and_monitor()
    {
        var ssh = new FakeSshConnectionService();
        var clients = new List<FakeBackendClient>();
        var delay = new ControlledDelay();
        await using var manager = new GpuConnectionManager(ssh, (_, _) =>
        {
            var client = new FakeBackendClient();
            clients.Add(client);
            return client;
        }, delay.WaitAsync);
        GpuBackendProfile profile = RemoteProfile();
        GpuConnectionHandle first = await manager.ConnectAsync(profile);
        DelayCall firstMonitor = await delay.NextAsync();
        await manager.DisconnectAsync(profile.Id);

        GpuConnectionHandle second = await manager.ReconnectAsync(profile);
        DelayCall secondMonitor = await delay.NextAsync();

        Assert.NotSame(first, second);
        Assert.True(firstMonitor.Token.IsCancellationRequested);
        Assert.False(secondMonitor.Token.IsCancellationRequested);
        Assert.True(manager.TryGetConnection(profile.Id, out var published));
        Assert.Same(second, published);
        Assert.Equal(GpuConnectionState.Connected, manager.GetStatus(profile.Id).State);
        await manager.DisconnectAsync(profile.Id);
        AssertDisposedOnce(ssh.Sessions[0], clients[0]);
        AssertDisposedOnce(ssh.Sessions[1], clients[1]);
    }

    [Fact]
    public async Task Monitor_failure_is_observed_and_reported_as_offline()
    {
        var ssh = new FakeSshConnectionService();
        var client = new FakeBackendClient();
        var delay = new ControlledDelay();
        await using var manager = new GpuConnectionManager(ssh, (_, _) => client, delay.WaitAsync);
        var offline = Signal();
        manager.StatusChanged += (_, status) =>
        {
            if (status.State == GpuConnectionState.Offline) offline.TrySetResult();
        };
        GpuBackendProfile profile = RemoteProfile();
        await manager.ConnectAsync(profile);
        DelayCall check = await delay.NextAsync();

        check.Release.SetException(new IOException("synthetic monitor failure"));
        await offline.Task;
        await manager.DisconnectAsync(profile.Id); // Joins the monitor after its exception handler.

        Assert.Equal(1, delay.Calls);
        Assert.Equal(1, ssh.Calls);
        AssertDisposedOnce(ssh.Sessions.Single(), client);
    }

    [Fact]
    public async Task Disconnect_during_monitor_reconnect_awaits_cleanup_without_restarting_monitor()
    {
        var entered = Signal();
        var release = Signal();
        CancellationToken healthToken = default;
        var firstClient = new FakeBackendClient();
        var secondClient = new FakeBackendClient
        {
            Check = token => { healthToken = token; entered.SetResult(); return release.Task; }
        };
        var ssh = new FakeSshConnectionService();
        var delay = new ControlledDelay();
        await using var manager = new GpuConnectionManager(ssh,
            (_, _) => ssh.Calls == 1 ? firstClient : secondClient, delay.WaitAsync);
        GpuBackendProfile profile = RemoteProfile();
        await manager.ConnectAsync(profile);
        DelayCall check = await delay.NextAsync();
        ssh.Sessions[0].Connected = false;
        check.Release.SetResult();
        await entered.Task;

        Task disconnect = manager.DisconnectAsync(profile.Id);
        Assert.True(healthToken.IsCancellationRequested);
        Assert.False(disconnect.IsCompleted);
        release.SetResult();
        await disconnect;

        Assert.False(manager.TryGetConnection(profile.Id, out _));
        Assert.Equal(GpuConnectionState.Disconnected, manager.GetStatus(profile.Id).State);
        Assert.Equal(1, delay.Calls);
        Assert.Equal(2, ssh.Calls);
        AssertDisposedOnce(ssh.Sessions[0], firstClient);
        AssertDisposedOnce(ssh.Sessions[1], secondClient);
    }

    [Fact]
    public async Task Disconnect_cancels_explicit_reconnect_during_backoff()
    {
        var ssh = new AlwaysFailSshService();
        var delay = new ControlledDelay();
        await using var manager = new GpuConnectionManager(ssh, (_, _) => new FakeBackendClient(), delay.WaitAsync);
        GpuBackendProfile profile = RemoteProfile();
        Task<GpuConnectionHandle> reconnect = manager.ReconnectAsync(profile);
        DelayCall backoff = await delay.NextAsync();

        await manager.DisconnectAsync(profile.Id);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconnect);

        Assert.True(backoff.Token.IsCancellationRequested);
        Assert.Equal(1, ssh.Calls);
        Assert.Equal(GpuConnectionState.Disconnected, manager.GetStatus(profile.Id).State);
    }

    [Fact]
    public async Task Client_factory_failure_disposes_tunnel_and_session_once()
    {
        var ssh = new FakeSshConnectionService();
        await using var manager = new GpuConnectionManager(ssh,
            (_, _) => throw new InvalidOperationException("synthetic factory failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ConnectAsync(RemoteProfile()));

        Assert.Equal(1, ssh.Session!.DisposeCalls);
        Assert.Equal(1, ssh.Session.Tunnel!.DisposeCalls);
    }

    [Fact]
    public async Task Dispose_joins_an_already_disposing_owned_handle()
    {
        var release = Signal();
        var ssh = new FakeSshConnectionService();
        var client = new FakeBackendClient();
        var delay = new ControlledDelay();
        var manager = new GpuConnectionManager(ssh, (_, _) => client, delay.WaitAsync);
        GpuConnectionHandle handle = await manager.ConnectAsync(RemoteProfile());
        await delay.NextAsync();
        ssh.Session!.DisposeCallback = () => release.Task;

        Task handleDisposal = handle.DisposeAsync().AsTask();
        Task secondHandleDisposal = handle.DisposeAsync().AsTask();
        Task managerDisposal = manager.DisposeAsync().AsTask();
        Assert.Same(handleDisposal, secondHandleDisposal);
        Assert.False(handle.IsConnected);
        Assert.False(managerDisposal.IsCompleted);
        release.SetResult();
        await Task.WhenAll(handleDisposal, secondHandleDisposal, managerDisposal);
        await manager.DisposeAsync();

        AssertDisposedOnce(ssh.Sessions.Single(), client);
    }

    [Fact]
    public async Task Host_key_failure_is_blocking_and_never_leaks_credentials()
    {
        var service = new RejectingSshService();
        await using var manager = new GpuConnectionManager(service, (_, _) => new FakeBackendClient());
        GpuBackendProfile profile = RemoteProfile();

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
        var store = new GpuProfileStore(Path.Combine(_root, "manager.json"), new Protector());
        var remote = RemoteProfile();
        await store.SaveAsync(new() { ActiveProfileId = remote.Id, Profiles = [remote] });
        using var manager = new GpuBackendManager(store);
        await manager.LoadAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TranslateAsync("must-not-leave", new()));

        var runtime = new FakeBackendClient();
        await manager.AttachRuntimeClientAsync(remote.Id, runtime);
        LlmCompletion completion = await manager.TranslateAsync("synthetic", new());
        Assert.Equal("ok", completion.Text);
        await manager.DetachRuntimeClientAsync(remote.Id);
        Assert.False(runtime.Disposed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.TranslateAsync("must-not-leave-2", new()));
    }

    private static GpuBackendProfile RemoteProfile() => new()
    {
        Id = Guid.NewGuid(), Name = "EZYCloudX RTX 3090 Ti", Kind = GpuBackendKind.Remote,
        Endpoint = "http://127.0.0.1:11434", Model = "translategemma:12b", RemoteDisclosureAccepted = true,
        HourlyPrice = 0.42m,
        Ssh = new() { Host = "gpu.example.test", Port = 2222, Username = "ubuntu", Password = "ssh-secret",
            Authentication = SshAuthenticationKind.Password, HostKeyFingerprint = "SHA256:abcdefghijklmnopqrstuv" }
    };

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void AssertDisposedOnce(FakeSshSession session, FakeBackendClient client)
    {
        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(1, session.Tunnel!.DisposeCalls);
        Assert.Equal(1, session.DisposeCalls);
    }

    private sealed record DelayCall(CancellationToken Token, TaskCompletionSource Release);

    private sealed class ControlledDelay(bool ignoreCancellation = false)
    {
        private readonly Channel<DelayCall> _calls = Channel.CreateUnbounded<DelayCall>();
        private int _count;
        public int Calls => Volatile.Read(ref _count);
        public Task WaitAsync(TimeSpan _, CancellationToken token)
        {
            var call = new DelayCall(token, Signal());
            Interlocked.Increment(ref _count);
            _calls.Writer.TryWrite(call);
            return ignoreCancellation ? call.Release.Task : call.Release.Task.WaitAsync(token);
        }
        public ValueTask<DelayCall> NextAsync() => _calls.Reader.ReadAsync();
    }

    private sealed class Protector : ISecretProtector
    {
        public string Protect(string value) => "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value[10..]));
    }

    private sealed class FakeBackendClient : ILlmBackendClient
    {
        public Func<CancellationToken, Task>? Check { get; init; }
        public int DisposeCalls { get; private set; }
        public bool Disposed { get; private set; }
        public int CheckCalls { get; private set; }
        public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default)
        { CheckCalls++; return Check?.Invoke(cancellationToken) ?? Task.CompletedTask; }
        public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(FakeClient.Completion("ok"));
        public void Dispose() { DisposeCalls++; Disposed = true; }
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
        public Func<Task>? DisposeCallback { get; set; }
        public int DisposeCalls { get; private set; }
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
        public Task<ISshTunnel> OpenServerTunnelAsync(int remotePort, CancellationToken cancellationToken = default)
        { RemotePort = remotePort; Tunnel = new(); return Task.FromResult<ISshTunnel>(Tunnel); }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++; Disposed = true;
            return new(DisposeCallback?.Invoke() ?? Task.CompletedTask);
        }
    }

    private sealed class FakeTunnel : ISshTunnel
    {
        public int DisposeCalls { get; private set; }
        public int LocalPort => 43117;
        public bool IsStarted => !Disposed;
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync() { DisposeCalls++; Disposed = true; return ValueTask.CompletedTask; }
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
