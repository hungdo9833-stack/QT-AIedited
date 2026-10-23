using System.Collections.Concurrent;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class GpuConnectionHandle : IAsyncDisposable
{
    private readonly ISshSession _session;
    private readonly ISshTunnel _tunnel;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    internal GpuConnectionHandle(GpuBackendProfile profile, ISshSession session, ISshTunnel tunnel,
        ILlmBackendClient client)
    { Profile = profile.Clone(); _session = session; _tunnel = tunnel; Client = client; }
    public GpuBackendProfile Profile { get; }
    public ILlmBackendClient Client { get; }
    public ISshSession Session => _session;
    public Uri TunnelEndpoint => new($"http://127.0.0.1:{_tunnel.LocalPort}/api/chat");
    public bool IsConnected => Volatile.Read(ref _disposeTask) is null && _session.IsConnected && _tunnel.IsStarted;
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        lock (_disposeGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
        }
        _ = DisposeCoreAsync(completion);
        return new(completion.Task);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource completion)
    {
        try
        {
            try { Client.Dispose(); }
            finally
            {
                try { await _tunnel.DisposeAsync().ConfigureAwait(false); }
                finally { await _session.DisposeAsync().ConfigureAwait(false); }
            }
            completion.SetResult();
        }
        catch (Exception error) { completion.SetException(error); }
    }
}

public sealed class GpuConnectionManager : IAsyncDisposable
{
    private readonly ISshConnectionService _ssh;
    private readonly Func<GpuBackendProfile, Uri, ILlmBackendClient> _clientFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _healthInterval;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, GpuConnectionHandle> _connections = new();
    private readonly ConcurrentDictionary<Guid, GpuProfileStatus> _statuses = new();
    // A lifetime's identity is the generation token. Publication and retirement share this lock.
    private readonly object _lifetimeGate = new();
    private readonly Dictionary<Guid, ConnectionLifetime> _lifetimes = new();
    private readonly Dictionary<Guid, Task> _shutdowns = new();
    private readonly HashSet<Task> _operations = [];
    private readonly List<Task> _retirements = [];
    private Task? _disposeTask;

    public GpuConnectionManager(ISshConnectionService? ssh = null,
        Func<GpuBackendProfile, Uri, ILlmBackendClient>? clientFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? healthInterval = null)
    {
        _ssh = ssh ?? new SshConnectionService();
        _clientFactory = clientFactory ?? ((profile, endpoint) =>
        {
            var tunnelProfile = profile.Clone();
            tunnelProfile.Endpoint = endpoint.ToString();
            tunnelProfile.Token = null;
            return LlamaServerClient.Create(tunnelProfile);
        });
        _delay = delay ?? Task.Delay;
        _healthInterval = healthInterval ?? TimeSpan.FromSeconds(10);
    }

    public event EventHandler<GpuProfileStatus>? StatusChanged;
    public GpuProfileStatus GetStatus(Guid profileId) => _statuses.TryGetValue(profileId, out var status)
        ? status : new(profileId, GpuConnectionState.Disconnected, "Chưa kết nối.");
    public bool TryGetConnection(Guid profileId, out GpuConnectionHandle? handle) =>
        _connections.TryGetValue(profileId, out handle);

