using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum SshOperation
{
    ReadOperatingSystem, ReadWindowsVersion, ReadWindowsInfo, ReadNvidiaGpuWindows, ReadSystemDiskWindows,
    TestLlamaServerExecutable, ExtractLlamaCpp, ConfigureLlamaServerTask, ReadLlamaServerLogTail,
    ReadLlamaCacheSize, ConfirmWindowsMux
}

public interface ISshTunnel : IAsyncDisposable { int LocalPort { get; } bool IsStarted { get; } }

public interface ISshSession : IAsyncDisposable
{
    bool IsConnected { get; }
    Task<SshCommandResult> ExecuteAsync(SshOperation operation, string? argument = null,
        CancellationToken cancellationToken = default);
    Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default);
    Task<ISshTunnel> OpenServerTunnelAsync(int remotePort, CancellationToken cancellationToken = default);
}

public interface ISshConnectionService
{
    Task<ISshSession> ConnectAsync(GpuSshSettings settings, CancellationToken cancellationToken = default);
}

public sealed class SshConnectionService : ISshConnectionService
{
    public async Task<ISshSession> ConnectAsync(GpuSshSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        GpuSshSettings copy = settings.Clone(); copy.Validate();
        PrivateKeyFile? keyFile = null; MemoryStream? keyStream = null;
        AuthenticationMethod authentication;
        if (copy.Authentication == SshAuthenticationKind.Password)
            authentication = new PasswordAuthenticationMethod(copy.Username, copy.Password!);
        else
        {
            keyStream = new(Encoding.UTF8.GetBytes(copy.PrivateKey!), writable: false);
            keyFile = new(keyStream, copy.PrivateKeyPassphrase);
            authentication = new PrivateKeyAuthenticationMethod(copy.Username, keyFile);
        }
        var connection = new ConnectionInfo(copy.Host, copy.Port, copy.Username, authentication) { Timeout = TimeSpan.FromSeconds(15) };
        var client = new SshClient(connection) { KeepAliveInterval = TimeSpan.FromSeconds(20) };
        string? observed = null; bool trusted = false;
        client.HostKeyReceived += (_, e) => { observed = GpuSshSettings.NormalizeFingerprint(e.FingerPrintSHA256); e.CanTrust = trusted = FixedTimeEquals(observed, copy.HostKeyFingerprint); };
        try
        {
            await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!trusted) throw HostKeyError(observed, copy.HostKeyFingerprint);
            return new SshNetSession(client, connection, copy.HostKeyFingerprint, keyFile, keyStream);
        }
        catch (OperationCanceledException) { client.Dispose(); keyFile?.Dispose(); keyStream?.Dispose(); throw; }
        catch (Exception error) when (!trusted && observed is not null && error is not SshHostKeyVerificationException)
        { client.Dispose(); keyFile?.Dispose(); keyStream?.Dispose(); throw HostKeyError(observed, copy.HostKeyFingerprint); }
        catch { client.Dispose(); keyFile?.Dispose(); keyStream?.Dispose(); throw; }
    }

    private static SshHostKeyVerificationException HostKeyError(string? observed, string? expected) =>
        new(string.IsNullOrWhiteSpace(expected) ? "SSH server chưa được tin cậy. Hãy đối chiếu fingerprint rồi lưu trước khi kết nối."
            : "SSH host key đã thay đổi hoặc không khớp. Kết nối bị chặn.", observed ?? "SHA256:unknown", expected);
    private static bool FixedTimeEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        byte[] a = Encoding.UTF8.GetBytes(GpuSshSettings.NormalizeFingerprint(left));
        byte[] b = Encoding.UTF8.GetBytes(GpuSshSettings.NormalizeFingerprint(right));
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private sealed class SshNetSession(SshClient client, ConnectionInfo connection, string? fingerprint,
        PrivateKeyFile? keyFile, MemoryStream? keyStream) : ISshSession
    {
        private bool _disposed;
        public bool IsConnected => !_disposed && client.IsConnected;
        public async Task<SshCommandResult> ExecuteAsync(SshOperation operation, string? argument = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected) throw new InvalidOperationException("SSH chưa kết nối.");
            using SshCommand command = client.CreateCommand(SshCommands.Resolve(operation, argument), Encoding.UTF8);
            await command.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return new(command.ExitStatus ?? -1, command.Result ?? "", command.Error ?? "");
        }

        public async Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsConnected) throw new InvalidOperationException("SSH chưa kết nối.");
            ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
            string destination = Normalize(remotePath), partial = destination + ".partial";
            await using FileStream source = new(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sftp = new SftpClient(connection);
            bool trusted = false;
            sftp.HostKeyReceived += (_, e) =>
                e.CanTrust = trusted = FixedTimeEquals(GpuSshSettings.NormalizeFingerprint(e.FingerPrintSHA256), fingerprint);
            await sftp.ConnectAsync(cancellationToken).ConfigureAwait(false);
            if (!trusted) throw HostKeyError(null, fingerprint);
            await EnsureFoldersAsync(sftp, destination, cancellationToken).ConfigureAwait(false);
            long offset = 0;
            if (await sftp.ExistsAsync(partial, cancellationToken).ConfigureAwait(false))
            {
                offset = (await sftp.GetAttributesAsync(partial, cancellationToken).ConfigureAwait(false)).Size;
                if (offset < 0 || offset > source.Length) offset = 0;
            }
            source.Position = offset; progress?.Report(offset);
            await using (Stream target = await sftp.OpenAsync(partial, offset > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, cancellationToken).ConfigureAwait(false))
            {
                byte[] buffer = new byte[131072];
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(source.Position);
                }
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            if (await sftp.ExistsAsync(destination, cancellationToken).ConfigureAwait(false))
                await sftp.DeleteFileAsync(destination, cancellationToken).ConfigureAwait(false);
            await sftp.RenameFileAsync(partial, destination, cancellationToken).ConfigureAwait(false);
        }

        private static async Task EnsureFoldersAsync(SftpClient sftp, string path, CancellationToken token)
        {
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries); string current = "";
            for (int i = 0; i < parts.Length - 1; i++)
            {
                current += "/" + parts[i];
                if (!await sftp.ExistsAsync(current, token).ConfigureAwait(false))
                    await sftp.CreateDirectoryAsync(current, token).ConfigureAwait(false);
            }
        }
        private static string Normalize(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path); string value = path.Replace('\\', '/');
            if (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':') value = "/" + value;
            if (!value.StartsWith('/') || value.Split('/').Any(p => p == ".."))
                throw new ArgumentException("Đường dẫn SFTP không hợp lệ.", nameof(path));
            return value;
        }

        public Task<ISshTunnel> OpenServerTunnelAsync(int remotePort, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new InvalidOperationException("SSH chưa kết nối.");
            if (remotePort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(remotePort));
            var port = new ForwardedPortLocal("127.0.0.1", 0, "127.0.0.1", (uint)remotePort); client.AddForwardedPort(port);
            try { port.Start(); return Task.FromResult<ISshTunnel>(new SshNetTunnel(client, port)); }
            catch { client.RemoveForwardedPort(port); port.Dispose(); throw; }
        }
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask; _disposed = true;
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
            if (_disposed) return ValueTask.CompletedTask; _disposed = true;
            try { if (port.IsStarted) port.Stop(); }
            finally { client.RemoveForwardedPort(port); port.Dispose(); }
            return ValueTask.CompletedTask;
        }
    }
}

