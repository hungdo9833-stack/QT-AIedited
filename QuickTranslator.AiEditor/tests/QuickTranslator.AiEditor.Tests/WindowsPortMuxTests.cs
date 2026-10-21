using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>Compiles the real splitter source with Windows PowerShell (as the rented machine does) and drives it.</summary>
public sealed class MuxFixture : IDisposable
{
    public string Directory { get; } = Path.Combine(Path.GetTempPath(), "qtaie-mux-" + Guid.NewGuid().ToString("N"));
    public string Executable { get; }

    public MuxFixture()
    {
        System.IO.Directory.CreateDirectory(Directory);
        Executable = Path.Combine(Directory, "qt-mux.exe");
        string source = Path.Combine(Directory, "mux.cs");
        string build = Path.Combine(Directory, "build.ps1");
        File.WriteAllText(source, WindowsPortMux.MuxSource);
        File.WriteAllText(build, "param($src, $out)\r\nAdd-Type -TypeDefinition (Get-Content -Raw -LiteralPath $src) " +
                                 "-OutputAssembly $out -OutputType ConsoleApplication");
        var start = new ProcessStartInfo("powershell.exe",
            ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", build, source, Executable])
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(Executable))
            throw new InvalidOperationException("The splitter did not compile: " + output);
    }

    public void Dispose() => System.IO.Directory.Delete(Directory, true);
}

public sealed class WindowsPortMuxTests(MuxFixture fixture) : IClassFixture<MuxFixture>
{
    private static readonly byte[] RdpRequest =
        [0x03, 0x00, 0x00, 0x13, 0x0E, 0xE0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x08, 0x00, 0x03, 0x00, 0x00, 0x00];

    [Fact]
    public async Task Ssh_clients_reach_sshd_and_everything_else_reaches_remote_desktop()
    {
        using var rdp = EchoServer.Start("RDP:");
        using var ssh = EchoServer.Start("SSH:");
        using MuxProcess mux = MuxProcess.Start(fixture.Executable, rdp.Port, ssh.Port);

        Assert.StartsWith("SSH:SSH-2.0-test", await mux.ExchangeAsync(Encoding.ASCII.GetBytes("SSH-2.0-test\r\n")));
        Assert.StartsWith("RDP:", await mux.ExchangeAsync(RdpRequest));
        // A TLS/other first byte is never mistaken for SSH.
        Assert.StartsWith("RDP:", await mux.ExchangeAsync([0x16, 0x03, 0x01, 0x00]));
    }

    [Fact]
    public async Task The_splitter_keeps_a_long_two_way_stream_intact_and_serves_clients_in_parallel()
    {
        using var rdp = EchoServer.Start("RDP:");
        using var ssh = EchoServer.Start("");
        using MuxProcess mux = MuxProcess.Start(fixture.Executable, rdp.Port, ssh.Port);

        byte[] payload = new byte[300_000];
        new Random(7).NextBytes(payload);
        payload[0] = (byte)'S';
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, mux.Port);
        NetworkStream stream = client.GetStream();
        Task write = Task.Run(async () => { await stream.WriteAsync(payload); });
        var received = new byte[payload.Length];
        int total = 0;
        while (total < received.Length)
        {
            int read = await stream.ReadAsync(received.AsMemory(total)).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(read > 0, "The splitter closed the stream early.");
            total += read;
        }
        await write;
        Assert.Equal(payload, received);

        string[] replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => i % 2 == 0
            ? mux.ExchangeAsync(Encoding.ASCII.GetBytes("SSH-2.0-" + i + "\r\n"))
            : mux.ExchangeAsync(RdpRequest)));
        for (int i = 0; i < replies.Length; i++)
            Assert.StartsWith(i % 2 == 0 ? "SSH-2.0-" + i : "RDP:", replies[i]);
    }

    [Fact]
    public async Task Probe_sees_ssh_behind_the_splitter_even_though_the_port_also_serves_remote_desktop()
    {
        using var rdp = EchoServer.Start("RDP:");
        using var ssh = EchoServer.Start("", greeting: "SSH-2.0-OpenSSH_for_Windows_9.5\r\n");
        using MuxProcess mux = MuxProcess.Start(fixture.Executable, rdp.Port, ssh.Port);

        RemoteHostProbeResult result = await new RemoteHostProbe(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(600))
            .ProbeAsync("127.0.0.1", mux.Port);

        Assert.Equal(RemoteEndpointKind.Ssh, result.Kind);
        Assert.True(result.LooksLikeWindows);
    }

    private sealed class MuxProcess : IDisposable
    {
        private readonly Process _process;
        public int Port { get; }
        private MuxProcess(Process process, int port) { _process = process; Port = port; }

        public static MuxProcess Start(string exe, int rdpPort, int sshPort)
        {
            int listen = EchoServer.FreePort();
            var start = new ProcessStartInfo(exe, [listen.ToString(), rdpPort.ToString(), sshPort.ToString()])
            { UseShellExecute = false, CreateNoWindow = true };
            Process process = Process.Start(start)!;
            // Wait until the port accepts connections.
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (true)
            {
                try { using var probe = new TcpClient(); probe.Connect(IPAddress.Loopback, listen); break; }
                catch (SocketException) when (DateTime.UtcNow < until) { Thread.Sleep(50); }
            }
            return new(process, listen);
        }

        public async Task<string> ExchangeAsync(byte[] first)
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, Port);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(first);
            var buffer = new byte[256];
            int read = await stream.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            return Encoding.Latin1.GetString(buffer, 0, read);
        }

        public void Dispose() { try { _process.Kill(true); } catch (InvalidOperationException) { } _process.Dispose(); }
    }

    /// <summary>Answers every connection by echoing what it receives behind a fixed prefix on the first read.</summary>
    private sealed class EchoServer : IDisposable
    {
        private readonly TcpListener _listener;
        public int Port { get; }

        private EchoServer(TcpListener listener, string prefix, string? greeting)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(); } catch { return; }
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                NetworkStream stream = client.GetStream();
                                if (greeting is not null) await stream.WriteAsync(Encoding.ASCII.GetBytes(greeting));
                                var buffer = new byte[16384];
                                bool first = true;
                                int read;
                                while ((read = await stream.ReadAsync(buffer)) > 0)
                                {
                                    byte[] head = first ? Encoding.ASCII.GetBytes(prefix) : [];
                                    first = false;
                                    await stream.WriteAsync(head.Concat(buffer.Take(read)).ToArray());
                                }
                            }
                            catch (IOException) { }
                        }
                    });
                }
            });
        }

        public static EchoServer Start(string prefix, string? greeting = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new(listener, prefix, greeting);
        }

        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose() => _listener.Stop();
    }
}
