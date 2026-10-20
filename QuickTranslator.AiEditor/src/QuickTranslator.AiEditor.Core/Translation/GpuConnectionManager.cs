using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class RemoteOllamaSetupService
{
    public async Task<RemoteGpuInspection> InspectAsync(ISshSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        SshCommandResult os = await session.ExecuteAsync(SshOperation.ReadOperatingSystem, cancellationToken: cancellationToken).ConfigureAwait(false);
        SshCommandResult gpu = await session.ExecuteAsync(SshOperation.ReadNvidiaGpu, cancellationToken: cancellationToken).ConfigureAwait(false);
        SshCommandResult disk = await session.ExecuteAsync(SshOperation.ReadRootDisk, cancellationToken: cancellationToken).ConfigureAwait(false);
        SshCommandResult ollama = await session.ExecuteAsync(SshOperation.FindOllama, cancellationToken: cancellationToken).ConfigureAwait(false);
        SshCommandResult service = ollama.Success
            ? await session.ExecuteAsync(SshOperation.ReadOllamaService, cancellationToken: cancellationToken).ConfigureAwait(false)
            : new(1, "", "Ollama chưa được cài.");
        SshCommandResult models = ollama.Success
            ? await session.ExecuteAsync(SshOperation.ListOllamaModels, cancellationToken: cancellationToken).ConfigureAwait(false)
            : new(1, "", "Ollama chưa được cài.");
        string operatingSystem = Value(os.Output, "PRETTY_NAME") ?? os.Output.Trim();
        (string? gpuName, long? vram) = ParseGpu(gpu.Output);
        bool ubuntu = (Value(os.Output, "ID") ?? "").Equals("ubuntu", StringComparison.OrdinalIgnoreCase);
        return new(operatingSystem, gpuName, vram, ParseDisk(disk.Output), ollama.Success,
            service.Success && service.Output.Trim().Equals("active", StringComparison.OrdinalIgnoreCase),
            ParseModels(models.Output), ubuntu,
            !ubuntu && !ollama.Success ? "Tự động cài Ollama hiện chỉ hỗ trợ Ubuntu." : null);
    }

    public async Task<SshCommandResult> ExecuteConfirmedAsync(ISshSession session, RemoteSetupAction action,
        bool confirmed, string? model = null, CancellationToken cancellationToken = default,
        string? sudoPassword = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!confirmed) throw new InvalidOperationException("Thao tác provisioning cần người dùng xác nhận rõ ràng.");
        SshOperation operation = action switch
        {
            RemoteSetupAction.InstallOllamaUbuntu => SshOperation.InstallOllamaUbuntu,
            RemoteSetupAction.StartOllamaService => SshOperation.StartOllamaService,
            RemoteSetupAction.PullModel => SshOperation.PullOllamaModel,
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        SshCommandResult result = await session.ExecuteAsync(operation, model, cancellationToken).ConfigureAwait(false);
        if (result.Success || action == RemoteSetupAction.PullModel || !NeedsSudoPassword(result))
            return result;
        if (string.IsNullOrEmpty(sudoPassword))
            return result with { Error = "SUDO_PASSWORD_REQUIRED: Ubuntu yêu cầu mật khẩu sudo để cài/khởi động Ollama." };
        // The retry sends the secret via SSH stdin, not in the remote command or its arguments.
        SshCommandResult retried = await session.ExecuteWithSudoPasswordAsync(operation, sudoPassword, cancellationToken)
            .ConfigureAwait(false);
        return retried.Success ? retried : retried with
        {
            Error = retried.Error.Contains("incorrect password", StringComparison.OrdinalIgnoreCase)
                ? "Mật khẩu sudo không đúng hoặc tài khoản không đủ quyền."
                : retried.Error
        };
    }

    private static bool NeedsSudoPassword(SshCommandResult result) =>
        !result.Success && (result.Error.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
                            result.Error.Contains("a password is required", StringComparison.OrdinalIgnoreCase));

    private static string? Value(string text, string key)
    {
        foreach (string line in text.Split('\n'))
        {
            int equals = line.IndexOf('=');
            if (equals > 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return line[(equals + 1)..].Trim().Trim('"');
        }
        return null;
    }

    private static (string?, long?) ParseGpu(string output)
    {
        string line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        int comma = line.LastIndexOf(',');
        if (comma < 1 || !long.TryParse(line[(comma + 1)..].Trim(), out long mib)) return (line.Length == 0 ? null : line, null);
        return (line[..comma].Trim(), checked(mib * 1024L * 1024L));
    }

    private static long? ParseDisk(string output)
    {
        string[] columns = Regex.Split(output.Trim(), @"\s+");
        return columns.Length >= 4 && long.TryParse(columns[3], out long kib) ? checked(kib * 1024L) : null;
    }

    private static IReadOnlyList<string> ParseModels(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Skip(1).Select(line => Regex.Split(line.Trim(), @"\s+").FirstOrDefault())
        .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
}

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

public sealed record LocalOllamaStatus(bool Ready, bool Installed, string Message);

public sealed class LocalOllamaService
{
    private readonly Func<IOllamaBackendClient> _clientFactory;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    public LocalOllamaService(Func<IOllamaBackendClient>? clientFactory = null,
        Func<ProcessStartInfo, Process?>? startProcess = null)
    { _clientFactory = clientFactory ?? OllamaClient.CreateLocal; _startProcess = startProcess ?? Process.Start; }

    public async Task<LocalOllamaStatus> CheckAsync(string model, CancellationToken cancellationToken = default)
    {
        try
        {
            using IOllamaBackendClient client = _clientFactory();
            await client.CheckConnectionAsync(model, TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            return new(true, true, "Ollama Local đã sẵn sàng.");
        }
        catch (OllamaRequestException error) when (error.Kind == OllamaErrorKind.Connection)
        {
            return new(false, FindOllamaExecutable() is not null, "Ollama Local chưa chạy.");
        }
    }

    public async Task<LocalOllamaStatus> StartAsync(string model, CancellationToken cancellationToken = default)
    {
        LocalOllamaStatus current = await CheckAsync(model, cancellationToken).ConfigureAwait(false);
        if (current.Ready) return current;
        string executable = FindOllamaExecutable() ?? throw new FileNotFoundException("Không tìm thấy Ollama trên máy Windows.");
        var start = new ProcessStartInfo(executable, "serve")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        _ = _startProcess(start) ?? throw new InvalidOperationException("Không khởi động được Ollama Local.");
        return new(false, true, "Đã gửi lệnh khởi động Ollama; đang chờ server sẵn sàng.");
    }

    private static string? FindOllamaExecutable()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        if (File.Exists(local)) return local;
        string? path = Environment.GetEnvironmentVariable("PATH");
        return path?.Split(Path.PathSeparator).Select(folder => Path.Combine(folder, "ollama.exe")).FirstOrDefault(File.Exists);
    }
}