internal static class SshCommands
{
    public static string Resolve(SshOperation operation, string? argument = null) => operation switch
    {
        SshOperation.ReadOperatingSystem => "cat /etc/os-release",
        SshOperation.ReadWindowsVersion => "cmd /c ver",
        SshOperation.ReadWindowsInfo => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ReadInfo),
        SshOperation.ReadNvidiaGpuWindows => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ReadGpu),
        SshOperation.ReadSystemDiskWindows => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ReadFreeDisk),
        SshOperation.TestLlamaServerExecutable => WindowsRemoteScripts.Encode(RemoteLlamaWindowsScripts.TestExecutable),
        SshOperation.ExtractLlamaCpp => WindowsRemoteScripts.Encode(RemoteLlamaWindowsScripts.ExtractPackage),
        SshOperation.ConfigureLlamaServerTask => WindowsRemoteScripts.Encode(RemoteLlamaWindowsScripts.ConfigureTask(argument)),
        SshOperation.ReadLlamaServerLogTail => WindowsRemoteScripts.Encode(RemoteLlamaWindowsScripts.ReadLogTail),
        SshOperation.ReadLlamaCacheSize => WindowsRemoteScripts.Encode(RemoteLlamaWindowsScripts.ReadCacheSize),
        SshOperation.ConfirmWindowsMux => WindowsRemoteScripts.Encode(WindowsRemoteScripts.ConfirmMux),
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };
}
