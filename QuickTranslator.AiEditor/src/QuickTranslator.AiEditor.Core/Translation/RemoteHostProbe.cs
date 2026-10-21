using System.Net.Sockets;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum RemoteEndpointKind { Unreachable, Ssh, Rdp, Http, Ollama, Unknown }

public sealed record RemoteHostProbeResult(RemoteEndpointKind Kind, string? Banner, bool LooksLikeWindows,
    IReadOnlyList<int> OpenCompanionPorts, string Message);

/// <summary>
/// Works out what a rented machine's port speaks without logging in: SSH sends its banner first, RDP answers an
/// X.224 connection request, HTTP answers a GET. Nothing but those fixed probes is ever sent.
/// </summary>
public sealed class RemoteHostProbe
{
    public static readonly int[] CompanionPorts = [22, 3389, 11434];

    private static readonly byte[] RdpConnectionRequest =
        [0x03, 0x00, 0x00, 0x13, 0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00];

    private static readonly byte[] SshClientHello = Encoding.ASCII.GetBytes("SSH-2.0-QTStudioProbe\r\n");

    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _readTimeout;

    public RemoteHostProbe(TimeSpan? connectTimeout = null, TimeSpan? readTimeout = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(4);
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(1.5);
    }

    public async Task<RemoteHostProbeResult> ProbeAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Thiếu địa chỉ máy.", nameof(host));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));

        (bool reachable, byte[] banner) = await ExchangeAsync(host, port, null, cancellationToken).ConfigureAwait(false);
        if (!reachable)
            return new(RemoteEndpointKind.Unreachable, null, false, [],
                $"Không kết nối được {host}:{port}. Kiểm tra IP, cổng và tường lửa của nhà cung cấp.");

        string text = Printable(banner).Trim();
        if (text.StartsWith("SSH-", StringComparison.Ordinal))
        {
            bool windows = text.Contains("Windows", StringComparison.OrdinalIgnoreCase);
            return new(RemoteEndpointKind.Ssh, text, windows, [],
                windows ? "Cổng này là SSH của Windows (OpenSSH). Có thể cài tự động." : "Cổng này là SSH.");
        }

        // A port splitter (SSH and RDP on one port) waits for the client to speak first, so greet it like an SSH client.
        (_, byte[] hello) = await ExchangeAsync(host, port, SshClientHello, cancellationToken).ConfigureAwait(false);
        string helloReply = Printable(hello).Trim();
        if (helloReply.StartsWith("SSH-", StringComparison.Ordinal))
            return new(RemoteEndpointKind.Ssh, helloReply, helloReply.Contains("Windows", StringComparison.OrdinalIgnoreCase), [],
                "Cổng này nhận SSH (có thể dùng chung với Remote Desktop).");

        (_, byte[] rdp) = await ExchangeAsync(host, port, RdpConnectionRequest, cancellationToken).ConfigureAwait(false);
        if (rdp.Length >= 2 && rdp[0] == 0x03 && rdp[1] == 0x00)
            return await WithCompanionsAsync(RemoteEndpointKind.Rdp, null, true, host, port,
                "Cổng này là Remote Desktop (RDP) của Windows, chưa phải SSH.", cancellationToken).ConfigureAwait(false);

        byte[] request = Encoding.ASCII.GetBytes($"GET / HTTP/1.0\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        (_, byte[] http) = await ExchangeAsync(host, port, request, cancellationToken).ConfigureAwait(false);
        string reply = Printable(http);
        if (reply.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            return reply.Contains("Ollama is running", StringComparison.OrdinalIgnoreCase)
                ? new(RemoteEndpointKind.Ollama, null, false, [], "Cổng này là Ollama trực tiếp (không qua SSH, không mã hóa).")
                : new(RemoteEndpointKind.Http, null, reply.Contains("Microsoft", StringComparison.OrdinalIgnoreCase), [],
                    "Cổng này là dịch vụ web, không phải SSH.");
        }

        return new(RemoteEndpointKind.Unknown, null, false, [], "Không nhận ra giao thức của cổng này.");
    }

    public async Task<IReadOnlyList<int>> FindOpenPortsAsync(string host, IEnumerable<int> ports,
        CancellationToken cancellationToken = default)
    {
        int[] list = ports.Distinct().ToArray();
        bool[] open = await Task.WhenAll(list.Select(async port =>
            (await ExchangeAsync(host, port, null, cancellationToken, readBanner: false).ConfigureAwait(false)).Reachable))
            .ConfigureAwait(false);
        return list.Where((_, index) => open[index]).ToArray();
    }

    private async Task<RemoteHostProbeResult> WithCompanionsAsync(RemoteEndpointKind kind, string? banner,
        bool windows, string host, int port, string message, CancellationToken cancellationToken)
    {
        IReadOnlyList<int> open = await FindOpenPortsAsync(host, CompanionPorts.Where(p => p != port), cancellationToken)
            .ConfigureAwait(false);
        string extra = open.Contains(22)
            ? " Máy này cũng mở cổng 22 (SSH) nên có thể dùng ngay."
            : " Chưa thấy cổng SSH (22) mở; cần bật OpenSSH trên máy thuê và nhà cung cấp phải mở cổng đó.";
        return new(kind, banner, windows, open, message + extra);
    }

    private async Task<(bool Reachable, byte[] Data)> ExchangeAsync(string host, int port, byte[]? send,
        CancellationToken cancellationToken, bool readBanner = true)
    {
        using var client = new TcpClient();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(_connectTimeout);
        try { await client.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (false, []); }
        catch (SocketException) { return (false, []); }
        if (!readBanner) return (true, []);
        try
        {
            NetworkStream stream = client.GetStream();
            if (send is not null) await stream.WriteAsync(send, cancellationToken).ConfigureAwait(false);
            var buffer = new byte[512];
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readTimeout.CancelAfter(_readTimeout);
            int count = await stream.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false);
            return (true, buffer[..count]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return (true, []); }
        catch (IOException) { return (true, []); }
        catch (SocketException) { return (true, []); }
    }

    private static string Printable(byte[] data) =>
        new(Encoding.ASCII.GetString(data).Where(c => c is '\r' or '\n' or >= ' ' and <= '~').ToArray());
}
