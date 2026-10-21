using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum SshOperation
{
    ReadOperatingSystem,
    ReadNvidiaGpu,
    ReadRootDisk,
    FindOllama,
    ReadOllamaService,
    ListOllamaModels,
    InstallOllamaUbuntu,
    StartOllamaService,
    PullOllamaModel,
    // Windows hosts (rented GPU machines that expose the OpenSSH server).
    ReadWindowsVersion,
    ReadWindowsInfo,
    ReadNvidiaGpuWindows,
    ReadSystemDiskWindows,
    FindOllamaWindows,
    ReadOllamaServiceWindows,
    ListOllamaModelsWindows,
    WriteWindowsProvisionChunk,
    FinalizeWindowsProvisionScript,
    StartWindowsProvision,
    ReadWindowsProvisionStatus
}

public interface ISshTunnel : IAsyncDisposable
{
    int LocalPort { get; }
    bool IsStarted { get; }
}

public interface ISshSession : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<SshCommandResult> ExecuteAsync(SshOperation operation, string? argument = null,
        CancellationToken cancellationToken = default);
    // Only setup operations may request sudo credentials; default keeps existing test doubles compatible.
    Task<SshCommandResult> ExecuteWithSudoPasswordAsync(SshOperation operation, string password,
        CancellationToken cancellationToken = default) =>
        Task.FromException<SshCommandResult>(new NotSupportedException("SSH session does not support sudo via stdin."));
    Task<ISshTunnel> OpenOllamaTunnelAsync(int remotePort, CancellationToken cancellationToken = default);
}

public interface ISshConnectionService
{
    Task<ISshSession> ConnectAsync(GpuSshSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SshConnectionService : ISshConnectionService
{
    public async Task<ISshSession> ConnectAsync(GpuSshSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        GpuSshSettings copy = settings.Clone();
        copy.Validate();
        AuthenticationMethod authentication;
        PrivateKeyFile? keyFile = null;
        MemoryStream? keyStream = null;
        if (copy.Authentication == SshAuthenticationKind.Password)
        {
            authentication = new PasswordAuthenticationMethod(copy.Username, copy.Password!);
        }
        else
        {
            keyStream = new MemoryStream(Encoding.UTF8.GetBytes(copy.PrivateKey!), writable: false);
            keyFile = new PrivateKeyFile(keyStream, copy.PrivateKeyPassphrase);
            authentication = new PrivateKeyAuthenticationMethod(copy.Username, keyFile);
        }

        var connection = new ConnectionInfo(copy.Host, copy.Port, copy.Username, authentication)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        var client = new SshClient(connection) { KeepAliveInterval = TimeSpan.FromSeconds(20) };
        string? observed = null;
        bool trusted = false;
        client.HostKeyReceived += (_, eventArgs) =>
        {
            observed = GpuSshSettings.NormalizeFingerprint(eventArgs.FingerPrintSHA256);
            trusted = FixedTimeEquals(observed, copy.HostKeyFingerprint);
            eventArgs.CanTrust = trusted;
        };
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!trusted)
                throw HostKeyError(observed, copy.HostKeyFingerprint);
            return new SshNetSession(client, keyFile, keyStream);
        }
        catch (OperationCanceledException) { client.Dispose(); keyFile?.Dispose(); keyStream?.Dispose(); throw; }
        catch (Exception error) when (!trusted && observed is not null && error is not SshHostKeyVerificationException)
        {
            client.Dispose(); keyFile?.Dispose(); keyStream?.Dispose();
            throw HostKeyError(observed, copy.HostKeyFingerprint);
        }
        catch
        {
            client.Dispose(); keyFile?.Dispose(); keyStream?.Dispose();
            throw;
        }
    }

    private static SshHostKeyVerificationException HostKeyError(string? observed, string? expected) =>
        new(string.IsNullOrWhiteSpace(expected)
                ? "SSH server chưa được tin cậy. Hãy đối chiếu fingerprint ngoài băng rồi lưu fingerprint trước khi kết nối."
                : "SSH host key đã thay đổi hoặc không khớp. Kết nối bị chặn.",
            observed ?? "SHA256:unknown", expected);

    private static bool FixedTimeEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        byte[] leftBytes = Encoding.UTF8.GetBytes(GpuSshSettings.NormalizeFingerprint(left));
        byte[] rightBytes = Encoding.UTF8.GetBytes(GpuSshSettings.NormalizeFingerprint(right));
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed class SshNetSession(SshClient client, PrivateKeyFile? keyFile, MemoryStream? keyStream) : ISshSession
    {
        private bool _disposed;
        public bool IsConnected => !_disposed && client.IsConnected;

        public async Task<SshCommandResult> ExecuteAsync(SshOperation operation, string? argument = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected) throw new InvalidOperationException("SSH chưa kết nối.");
            string commandText = SshCommands.Resolve(operation, argument);
            using SshCommand command = client.CreateCommand(commandText, Encoding.UTF8);
            await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new(command.ExitStatus ?? -1, command.Result ?? "", command.Error ?? "");
        }

        public async Task<SshCommandResult> ExecuteWithSudoPasswordAsync(SshOperation operation, string password,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected) throw new InvalidOperationException("SSH chưa kết nối.");
            if (operation is not (SshOperation.InstallOllamaUbuntu or SshOperation.StartOllamaService))
                throw new InvalidOperationException("Chỉ thao tác cài/khởi động Ollama mới được dùng sudo.");
            if (string.IsNullOrEmpty(password) || password.Contains('\n') || password.Contains('\r'))
                throw new ArgumentException("Mật khẩu sudo không hợp lệ.", nameof(password));

