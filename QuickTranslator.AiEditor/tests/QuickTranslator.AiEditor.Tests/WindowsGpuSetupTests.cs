using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class WindowsGpuSetupTests
{
    private static readonly Uri Tunnel = new("http://127.0.0.1:43117/api/chat");

    [Fact]
    public void Windows_operations_are_fixed_encoded_powershell_commands_that_fit_cmd_exe()
    {
        foreach (SshOperation operation in new[]
        {
            SshOperation.ReadWindowsInfo, SshOperation.ReadNvidiaGpuWindows, SshOperation.ReadSystemDiskWindows,
            SshOperation.FindOllamaWindows, SshOperation.ReadOllamaServiceWindows, SshOperation.ListOllamaModelsWindows,
            SshOperation.FinalizeWindowsProvisionScript, SshOperation.StartWindowsProvision,
            SshOperation.ReadWindowsProvisionStatus
        })
        {
            string command = SshCommands.Resolve(operation);
            Assert.StartsWith("powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", command);
            Assert.True(command.Length < 8000, $"{operation} is {command.Length} characters; cmd.exe stops at 8191.");
        }
        Assert.Equal("cmd /c ver", SshCommands.Resolve(SshOperation.ReadWindowsVersion));
    }

    [Fact]
    public void Provision_chunks_rebuild_the_script_and_reject_anything_but_base64()
    {
        IReadOnlyList<string> chunks = WindowsRemoteScripts.SplitProvisionScript();
        byte[] joined = Convert.FromBase64String(string.Concat(chunks));
        Assert.Equal(WindowsRemoteScripts.ProvisionScript, Encoding.UTF8.GetString(joined));
        for (int index = 0; index < chunks.Count; index++)
        {
            string command = SshCommands.Resolve(SshOperation.WriteWindowsProvisionChunk, $"{index}:{chunks[index]}");
            Assert.True(command.Length < 8000, $"chunk {index} is {command.Length} characters");
        }
        Assert.Throws<ArgumentException>(() =>
            SshCommands.Resolve(SshOperation.WriteWindowsProvisionChunk, "0:abc'; calc; '"));
        Assert.Throws<ArgumentException>(() => SshCommands.Resolve(SshOperation.WriteWindowsProvisionChunk, null));
    }

    [Fact]
    public void Every_windows_script_is_valid_powershell()
    {
        string bootstrap = Encoding.Unicode.GetString(Convert.FromBase64String(
            WindowsRemoteScripts.BuildOpenSshBootstrapCommand(2222).Split(' ')[^1]));
        Assert.Contains("2222", bootstrap);
        var scripts = new Dictionary<string, string>
        {
            ["provision"] = WindowsRemoteScripts.ProvisionScript, ["bootstrap"] = bootstrap,
            ["info"] = WindowsRemoteScripts.ReadInfo, ["gpu"] = WindowsRemoteScripts.ReadGpu,
            ["disk"] = WindowsRemoteScripts.ReadFreeDisk, ["find"] = WindowsRemoteScripts.FindOllama,
            ["service"] = WindowsRemoteScripts.ReadOllamaService, ["models"] = WindowsRemoteScripts.ListModels,
            ["start"] = WindowsRemoteScripts.StartProvision, ["status"] = WindowsRemoteScripts.ReadProvisionStatus,
            ["finalize"] = WindowsRemoteScripts.FinalizeProvisionScript,
            ["chunk0"] = WindowsRemoteScripts.WriteProvisionChunk(0, "QUJD"),
            ["chunk1"] = WindowsRemoteScripts.WriteProvisionChunk(1, "QUJD")
        };
        Assert.All(scripts.Values, script => Assert.True(script.All(c => c < 128), "Remote scripts must stay ASCII."));

        string directory = Path.Combine(Path.GetTempPath(), "qtaie-ps-" + Guid.NewGuid().ToString("N"));
        string checkerPath = directory + "-check.ps1";
        Directory.CreateDirectory(directory);
        try
        {
            foreach ((string name, string script) in scripts)
                File.WriteAllText(Path.Combine(directory, name + ".ps1"), script, new UTF8Encoding(true));
            const string checker = "$bad=0;Get-ChildItem -LiteralPath $args[0] -Filter *.ps1|ForEach-Object{" +
                "$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile($_.FullName,[ref]$t,[ref]$e);" +
                "if($e.Count){$bad++;\"$($_.Name): $($e[0].Message)\"}};if($bad){exit 1}";
            File.WriteAllText(checkerPath, "param($dir)\r\n" + checker.Replace("$args[0]", "$dir"));
            var start = new ProcessStartInfo("powershell.exe",
                ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", checkerPath, directory])
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, output);
        }
        finally { Directory.Delete(directory, true); File.Delete(checkerPath); }
    }

    [Fact]
    public void Bootstrap_command_rejects_invalid_ports()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsRemoteScripts.BuildOpenSshBootstrapCommand(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsRemoteScripts.BuildOpenSshBootstrapCommand(70000));
        Assert.True(WindowsRemoteScripts.BuildOpenSshBootstrapCommand().Length < 8000);
    }

    [Fact]
    public async Task Inspection_recognizes_a_windows_host_and_reads_gpu_disk_and_models()
    {
        var session = new ScriptedSession(WindowsHost(ollama: true, models: ["translategemma:12b"]));
        RemoteGpuInspection result = await new RemoteOllamaSetupService().InspectAsync(session);

        Assert.Equal(RemoteOsFamily.Windows, result.OsFamily);
        Assert.Equal("Microsoft Windows Server 2022 Standard 10.0.20348", result.OperatingSystem);
        Assert.Equal("NVIDIA GeForce RTX 4090", result.GpuName);
        Assert.Equal(24564L * 1024 * 1024, result.VramBytes);
        Assert.Equal(300_000_000_000L, result.FreeDiskBytes);
        Assert.True(result.OllamaInstalled);
        Assert.True(result.OllamaServiceActive);
        Assert.Equal(["translategemma:12b"], result.Models);
        Assert.True(result.SupportsAutomaticSetup);
    }

    [Fact]
    public async Task Windows_without_an_nvidia_driver_is_blocked_with_a_clear_reason()
    {
        var host = WindowsHost(ollama: false, models: []);
        host.Results[SshOperation.ReadNvidiaGpuWindows] = new(0, "NONVIDIA:Microsoft Basic Display Adapter", "");
        RemoteGpuInspection result = await new RemoteOllamaSetupService().InspectAsync(new ScriptedSession(host));

        Assert.False(result.SupportsAutomaticSetup);
        Assert.Null(result.GpuName);
        Assert.Contains("driver NVIDIA", result.BlockedReason);
    }

    [Fact]
    public async Task Unknown_operating_system_is_reported_instead_of_guessing()
    {
        var host = new Dictionary<SshOperation, SshCommandResult>
        {
            [SshOperation.ReadOperatingSystem] = new(1, "", "not found"),
            [SshOperation.ReadWindowsVersion] = new(1, "", "not found")
        };
        RemoteGpuInspection result = await new RemoteOllamaSetupService().InspectAsync(new ScriptedSession(new() { Results = host }));
        Assert.Equal(RemoteOsFamily.Unknown, result.OsFamily);
        Assert.False(result.SupportsAutomaticSetup);
    }

    [Fact]
    public async Task Auto_setup_provisions_windows_pulls_the_model_and_verifies_it()
    {
        var host = WindowsHost(ollama: false, models: []);
        var session = new ScriptedSession(host);
        host.OnExecute = (operation, argument) =>
        {
            if (operation == SshOperation.ReadWindowsProvisionStatus)
            {
                host.StatusCalls++;
                return host.StatusCalls == 1
                    ? new(0, "{\"stage\":\"download\",\"percent\":30,\"message\":\"download\",\"done\":false,\"ok\":false,\"error\":\"\",\"warning\":\"\"}", "")
                    : new(0, "{\"stage\":\"ready\",\"percent\":100,\"message\":\"ready\",\"done\":true,\"ok\":true,\"error\":\"\",\"warning\":\"\"}", "");
            }
            if (operation == SshOperation.StartWindowsProvision)
            {
                host.Results[SshOperation.FindOllamaWindows] = new(0, @"C:\ProgramData\QTStudio\ollama\ollama.exe", "");
                host.Results[SshOperation.ReadOllamaServiceWindows] = new(0, "active", "");
            }
            return null;
        };
        var pullHandler = new StubHandler(_ =>
        {
            host.Results[SshOperation.ListOllamaModelsWindows] = new(0, "NAME\ntranslategemma:12b", "");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"status\":\"pulling\",\"completed\":50,\"total\":100}\n{\"status\":\"success\"}\n")
            };
        });
        var steps = new List<AutoSetupProgress>();
        var setup = new RemoteOllamaAutoSetup(pull: new RemoteOllamaModelPullService(() => new HttpClient(pullHandler)),
            delay: (_, _) => Task.CompletedTask);

        AutoSetupResult result = await setup.RunAsync(session, Tunnel, "translategemma:12b",
            progress: new SyncProgress(steps.Add));

        Assert.True(result.Success, result.Message);
        Assert.Contains(steps, s => s.Stage == AutoSetupStage.PullModel && s.Percent == 50);
        Assert.Equal(WindowsRemoteScripts.SplitProvisionScript().Count,
            session.Executed.Count(e => e.Operation == SshOperation.WriteWindowsProvisionChunk));
        int finalize = session.Executed.FindIndex(e => e.Operation == SshOperation.FinalizeWindowsProvisionScript);
        int start = session.Executed.FindIndex(e => e.Operation == SshOperation.StartWindowsProvision);
        Assert.True(finalize > 0 && start > finalize);
        Assert.DoesNotContain(session.Executed, e => e.Operation is SshOperation.InstallOllamaUbuntu);
    }

    [Fact]
    public async Task Auto_setup_reports_a_non_admin_account_in_plain_words()
    {
        var host = WindowsHost(ollama: false, models: []);
        host.OnExecute = (operation, _) => operation == SshOperation.ReadWindowsProvisionStatus
            ? new(0, "{\"stage\":\"error\",\"percent\":0,\"message\":\"error\",\"done\":true,\"ok\":false,\"error\":\"NOT_ADMIN\",\"warning\":\"\"}", "")
            : null;
        var setup = new RemoteOllamaAutoSetup(delay: (_, _) => Task.CompletedTask);

        AutoSetupResult result = await setup.RunAsync(new ScriptedSession(host), Tunnel, "translategemma:12b");

        Assert.False(result.Success);
        Assert.Contains("Administrator", result.Message);
    }

    [Fact]
    public async Task Auto_setup_skips_installation_when_ollama_and_the_model_are_already_there()
    {
        var host = WindowsHost(ollama: true, models: ["translategemma:12b"]);
        var session = new ScriptedSession(host);
        var setup = new RemoteOllamaAutoSetup(delay: (_, _) => Task.CompletedTask);

        AutoSetupResult result = await setup.RunAsync(session, Tunnel, "translategemma:12b");

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(session.Executed, e => e.Operation == SshOperation.WriteWindowsProvisionChunk);
        Assert.DoesNotContain(session.Executed, e => e.Operation == SshOperation.StartWindowsProvision);
    }

    [Fact]
    public async Task Auto_setup_stops_before_downloading_when_the_disk_is_too_small()
    {
        var host = WindowsHost(ollama: true, models: []);
        host.Results[SshOperation.ReadSystemDiskWindows] = new(0, "2000000000", "");
        AutoSetupResult result = await new RemoteOllamaAutoSetup().RunAsync(new ScriptedSession(host), Tunnel, "translategemma:12b");
        Assert.False(result.Success);
        Assert.Contains("Ổ đĩa", result.Message);
    }

    [Theory]
    [InlineData("SSH-2.0-OpenSSH_for_Windows_9.5\r\n", RemoteEndpointKind.Ssh, true)]
    [InlineData("SSH-2.0-OpenSSH_9.6p1 Ubuntu-3ubuntu13\r\n", RemoteEndpointKind.Ssh, false)]
    public async Task Probe_reads_the_ssh_banner_and_tells_windows_from_linux(string banner, RemoteEndpointKind kind, bool windows)
    {
        using var server = FakeServer.Start(onConnect: stream => stream.Write(Encoding.ASCII.GetBytes(banner)));
        RemoteHostProbeResult result = await FastProbe().ProbeAsync("127.0.0.1", server.Port);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(windows, result.LooksLikeWindows);
    }

    [Fact]
    public async Task Probe_recognizes_remote_desktop_and_reports_that_ssh_is_missing()
    {
        // An RDP server stays silent until it receives the X.224 connection request, then answers with a TPKT header.
        using var server = FakeServer.Start(onRequest: (stream, _) =>
            stream.Write([0x03, 0x00, 0x00, 0x13, 0x0E, 0xD0, 0x00, 0x00, 0x12, 0x34, 0x00, 0x02, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00, 0x00]));
        RemoteHostProbeResult result = await FastProbe().ProbeAsync("127.0.0.1", server.Port);
        Assert.Equal(RemoteEndpointKind.Rdp, result.Kind);
        Assert.True(result.LooksLikeWindows);
        Assert.Contains("RDP", result.Message);
        Assert.Contains("OpenSSH", result.Message);
    }

    [Fact]
    public async Task Probe_recognizes_a_bare_ollama_port_and_a_closed_port()
    {
        using var ollama = FakeServer.Start(onRequest: (stream, request) =>
        {
            if (!request.StartsWith("GET ", StringComparison.Ordinal)) return;
            stream.Write(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 17\r\n\r\nOllama is running"));
        });
        Assert.Equal(RemoteEndpointKind.Ollama, (await FastProbe().ProbeAsync("127.0.0.1", ollama.Port)).Kind);

        int closed = FakeServer.FreePort();
        Assert.Equal(RemoteEndpointKind.Unreachable, (await FastProbe().ProbeAsync("127.0.0.1", closed)).Kind);

        using var silent = FakeServer.Start();
        Assert.Equal(RemoteEndpointKind.Unknown, (await FastProbe().ProbeAsync("127.0.0.1", silent.Port)).Kind);
    }

    [Fact]
    public void Rdp_file_gives_host_port_and_user_and_never_reads_the_saved_password()
    {
        const string content = "\uFEFFscreen mode id:i:2\r\nfull address:s:203.0.113.7:33890\r\nusername:s:WIN-GPU\\Administrator\r\n" +
                               "password 51:b:01000000D08C9DDF0115D1118C7A\r\ngatewayhostname:s:gw.example.test\r\n";
        RdpConnectionFile file = RdpConnectionFile.Parse(content);

        Assert.Equal("203.0.113.7", file.Host);
        Assert.Equal(33890, file.Port);
        Assert.Equal(@"WIN-GPU\Administrator", file.Username);
        Assert.Equal("Administrator", file.SshUsername);
        Assert.Equal("gw.example.test", file.GatewayHost);
        Assert.DoesNotContain("01000000", file.ToString());
    }

    [Fact]
    public void Rdp_file_handles_default_port_ipv6_server_port_and_bad_input()
    {
        Assert.Equal(3389, RdpConnectionFile.Parse("full address:s:gpu.example.test").Port);
        RdpConnectionFile v6 = RdpConnectionFile.Parse("full address:s:[2001:db8::1]:4000");
        Assert.Equal("2001:db8::1", v6.Host);
        Assert.Equal(4000, v6.Port);
        Assert.Equal(5555, RdpConnectionFile.Parse("full address:s:gpu.example.test\nserver port:i:5555").Port);
        Assert.Throws<InvalidDataException>(() => RdpConnectionFile.Parse("username:s:x"));
        Assert.Throws<InvalidDataException>(() => RdpConnectionFile.Parse("full address:s:bad host/x"));
    }

    private static RemoteHostProbe FastProbe() => new(TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(400));

    private static ScriptedHost WindowsHost(bool ollama, string[] models)
    {
        var host = new ScriptedHost();
        host.Results[SshOperation.ReadOperatingSystem] = new(1, "", "'cat' is not recognized");
        host.Results[SshOperation.ReadWindowsVersion] = new(0, "\r\nMicrosoft Windows [Version 10.0.20348.2113]", "");
        host.Results[SshOperation.ReadWindowsInfo] = new(0, "Microsoft Windows Server 2022 Standard 10.0.20348\r\n", "");
        host.Results[SshOperation.ReadNvidiaGpuWindows] = new(0, "NVIDIA GeForce RTX 4090, 24564\r\n", "");
        host.Results[SshOperation.ReadSystemDiskWindows] = new(0, "300000000000\r\n", "");
        host.Results[SshOperation.FindOllamaWindows] = ollama
            ? new(0, @"C:\ProgramData\QTStudio\ollama\ollama.exe", "") : new(1, "", "");
        host.Results[SshOperation.ReadOllamaServiceWindows] = ollama ? new(0, "active", "") : new(3, "inactive", "");
        host.Results[SshOperation.ListOllamaModelsWindows] = new(0, "NAME\r\n" + string.Join("\r\n", models), "");
        return host;
    }

    private sealed class ScriptedHost
    {
        public Dictionary<SshOperation, SshCommandResult> Results { get; init; } = [];
        public Func<SshOperation, string?, SshCommandResult?>? OnExecute { get; set; }
        public int StatusCalls { get; set; }
    }

    private sealed class ScriptedSession(ScriptedHost host) : ISshSession
    {
        public List<(SshOperation Operation, string? Argument)> Executed { get; } = [];
        public bool IsConnected => true;
        public Task<SshCommandResult> ExecuteAsync(SshOperation operation, string? argument = null,
            CancellationToken cancellationToken = default)
        {
            _ = SshCommands.Resolve(operation, argument); // Every command must be buildable, as on the real session.
            Executed.Add((operation, argument));
            SshCommandResult? scripted = host.OnExecute?.Invoke(operation, argument);
            return Task.FromResult(scripted ?? (host.Results.TryGetValue(operation, out var result)
                ? result : new SshCommandResult(0, "", "")));
        }
        public Task<ISshTunnel> OpenOllamaTunnelAsync(int remotePort, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class SyncProgress(Action<AutoSetupProgress> report) : IProgress<AutoSetupProgress>
    {
        public void Report(AutoSetupProgress value) => report(value);
    }

    /// <summary>A loopback TCP server that behaves like the protocol under test.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        public int Port { get; }

        private FakeServer(TcpListener listener, Action<NetworkStream>? onConnect, Action<NetworkStream, string>? onRequest)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await listener.AcceptTcpClientAsync(_stop.Token); }
                    catch { return; }
                    _ = Task.Run(() =>
                    {
                        using (client)
                        {
                            try
                            {
                                NetworkStream stream = client.GetStream();
                                onConnect?.Invoke(stream);
                                if (onRequest is null) { Thread.Sleep(800); return; }
                                var buffer = new byte[512];
                                client.ReceiveTimeout = 800;
                                int count = stream.Read(buffer, 0, buffer.Length);
                                onRequest(stream, Encoding.ASCII.GetString(buffer, 0, count));
                                Thread.Sleep(100);
                            }
                            catch (IOException) { }
                        }
                    });
                }
            });
        }

        public static FakeServer Start(Action<NetworkStream>? onConnect = null, Action<NetworkStream, string>? onRequest = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new(listener, onConnect, onRequest);
        }

        public static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public void Dispose() { _stop.Cancel(); _listener.Stop(); }
    }
}