    public async Task<GpuConnectionHandle> ConnectAsync(GpuBackendProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using Operation operation = BeginOperation(profile.Id);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, operation.Lifetime!.Cancellation.Token);
        return await ConnectCoreAsync(profile, operation.Lifetime, cancellation.Token).ConfigureAwait(false);
    }

    private async Task<GpuConnectionHandle> ConnectCoreAsync(GpuBackendProfile profile,
        ConnectionLifetime lifetime, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        GpuBackendProfile copy = profile.Clone();
        copy.Validate();
        if (copy.Kind != GpuBackendKind.Remote || copy.Ssh is null)
            throw new InvalidOperationException("GPU Connection Manager chỉ kết nối profile Remote có cấu hình SSH.");
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        GpuConnectionHandle? pending = null;
        try
        {
            lock (_lifetimeGate)
            {
                ThrowIfRetired(copy.Id, lifetime, cancellationToken);
                if (_connections.TryGetValue(copy.Id, out GpuConnectionHandle? existing) && existing.IsConnected)
                {
                    StartMonitor(copy, lifetime);
                    return existing;
                }
                SetStatus(new(copy.Id, GpuConnectionState.Connecting, "Đang kết nối SSH…"));
            }
            pending = await CreateConnectionAsync(copy, cancellationToken).ConfigureAwait(false);
            GpuConnectionHandle? old;
            lock (_lifetimeGate)
            {
                ThrowIfRetired(copy.Id, lifetime, cancellationToken);
                _connections.TryRemove(copy.Id, out old);
            }
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
            lock (_lifetimeGate)
            {
                // A backend may ignore cancellation, so the generation must also be checked here.
                ThrowIfRetired(copy.Id, lifetime, cancellationToken);
                GpuConnectionHandle handle = pending;
                _connections[copy.Id] = handle;
                pending = null; // Ownership passes to the manager only at publication.
                SetStatus(new(copy.Id, GpuConnectionState.Connected, "SSH tunnel đã sẵn sàng.",
                    TunnelEndpoint: handle.TunnelEndpoint, CheckedAt: DateTimeOffset.UtcNow));
                StartMonitor(copy, lifetime);
                return handle;
            }
        }
        catch (SshHostKeyVerificationException error)
        {
            SetLifetimeStatus(lifetime, new(copy.Id, GpuConnectionState.TrustRequired, error.Message));
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SetLifetimeStatus(lifetime, new(copy.Id, GpuConnectionState.Offline, SafeMessage(error)));
            throw;
        }
        finally
        {
            try { if (pending is not null) await pending.DisposeAsync().ConfigureAwait(false); }
            finally { _connectionGate.Release(); }
        }
    }

    private async Task<GpuConnectionHandle> CreateConnectionAsync(GpuBackendProfile profile,
        CancellationToken cancellationToken)
    {
        ISshSession session = await _ssh.ConnectAsync(profile.Ssh!, cancellationToken).ConfigureAwait(false);
        ISshTunnel? tunnel = null;
        ILlmBackendClient? client = null;
        bool transferred = false;
        try
        {
            tunnel = await session.OpenServerTunnelAsync(profile.Ssh!.ServerPort, cancellationToken).ConfigureAwait(false);
            client = _clientFactory(profile, new($"http://127.0.0.1:{tunnel.LocalPort}/api/chat"));
            await client.CheckConnectionAsync(profile.Model, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            var handle = new GpuConnectionHandle(profile, session, tunnel, client);
            transferred = true;
            return handle;
        }
        finally
        {
            if (!transferred)
            {
                try { client?.Dispose(); }
                finally
                {
                    try { if (tunnel is not null) await tunnel.DisposeAsync().ConfigureAwait(false); }
                    finally { await session.DisposeAsync().ConfigureAwait(false); }
                }
            }
        }
    }

    public async Task<GpuConnectionHandle> ReconnectAsync(GpuBackendProfile profile, int maximumAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        if (maximumAttempts is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        ArgumentNullException.ThrowIfNull(profile);
        using Operation operation = BeginOperation(profile.Id, reconnect: true);
        ConnectionLifetime lifetime = operation.Lifetime!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Cancellation.Token);
        cancellationToken = cancellation.Token;
        await operation.PreviousShutdown.ConfigureAwait(false);
        Exception? last = null;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                return await ConnectCoreAsync(profile, lifetime, cancellationToken).ConfigureAwait(false);
            }
            catch (SshHostKeyVerificationException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                last = error;
                if (attempt == maximumAttempts) break;
                SetLifetimeStatus(lifetime, new(profile.Id, GpuConnectionState.Reconnecting,
                    $"Mất kết nối. Thử lại {attempt}/{maximumAttempts}…"));
                await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
            }
        }
        SetLifetimeStatus(lifetime, new(profile.Id, GpuConnectionState.Offline,
            "Không khôi phục được SSH tunnel sau số lần thử giới hạn."));
        throw new InvalidOperationException("Không khôi phục được SSH tunnel.", last);
    }

    public async Task DisconnectAsync(Guid profileId)
    {
        using Operation operation = BeginOperation();
        Task shutdown;
        lock (_lifetimeGate)
        {
            shutdown = StopConnection(profileId);
        }
        await shutdown.ConfigureAwait(false);
        lock (_lifetimeGate)
        {
            if (!_lifetimes.ContainsKey(profileId))
                SetStatus(new(profileId, GpuConnectionState.Disconnected, "Đã ngắt SSH tunnel."));
        }
    }

    // Called under _lifetimeGate. Retiring the generation prevents both publication and monitor restart.
    private Task StopConnection(Guid profileId, bool reportDisconnected = false)
    {
        _lifetimes.Remove(profileId, out var lifetime);
        if (lifetime is not null)
        {
            lifetime.CancellationTask = lifetime.Cancellation.CancelAsync();
            _retirements.RemoveAll(task => task.IsCompletedSuccessfully);
            _retirements.Add(RetireAsync(lifetime));
        }
        _connections.TryRemove(profileId, out var connection);
        Task shutdown = FinishDisconnectAsync(lifetime, connection,
            reportDisconnected && connection is not null ? profileId : null);
        // Concurrent disconnects also join monitors/cleanup already detached by an earlier caller.
        if (_shutdowns.TryGetValue(profileId, out var previous) && !previous.IsCompletedSuccessfully)
            shutdown = Task.WhenAll(previous, shutdown);
        _shutdowns[profileId] = shutdown;
        return shutdown;
    }

    private static async Task RetireAsync(ConnectionLifetime lifetime)
    {
        try
        {
            await Task.WhenAll(lifetime.Operations.Append(lifetime.MonitorTask)
                .Append(lifetime.CancellationTask)).ConfigureAwait(false);
        }
        finally { lifetime.Cancellation.Dispose(); }
    }

    private async Task FinishDisconnectAsync(ConnectionLifetime? lifetime, GpuConnectionHandle? connection,
        Guid? disconnectedProfileId = null)
    {
        // Monitors must stop using a published handle before it is disposed. A pending connect owns
        // its unpublished resources and cleans them up when it unwinds, even if it ignores cancellation.
        try
        {
            if (lifetime is not null)
                await Task.WhenAll(lifetime.CancellationTask, lifetime.MonitorTask).ConfigureAwait(false);
        }
        finally { if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false); }
        if (disconnectedProfileId is Guid id)
        {
            lock (_lifetimeGate)
                SetStatus(new(id, GpuConnectionState.Disconnected, "Đã ngắt SSH tunnel."));
        }
    }

    private void StartMonitor(GpuBackendProfile profile, ConnectionLifetime lifetime)
    {
        // Publication and this registration are atomic with respect to disconnect/dispose.
        if (!IsCurrent(profile.Id, lifetime) || !lifetime.MonitorTask.IsCompleted) return;
        lifetime.MonitorTask = Task.Run(() => MonitorConnectionAsync(profile, lifetime));
    }

    private async Task MonitorConnectionAsync(GpuBackendProfile profile, ConnectionLifetime lifetime)
    {
        CancellationToken cancellationToken = lifetime.Cancellation.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _delay(_healthInterval, cancellationToken).ConfigureAwait(false);
                GpuConnectionHandle? previous;
                lock (_lifetimeGate)
                {
                    ThrowIfRetired(profile.Id, lifetime, cancellationToken);
                    if (_connections.TryGetValue(profile.Id, out var handle) && handle.IsConnected) continue;
                    SetStatus(new(profile.Id, GpuConnectionState.Reconnecting,
                        "SSH tunnel bị gián đoạn; đang khôi phục có giới hạn."));
                    _connections.TryRemove(profile.Id, out previous);
                }
                if (previous is not null) await previous.DisposeAsync().ConfigureAwait(false);
                bool reconnected = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        await ConnectCoreAsync(profile, lifetime, cancellationToken).ConfigureAwait(false);
                        reconnected = true;
                        break;
                    }
                    catch (SshHostKeyVerificationException) { return; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception)
                    {
                        if (attempt < 3) await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
                    }
                }
                if (reconnected) continue;
                SetLifetimeStatus(lifetime, new(profile.Id, GpuConnectionState.Offline,
                    "Không khôi phục được SSH tunnel. Request chưa có checkpoint sẽ không tự gửi lại."));
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            SetLifetimeStatus(lifetime, new(profile.Id, GpuConnectionState.Offline, SafeMessage(error)));
        }
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource completion;
        Task[] operations;
        lock (_lifetimeGate)
        {
            if (_disposeTask is not null) return new(_disposeTask);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _disposeTask = completion.Task;
            foreach (Guid id in _lifetimes.Keys.ToArray())
            {
                StopConnection(id, reportDisconnected: true);
            }
            operations = _operations.Concat(_retirements).Concat(_shutdowns.Values).ToArray();
        }
        _ = CompleteDisposeAsync(operations, completion);
        return new(completion.Task);
    }

    private async Task CompleteDisposeAsync(Task[] operations, TaskCompletionSource completion)
    {
        try
        {
            try { await Task.WhenAll(operations).ConfigureAwait(false); }
            finally { _connectionGate.Dispose(); }
            completion.SetResult();
        }
        catch (Exception error) { completion.SetException(error); }
    }

    private Operation BeginOperation(Guid? profileId = null, bool reconnect = false)
    {
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            var operation = new Operation(this);
            if (profileId is Guid id)
            {
                if (reconnect)
                    operation.PreviousShutdown = StopConnection(id);
                if (!_lifetimes.TryGetValue(id, out var lifetime))
                    _lifetimes[id] = lifetime = new();
                operation.Lifetime = lifetime;
                lifetime.Operations.Add(operation.Completion.Task);
            }
            _operations.Add(operation.Completion.Task);
            return operation;
        }
    }

    private bool IsCurrent(Guid profileId, ConnectionLifetime lifetime) =>
        _disposeTask is null && _lifetimes.TryGetValue(profileId, out var current) && ReferenceEquals(current, lifetime);

    private void ThrowIfRetired(Guid profileId, ConnectionLifetime lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrent(profileId, lifetime)) throw new OperationCanceledException(lifetime.Cancellation.Token);
    }

    private void SetLifetimeStatus(ConnectionLifetime lifetime, GpuProfileStatus status)
    {
        lock (_lifetimeGate)
            if (IsCurrent(status.ProfileId, lifetime)) SetStatus(status);
    }

    private sealed class ConnectionLifetime
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public HashSet<Task> Operations { get; } = [];
        public Task MonitorTask { get; set; } = Task.CompletedTask;
        public Task CancellationTask { get; set; } = Task.CompletedTask;
    }

    private sealed class Operation(GpuConnectionManager owner) : IDisposable
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConnectionLifetime? Lifetime;
        public Task PreviousShutdown = Task.CompletedTask;

        public void Dispose()
        {
            lock (owner._lifetimeGate)
            {
                Lifetime?.Operations.Remove(Completion.Task);
                owner._operations.Remove(Completion.Task);
                Completion.SetResult();
            }
        }
    }

    private void SetStatus(GpuProfileStatus status)
    {
        _statuses[status.ProfileId] = status;
        StatusChanged?.Invoke(this, status);
    }

    private static string SafeMessage(Exception error) => error switch
    {
        TimeoutException => "Kết nối SSH quá thời gian chờ.",
        _ => "Không kết nối được SSH hoặc llama-server tunnel. Kiểm tra host, quyền truy cập và trạng thái server."
    };
}
