using System.Collections.Concurrent;
using System.Diagnostics;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class GpuConnectionHandle : IAsyncDisposable
{
    private readonly ISshSession _session;
    private readonly ISshTunnel _tunnel;
    private bool _disposed;
    internal GpuConnectionHandle(OllamaBackendProfile profile, ISshSession session, ISshTunnel tunnel,
        IOllamaBackendClient client)
    { Profile = profile.Clone(); _session = session; _tunnel = tunnel; Client = client; }
    public OllamaBackendProfile Profile { get; }
    public IOllamaBackendClient Client { get; }
    public ISshSession Session => _session;
    public Uri TunnelEndpoint => new($"http://127.0.0.1:{_tunnel.LocalPort}/api/chat");
    public bool IsConnected => !_disposed && _session.IsConnected && _tunnel.IsStarted;
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Client.Dispose();
        await _tunnel.DisposeAsync().ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class GpuConnectionManager : IAsyncDisposable
{
    private readonly ISshConnectionService _ssh;
    private readonly Func<OllamaBackendProfile, Uri, IOllamaBackendClient> _clientFactory;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _healthInterval;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, GpuConnectionHandle> _connections = new();
    private readonly ConcurrentDictionary<Guid, GpuProfileStatus> _statuses = new();
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _monitorCancellations = new();

    public GpuConnectionManager(ISshConnectionService? ssh = null,
        Func<OllamaBackendProfile, Uri, IOllamaBackendClient>? clientFactory = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? healthInterval = null)
    {
        _ssh = ssh ?? new SshConnectionService();
        _clientFactory = clientFactory ?? ((profile, endpoint) =>
        {
            var tunnelProfile = profile.Clone();
            tunnelProfile.Endpoint = endpoint.ToString();
            tunnelProfile.Token = null;
            return OllamaClient.Create(tunnelProfile);
        });
        _delay = delay ?? Task.Delay;
        _healthInterval = healthInterval ?? TimeSpan.FromSeconds(10);
    }

    public event EventHandler<GpuProfileStatus>? StatusChanged;
    public GpuProfileStatus GetStatus(Guid profileId) => _statuses.TryGetValue(profileId, out var status)
        ? status : new(profileId, GpuConnectionState.Disconnected, "Chưa kết nối.");
    public bool TryGetConnection(Guid profileId, out GpuConnectionHandle? handle) =>
        _connections.TryGetValue(profileId, out handle);

    public async Task<GpuConnectionHandle> ConnectAsync(OllamaBackendProfile profile,
        CancellationToken cancellationToken = default)
    {
        GpuConnectionHandle handle = await ConnectCoreAsync(profile, cancellationToken).ConfigureAwait(false);
        StartMonitor(profile.Clone());
        return handle;
    }

    private async Task<GpuConnectionHandle> ConnectCoreAsync(OllamaBackendProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        OllamaBackendProfile copy = profile.Clone();
        copy.Validate();
        if (copy.Kind != OllamaBackendKind.Remote || copy.Ssh is null)
            throw new InvalidOperationException("GPU Connection Manager chỉ kết nối profile Remote có cấu hình SSH.");
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connections.TryGetValue(copy.Id, out GpuConnectionHandle? existing) && existing.IsConnected) return existing;
            SetStatus(new(copy.Id, GpuConnectionState.Connecting, "Đang kết nối SSH…"));
            ISshSession session = await _ssh.ConnectAsync(copy.Ssh, cancellationToken).ConfigureAwait(false);
            try
            {
                ISshTunnel tunnel = await session.OpenOllamaTunnelAsync(copy.Ssh.RemoteOllamaPort, cancellationToken).ConfigureAwait(false);
                Uri endpoint = new($"http://127.0.0.1:{tunnel.LocalPort}/api/chat");
                IOllamaBackendClient client = _clientFactory(copy, endpoint);
                if (copy.Api == BackendApi.OpenAiCompatible)
                {
                    try { await client.CheckConnectionAsync(copy.Model, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false); }
                    catch
                    {
                        client.Dispose();
                        await tunnel.DisposeAsync().ConfigureAwait(false);
                        throw;
                    }
                }
                var handle = new GpuConnectionHandle(copy, session, tunnel, client);
                if (_connections.TryRemove(copy.Id, out var old)) await old.DisposeAsync().ConfigureAwait(false);
                _connections[copy.Id] = handle;
                SetStatus(new(copy.Id, GpuConnectionState.Connected, "SSH tunnel đã sẵn sàng.",
                    TunnelEndpoint: endpoint, CheckedAt: DateTimeOffset.UtcNow));
                return handle;
            }
            catch { await session.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch (SshHostKeyVerificationException error)
        {
            SetStatus(new(copy.Id, GpuConnectionState.TrustRequired, error.Message));
            throw;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SetStatus(new(copy.Id, GpuConnectionState.Offline, SafeMessage(error)));
            throw;
        }
        finally { _connectionGate.Release(); }
    }

    public async Task<GpuConnectionHandle> ReconnectAsync(OllamaBackendProfile profile, int maximumAttempts = 3,
        CancellationToken cancellationToken = default)
    {
        if (maximumAttempts is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        await DisconnectCoreAsync(profile.Id, cancelMonitor: true).ConfigureAwait(false);
        Exception? last = null;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            try
            {
                GpuConnectionHandle handle = await ConnectCoreAsync(profile, cancellationToken).ConfigureAwait(false);
                StartMonitor(profile.Clone());
                return handle;
            }
            catch (SshHostKeyVerificationException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error)
            {
                last = error;
                if (attempt == maximumAttempts) break;
                SetStatus(new(profile.Id, GpuConnectionState.Reconnecting,
                    $"Mất kết nối. Thử lại {attempt}/{maximumAttempts}…"));
                await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
            }
        }
        SetStatus(new(profile.Id, GpuConnectionState.Offline,
            "Không khôi phục được SSH tunnel sau số lần thử giới hạn."));
        throw new InvalidOperationException("Không khôi phục được SSH tunnel.", last);
    }

    public async Task DisconnectAsync(Guid profileId)
    {
        await DisconnectCoreAsync(profileId, cancelMonitor: true).ConfigureAwait(false);
        SetStatus(new(profileId, GpuConnectionState.Disconnected, "Đã ngắt SSH tunnel."));
    }

    private async Task DisconnectCoreAsync(Guid profileId, bool cancelMonitor)
    {
        if (cancelMonitor && _monitorCancellations.TryRemove(profileId, out var monitor))
        { monitor.Cancel(); monitor.Dispose(); }
        if (_connections.TryRemove(profileId, out var connection)) await connection.DisposeAsync().ConfigureAwait(false);
    }

    private void StartMonitor(OllamaBackendProfile profile)
    {
        var cancellation = new CancellationTokenSource();
        if (_monitorCancellations.TryGetValue(profile.Id, out var old)) { old.Cancel(); old.Dispose(); }
        _monitorCancellations[profile.Id] = cancellation;
        _ = MonitorConnectionAsync(profile, cancellation.Token);
    }

    private async Task MonitorConnectionAsync(OllamaBackendProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _delay(_healthInterval, cancellationToken).ConfigureAwait(false);
                if (_connections.TryGetValue(profile.Id, out GpuConnectionHandle? handle) && handle.IsConnected) continue;
                SetStatus(new(profile.Id, GpuConnectionState.Reconnecting,
                    "SSH tunnel bị gián đoạn; đang khôi phục có giới hạn."));
                await DisconnectCoreAsync(profile.Id, cancelMonitor: false).ConfigureAwait(false);
                Exception? last = null;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        await ConnectCoreAsync(profile, cancellationToken).ConfigureAwait(false);
                        StartMonitor(profile.Clone());
                        return;
                    }
                    catch (SshHostKeyVerificationException) { return; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        last = error;
                        if (attempt < 3) await _delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), cancellationToken).ConfigureAwait(false);
                    }
                }
                SetStatus(new(profile.Id, GpuConnectionState.Offline,
                    "Không khôi phục được SSH tunnel. Request chưa có checkpoint sẽ không tự gửi lại."));
                _ = last;
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Guid id in _connections.Keys.ToArray()) await DisconnectAsync(id).ConfigureAwait(false);
        foreach (var monitor in _monitorCancellations.Values) { monitor.Cancel(); monitor.Dispose(); }
        _monitorCancellations.Clear();
        _connectionGate.Dispose();
    }

    private void SetStatus(GpuProfileStatus status)
    {
        _statuses[status.ProfileId] = status;
        StatusChanged?.Invoke(this, status);
    }

    private static string SafeMessage(Exception error) => error switch
    {
        TimeoutException => "Kết nối SSH quá thời gian chờ.",
        _ => "Không kết nối được SSH hoặc Ollama tunnel. Kiểm tra host, quyền truy cập và trạng thái server."
    };
}