            // Password is written to the SSH command stdin, never to the remote command text,
            // process arguments, shell history, profile files or diagnostics.
            using SshCommand command = client.CreateCommand(SshCommands.Resolve(operation, sudoStdin: true), Encoding.UTF8);
            Task executing = command.ExecuteAsync(cancellationToken);
            using (Stream input = command.CreateInputStream())
            {
                byte[] secretBytes = Encoding.UTF8.GetBytes(password + "\n");
                try
                {
                    await input.WriteAsync(secretBytes, cancellationToken).ConfigureAwait(false);
                    await input.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(secretBytes); }
            }
            await executing.ConfigureAwait(false);
            return new(command.ExitStatus ?? -1, Redact(command.Result ?? "", password),
                Redact(command.Error ?? "", password));
        }

        private static string Redact(string value, string secret) =>
            value.Replace(secret, "[redacted]", StringComparison.Ordinal);

        public Task<ISshTunnel> OpenOllamaTunnelAsync(int remotePort,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new InvalidOperationException("SSH chưa kết nối.");
            if (remotePort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(remotePort));
            var port = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)remotePort);
            client.AddForwardedPort(port);
            try
            {
                port.Start();
                return Task.FromResult<ISshTunnel>(new SshNetTunnel(client, port));
            }
            catch
            {
                client.RemoveForwardedPort(port);
                port.Dispose();
                throw;
            }
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            try { if (client.IsConnected) client.Disconnect(); }
            finally { client.Dispose(); keyFile?.Dispose(); keyStream?.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SshNetTunnel(SshClient client, ForwardedPortLocal port) : ISshTunnel
    {
        private bool _disposed;
        public int LocalPort => checked((int)port.BoundPort);
        public bool IsStarted => !_disposed && port.IsStarted;
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            _disposed = true;
            try { if (port.IsStarted) port.Stop(); }
            finally
            {
                client.RemoveForwardedPort(port);
                port.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }
}

internal static partial class SshCommands
{
    public static string Resolve(SshOperation operation, string? argument = null, bool sudoStdin = false) => operation switch
    {
        SshOperation.ReadOperatingSystem => "cat /etc/os-release",
        SshOperation.ReadNvidiaGpu => "nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits",
        SshOperation.ReadRootDisk => "df -Pk / | tail -n 1",
        SshOperation.FindOllama => "command -v ollama",
        SshOperation.ReadOllamaService => "systemctl is-active ollama",
        SshOperation.ListOllamaModels => "ollama list",
        SshOperation.InstallOllamaUbuntu => "curl -fsSL https://ollama.com/install.sh -o /tmp/qt-studio-ollama-install.sh && " +
            "if [ \"$(id -u)\" -eq 0 ]; then sh /tmp/qt-studio-ollama-install.sh; else " +
            (sudoStdin ? "sudo -S -p ''" : "sudo -n") + " sh /tmp/qt-studio-ollama-install.sh; fi; " +
            "code=$?; rm -f /tmp/qt-studio-ollama-install.sh; exit $code",
        SshOperation.StartOllamaService =>
            (sudoStdin ? "sudo -S -p ''" : "sudo -n") + " systemctl enable --now ollama",
        SshOperation.PullOllamaModel => "ollama pull " + QuoteModel(argument),
        SshOperation.ReadWindowsVersion => "cmd /c ver",
        SshOperation.ReadWindowsInfo => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ReadInfo),
        SshOperation.ReadNvidiaGpuWindows => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ReadGpu),
        SshOperation.ReadSystemDiskWindows => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ReadFreeDisk),
        SshOperation.FindOllamaWindows => WindowsRemoteScripts.Encode(WindowsRemoteScripts.FindOllama),
        SshOperation.ReadOllamaServiceWindows => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ReadOllamaService),
        SshOperation.ListOllamaModelsWindows => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ListModels),
        SshOperation.WriteWindowsProvisionChunk => WindowsRemoteScripts.Encode(ProvisionChunk(argument)),
        SshOperation.FinalizeWindowsProvisionScript => WindowsRemoteScripts.Encode(WindowsRemoteScripts.FinalizeProvisionScript),
        SshOperation.StartWindowsProvision => WindowsRemoteScripts.Encode(WindowsRemoteScripts.StartProvision),
        SshOperation.ReadWindowsProvisionStatus => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ReadProvisionStatus),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    private static string ProvisionChunk(string? argument)
    {
        // "<index>:<base64>" — the only caller-controlled text that reaches a Windows command line.
        Match match = ChunkRegex().Match(argument ?? "");
        if (!match.Success) throw new ArgumentException("Đoạn script provisioning không hợp lệ.", nameof(argument));
        return WindowsRemoteScripts.WriteProvisionChunk(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            match.Groups[2].Value);
    }

    [GeneratedRegex(@"^(\d{1,4}):([A-Za-z0-9+/]{1,2100}={0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex ChunkRegex();

    private static string QuoteModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200 || !ModelNameRegex().IsMatch(model))
            throw new ArgumentException("Tên model Ollama không hợp lệ.", nameof(model));
        return "'" + model + "'";
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/-]{0,199}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelNameRegex();
}

