using System.Text;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class RemoteLlamaSetupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-llama-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly Uri Tunnel = new("http://127.0.0.1:43117/api/chat");

    [Fact]
    public async Task Setup_uploads_two_archives_once_configures_task_and_stops_old_ollama()
    {
        FakePackageProvider packages = MakePackage();
        var session = new FakeSession(executableExists: false);
        var health = new FakeHealth([new(false, []), new(true, ["Index-Translate-9B-Q4_K_M"])]);
        var setup = new RemoteLlamaSetup(packages, health, delay: (_, _) => Task.CompletedTask);

        RemoteLlamaSetupResult result = await setup.RunAsync(session, Tunnel,
            "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", 11436, 4, 8192);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, session.Uploads.Count);
        Assert.All(session.Uploads, upload => Assert.StartsWith(@"C:\ProgramData\QTStudio\dl\", upload.Remote));
        Assert.Equal(1, packages.Calls);
        string json = Assert.Single(session.Commands, c => c.Operation == SshOperation.ConfigureLlamaServerTask).Argument!;
        var spec = System.Text.Json.JsonSerializer.Deserialize<RemoteLlamaTaskSpecification>(json)!;
        Assert.Equal(4, spec.Slots); Assert.Equal(8192, spec.ContextPerSlot); Assert.Equal(11436, spec.Port);
        string script = RemoteLlamaWindowsScripts.ConfigureTask(json);
        Assert.Contains("QTStudio llama-server", script);
        Assert.Contains("QTStudio Ollama", script);
        string wrapper = DecodeWrapper(script);
        foreach (string argument in new LlamaServerLaunch(GgufModelReference.Parse(spec.Model), 11436, 4, 8192).Arguments())
            Assert.Contains("'" + argument + "'", wrapper);
    }

    [Fact]
    public async Task Existing_executable_skips_package_and_upload_but_rewrites_task()
    {
        FakePackageProvider packages = MakePackage(); var session = new FakeSession(executableExists: true);
        var setup = new RemoteLlamaSetup(packages, new FakeHealth([new(true, ["ready"])]));
        RemoteLlamaSetupResult result = await setup.RunAsync(session, Tunnel,
            "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", 11436, 2, 4096);
        Assert.True(result.Success); Assert.Empty(session.Uploads); Assert.Equal(0, packages.Calls);
        Assert.Contains(session.Commands, c => c.Operation == SshOperation.ConfigureLlamaServerTask);
    }

    [Fact]
    public async Task Health_timeout_includes_safe_log_tail()
    {
        var session = new FakeSession(executableExists: true) { LogTail = "load failed: synthetic tail" };
        var setup = new RemoteLlamaSetup(MakePackage(), new FakeHealth([]),
            healthTimeout: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);
        RemoteLlamaSetupResult result = await setup.RunAsync(session, Tunnel,
            "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", 11436, 1, 4096);
        Assert.False(result.Success); Assert.Contains("synthetic tail", result.Message);
    }

    [Fact]
    public async Task Waiting_for_model_honors_cancellation()
    {
        var session = new FakeSession(executableExists: true);
        var setup = new RemoteLlamaSetup(MakePackage(), new FakeHealth([new(false, [])]),
            delay: (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => setup.RunAsync(session, Tunnel,
            "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", 11436, 1, 4096, cancellationToken: cancellation.Token));
    }

    [Fact]
    public void Generated_powershell_quotes_spaces_and_rejects_model_injection()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new RemoteLlamaTaskSpecification(
            @"C:\Models With Space\translate model.gguf", 11436, 3, 4096));
        string script = RemoteLlamaWindowsScripts.ConfigureTask(json);
        Assert.Contains("'C:\\Models With Space\\translate model.gguf'", DecodeWrapper(script));
        string attack = System.Text.Json.JsonSerializer.Serialize(new RemoteLlamaTaskSpecification(
            "owner/repo:Q4';Start-Process calc", 11436, 1, 4096));
        Assert.Throws<ArgumentException>(() => RemoteLlamaWindowsScripts.ConfigureTask(attack));
        Assert.All(new[] { RemoteLlamaWindowsScripts.ExtractPackage, RemoteLlamaWindowsScripts.ReadCacheSize,
            RemoteLlamaWindowsScripts.ReadLogTail, script }, value => Assert.True(value.All(c => c < 128)));
    }

    [Fact]
    public async Task Probe_rejects_ubuntu_and_keeps_windows_gpu_details()
    {
        var windows = new FakeSession(true);
        RemoteLlamaInspection result = await new RemoteWindowsGpuProbe().InspectAsync(windows, 11436);
        Assert.True(result.SupportsAutomaticSetup); Assert.Equal("NVIDIA RTX 4090", result.GpuName);
        Assert.Equal(24564L * 1024 * 1024, result.VramBytes);
        var linux = new FakeSession(true) { IsWindows = false };
        RemoteLlamaInspection rejected = await new RemoteWindowsGpuProbe().InspectAsync(linux, 11436);
        Assert.False(rejected.SupportsAutomaticSetup); Assert.Contains("Windows", rejected.BlockedReason);
    }

    private FakePackageProvider MakePackage()
    {
        Directory.CreateDirectory(_root); string a = Path.Combine(_root, LlamaCppRelease.ServerZip);
        string b = Path.Combine(_root, LlamaCppRelease.CudaRuntimeZip); File.WriteAllBytes(a, new byte[32]); File.WriteAllBytes(b, new byte[16]);
        return new(new(LlamaCppRelease.Build, [a, b], Path.Combine(_root, "llama-server.exe")));
    }

    private static string DecodeWrapper(string configureScript)
    {
        const string marker = "FromBase64String('"; int start = configureScript.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        return Encoding.UTF8.GetString(Convert.FromBase64String(configureScript[start..configureScript.IndexOf('\'', start)]));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private sealed class FakePackageProvider(LlamaCppPackage package) : ILlamaCppPackageProvider
    {
        public int Calls { get; private set; }
        public Task<LlamaCppPackage> EnsureAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        { Calls++; progress?.Report("cached"); return Task.FromResult(package); }
    }

    private sealed class FakeHealth(IEnumerable<LlamaServerHealth> values) : IRemoteLlamaHealthClient
    {
        private readonly Queue<LlamaServerHealth> _values = new(values);
        public Task<LlamaServerHealth> CheckAsync(Uri tunnelEndpoint, CancellationToken cancellationToken = default) =>
            Task.FromResult(_values.Count == 0 ? new LlamaServerHealth(false, []) : _values.Dequeue());
    }

    private sealed class FakeSession(bool executableExists) : ISshSession
    {
        private bool _installed = executableExists;
        public bool IsWindows { get; set; } = true;
        public string LogTail { get; set; } = "";
        public bool IsConnected => true;
        public List<(SshOperation Operation, string? Argument)> Commands { get; } = [];
        public List<(string Local, string Remote)> Uploads { get; } = [];
        public Task<SshCommandResult> ExecuteAsync(SshOperation operation, string? argument = null, CancellationToken cancellationToken = default)
        {
            Commands.Add((operation, argument));
            SshCommandResult result = operation switch
            {
                SshOperation.ReadOperatingSystem when IsWindows => new(1, "", "'cat' is not recognized as an internal or external command"),
                SshOperation.ReadOperatingSystem => new(0, "ID=ubuntu", ""),
                SshOperation.ReadWindowsVersion => new(0, IsWindows ? "Microsoft Windows" : "", ""),
                SshOperation.ReadWindowsInfo => new(0, "Microsoft Windows Server 2022 10.0.20348", ""),
                SshOperation.ReadNvidiaGpuWindows => new(0, "NVIDIA RTX 4090, 24564", ""),
                SshOperation.ReadSystemDiskWindows => new(0, "300000000000", ""),
                SshOperation.TestLlamaServerExecutable => _installed ? new(0, "installed", "") : new(1, "", "missing"),
                SshOperation.ExtractLlamaCpp => Install(),
                SshOperation.ReadLlamaCacheSize => new(0, "1073741824", ""),
                SshOperation.ReadLlamaServerLogTail => new(0, LogTail, ""),
                _ => new(0, "ok", "")
            };
            return Task.FromResult(result);
        }
        private SshCommandResult Install() { _installed = true; return new(0, "ok", ""); }
        public Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        { Uploads.Add((localPath, remotePath)); progress?.Report(new FileInfo(localPath).Length); return Task.CompletedTask; }
        public Task<ISshTunnel> OpenServerTunnelAsync(int remotePort, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
