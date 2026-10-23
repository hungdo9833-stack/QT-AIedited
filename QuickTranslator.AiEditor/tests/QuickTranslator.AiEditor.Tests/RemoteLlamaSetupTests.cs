using System.Text;
using System.Text.Json;
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
            Assert.Contains("\"" + argument + "\"", wrapper);
        Assert.Contains(" 2>&1", wrapper); Assert.DoesNotContain("ErrorActionPreference", wrapper);
        Assert.Contains("llama-server.cmd", script);
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
    public async Task Probe_timeouts_while_the_server_starts_are_not_a_user_cancel()
    {
        var session = new FakeSession(executableExists: true);
        var setup = new RemoteLlamaSetup(MakePackage(), new TimingOutThenHealthy(2), delay: (_, _) => Task.CompletedTask);

        RemoteLlamaSetupResult result = await setup.RunAsync(session, Tunnel,
            "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", 11436, 4, 8192);

        Assert.True(result.Success, result.Message);
    }

    private sealed class TimingOutThenHealthy(int timeouts) : IRemoteLlamaHealthClient
    {
        private int _calls;
        public Task<LlamaServerHealth> CheckAsync(Uri tunnelEndpoint, CancellationToken cancellationToken = default) =>
            ++_calls <= timeouts ? throw new TaskCanceledException("probe timeout", new TimeoutException())
                : Task.FromResult(new LlamaServerHealth(true, ["ready"]));
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
        Assert.Contains("\"C:\\Models With Space\\translate model.gguf\"", DecodeWrapper(script));
        string attack = System.Text.Json.JsonSerializer.Serialize(new RemoteLlamaTaskSpecification(
            "owner/repo:Q4';Start-Process calc", 11436, 1, 4096));
        Assert.Throws<ArgumentException>(() => RemoteLlamaWindowsScripts.ConfigureTask(attack));
        Assert.All(new[] { RemoteLlamaWindowsScripts.ExtractPackage, RemoteLlamaWindowsScripts.ReadCacheSize,
            RemoteLlamaWindowsScripts.ReadLogTail, script }, value => Assert.True(value.All(c => c < 128)));
    }

    [Fact]
    public async Task Probe_keeps_windows_gpu_details()
    {
        var windows = new FakeSession(true);
        RemoteLlamaInspection result = await new RemoteGpuProbe().InspectAsync(windows, 11436);
        Assert.True(result.SupportsAutomaticSetup); Assert.Equal("NVIDIA RTX 4090", result.GpuName);
        Assert.Equal(24564L * 1024 * 1024, result.VramBytes); Assert.Equal(RemoteHostPlatform.Windows, result.Platform);
    }

    [Fact]
    public async Task Probe_reads_ubuntu_gpu_architecture_and_disk_without_windows_commands()
    {
        var session = new FakeSession(true) { IsWindows = false };
        RemoteLlamaInspection result = await new RemoteGpuProbe().InspectAsync(session, 11434);
        Assert.True(result.SupportsAutomaticSetup, result.BlockedReason);
        Assert.Equal(RemoteHostPlatform.Linux, result.Platform);
        Assert.Equal("NVIDIA GeForce RTX 3090", result.GpuName);
        Assert.Equal(24576L * 1024 * 1024, result.VramBytes); Assert.Equal(300000000000L, result.FreeDiskBytes);
        Assert.Contains("Ubuntu 22.04.4 LTS", result.OperatingSystem); Assert.Contains("x86_64", result.OperatingSystem);
        Assert.Equal(new[] { SshOperation.ReadOperatingSystem, SshOperation.ReadLinuxArchitecture,
            SshOperation.ReadNvidiaGpuLinux, SshOperation.ReadSystemDiskLinux }, session.Commands.Select(c => c.Operation));
        Assert.DoesNotContain(session.Commands, c => c.Operation is SshOperation.ReadWindowsVersion or SshOperation.ConfirmWindowsMux);
    }

    [Theory]
    [InlineData(0, "NONVIDIA:")]
    [InlineData(1, "NVIDIA GeForce RTX 3090, 24576")]
    public async Task Probe_blocks_linux_without_working_nvidia_smi(int exitCode, string output)
    {
        var session = new FakeSession(true) { IsWindows = false, LinuxGpuExitCode = exitCode, LinuxGpuOutput = output };
        RemoteLlamaInspection result = await new RemoteGpuProbe().InspectAsync(session, 11434);
        Assert.Equal(RemoteHostPlatform.Linux, result.Platform); Assert.False(result.SupportsAutomaticSetup);
        Assert.Contains("NVIDIA", result.BlockedReason!); Assert.Null(result.GpuName); Assert.Null(result.VramBytes);
    }

    [Fact]
    public async Task Probe_blocks_linux_aarch64()
    {
        var session = new FakeSession(true) { IsWindows = false, LinuxArchitecture = "aarch64" };
        RemoteLlamaInspection result = await new RemoteGpuProbe().InspectAsync(session, 11434);
        Assert.Equal(RemoteHostPlatform.Linux, result.Platform); Assert.False(result.SupportsAutomaticSetup);
        Assert.Contains("x86_64", result.BlockedReason!);
    }

    [Fact]
    public async Task Probe_blocks_unknown_os_when_os_release_fails_without_windows_evidence()
    {
        var session = new FakeSession(true) { IsWindows = false, OsReleaseExitCode = 1, OsRelease = "" };
        RemoteLlamaInspection result = await new RemoteGpuProbe().InspectAsync(session, 11434);
        Assert.Equal(RemoteHostPlatform.Unsupported, result.Platform); Assert.False(result.SupportsAutomaticSetup);
        Assert.False(string.IsNullOrWhiteSpace(result.BlockedReason));
        Assert.Equal(new[] { SshOperation.ReadOperatingSystem, SshOperation.ReadWindowsVersion }, session.Commands.Select(c => c.Operation));
    }

    [Theory]
    [InlineData("NAME=Ubuntu\nID=ubuntu\nPRETTY_NAME=\"Ubuntu 22.04.4 LTS\"", "Ubuntu 22.04.4 LTS")]
    [InlineData("PRETTY_NAME=\"Ubuntu 22.04.4 LTS\"\nNAME=Ubuntu\nID=ubuntu", "Ubuntu 22.04.4 LTS")]
    [InlineData("ID=ubuntu\nNAME=\"Ubuntu Linux\"", "Ubuntu Linux")]
    [InlineData("NAME='Ubuntu Linux'", "Ubuntu Linux")]
    [InlineData("PRETTY_NAME=\"\"\nNAME=Ubuntu", "Ubuntu")]
    [InlineData("ID=ubuntu", "ubuntu")]
    [InlineData("'cat' is not recognized as an internal or external command", null)]
    [InlineData("", null)]
    public void Linux_release_prefers_pretty_name_and_strips_quotes(string text, string? expected) =>
        Assert.Equal(expected, RemoteGpuProbe.LinuxRelease(text));

    [Fact]
    public async Task Linux_setup_installs_once_retests_and_configures_without_packages_or_uploads()
    {
        const string model = "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M";
        FakePackageProvider packages = MakePackage(); var session = new FakeSession(false) { IsWindows = false };
        var setup = new RemoteLlamaSetup(packages, new FakeHealth([new(false, []), new(true, ["ready"])]),
            delay: (_, _) => Task.CompletedTask);
        RemoteLlamaSetupResult result = await setup.RunAsync(session, Tunnel, model, 11434, 4, 8192);
        Assert.True(result.Success, result.Message); Assert.Empty(session.Uploads); Assert.Equal(0, packages.Calls);
        Assert.Single(session.Commands, c => c.Operation == SshOperation.InstallLlamaCppLinux);
        Assert.Equal(new[] { SshOperation.TestLlamaServerLinux, SshOperation.InstallLlamaCppLinux,
            SshOperation.TestLlamaServerLinux, SshOperation.ConfigureLlamaServerLinux },
            session.Commands.Skip(4).Take(4).Select(c => c.Operation));
        string json = Assert.Single(session.Commands, c => c.Operation == SshOperation.ConfigureLlamaServerLinux).Argument!;
        var spec = JsonSerializer.Deserialize<RemoteLlamaTaskSpecification>(json)!;
        Assert.Equal(model, spec.Model); Assert.Equal(11434, spec.Port);
        Assert.Equal(4, spec.Slots); Assert.Equal(8192, spec.ContextPerSlot);
        Assert.Contains(session.Commands, c => c.Operation == SshOperation.ReadLlamaCacheSizeLinux);
        Assert.DoesNotContain(session.Commands, c => c.Operation is SshOperation.ConfigureLlamaServerTask
            or SshOperation.ExtractLlamaCpp or SshOperation.TestLlamaServerExecutable
            or SshOperation.ReadWindowsVersion or SshOperation.ConfirmWindowsMux or SshOperation.ReadLlamaCacheSize);
        Assert.True(result.Inspection!.LlamaServerRunning); Assert.Equal(new[] { "ready" }, result.Inspection.Models);
    }

    [Fact]
    public async Task Linux_setup_with_existing_executable_skips_install()
    {
        FakePackageProvider packages = MakePackage(); var session = new FakeSession(true) { IsWindows = false };
        var setup = new RemoteLlamaSetup(packages, new FakeHealth([new(true, ["ready"])]));
        RemoteLlamaSetupResult result = await setup.RunAsync(session, Tunnel,
            "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", 11434, 2, 4096);
        Assert.True(result.Success, result.Message); Assert.Empty(session.Uploads); Assert.Equal(0, packages.Calls);
        Assert.Single(session.Commands, c => c.Operation == SshOperation.TestLlamaServerLinux);
        Assert.Single(session.Commands, c => c.Operation == SshOperation.ConfigureLlamaServerLinux);
        Assert.DoesNotContain(session.Commands, c => c.Operation == SshOperation.InstallLlamaCppLinux);
    }

    [Fact]
    public async Task Linux_setup_surfaces_install_checksum_failure_and_stops_before_configuration()
    {
        FakePackageProvider packages = MakePackage();
        var session = new FakeSession(false) { IsWindows = false, LinuxInstallFails = true };
        var setup = new RemoteLlamaSetup(packages, new FakeHealth([]));
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.RunAsync(session, Tunnel,
            "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", 11434, 4, 8192));
        Assert.Contains(session.LinuxInstallError, error.Message);
        Assert.Single(session.Commands, c => c.Operation == SshOperation.InstallLlamaCppLinux);
        Assert.Single(session.Commands, c => c.Operation == SshOperation.TestLlamaServerLinux);
        Assert.DoesNotContain(session.Commands, c => c.Operation == SshOperation.ConfigureLlamaServerLinux);
        Assert.Empty(session.Uploads); Assert.Equal(0, packages.Calls);
    }

    [Fact]
    public async Task Linux_health_timeout_includes_linux_log_tail()
    {
        var session = new FakeSession(true) { IsWindows = false, LogTail = "load failed: synthetic Linux tail" };
        var setup = new RemoteLlamaSetup(MakePackage(), new FakeHealth([]),
            healthTimeout: TimeSpan.Zero, delay: (_, _) => Task.CompletedTask);
        RemoteLlamaSetupResult result = await setup.RunAsync(session, Tunnel,
            "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", 11434, 1, 4096);
        Assert.False(result.Success); Assert.Contains(session.LogTail, result.Message);
        Assert.Single(session.Commands, c => c.Operation == SshOperation.ReadLlamaServerLogTailLinux);
        Assert.DoesNotContain(session.Commands, c => c.Operation == SshOperation.ReadLlamaServerLogTail);
    }

    [Fact]
    public void Linux_configuration_quotes_launch_arguments_and_manages_detached_server()
    {
        var spec = new RemoteLlamaTaskSpecification("IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", 11434, 4, 8192);
        string script = RemoteLlamaLinuxScripts.ConfigureServer(JsonSerializer.Serialize(spec));
        string wrapper = DecodeLinuxWrapper(script);
        foreach (string argument in new LlamaServerLaunch(GgufModelReference.Parse(spec.Model), spec.Port, spec.Slots, spec.ContextPerSlot).Arguments())
            Assert.Contains("'" + argument + "'", wrapper);
        Assert.Contains("exec \"$D/llama-server\"", wrapper); Assert.Contains("LD_LIBRARY_PATH", wrapper);
        Assert.Contains("LLAMA_CACHE", wrapper); Assert.Contains("'--port' '11434'", wrapper); Assert.Contains("'-np' '4'", wrapper);
        foreach (string marker in new[] { "setsid", "nohup", "llama-server.pid", "kill -TERM", "LLAMA_SERVER_EXITED" })
            Assert.Contains(marker, script);
    }

    [Fact]
    public void Linux_configuration_rejects_model_injection_and_quotes_absolute_gguf_paths()
    {
        string attack = JsonSerializer.Serialize(new RemoteLlamaTaskSpecification("owner/repo:Q4';rm -rf ~", 11434, 4, 8192));
        Assert.Throws<ArgumentException>(() => RemoteLlamaLinuxScripts.ConfigureServer(attack));
        Assert.Equal("'a'\\''b'", RemoteLlamaLinuxScripts.ShellQuote("a'b"));
        const string path = "/data/models/x y.gguf";
        Assert.Equal(path, GgufModelReference.Parse(path).FilePath);
        string json = JsonSerializer.Serialize(new RemoteLlamaTaskSpecification(path, 11434, 4, 8192));
        Assert.Contains("'-m' '/data/models/x y.gguf'", DecodeLinuxWrapper(RemoteLlamaLinuxScripts.ConfigureServer(json)));
    }

    [Fact]
    public void Linux_install_uses_pinned_archives_checksums_and_ascii_scripts()
    {
        string install = RemoteLlamaLinuxScripts.InstallPackage;
        foreach (string marker in new[] { LlamaCppLinuxRelease.ServerArchive, LlamaCppLinuxRelease.CudaRuntimeArchive,
            LlamaCppLinuxRelease.ServerSha256, LlamaCppLinuxRelease.CudaRuntimeSha256, "sha256sum -c", "--strip-components=1",
            "https://github.com/ggml-org/llama.cpp/releases/download/" + LlamaCppRelease.Build + "/" })
            Assert.Contains(marker, install);
        string configure = RemoteLlamaLinuxScripts.ConfigureServer(JsonSerializer.Serialize(
            new RemoteLlamaTaskSpecification("owner/repo:Q4", 11434, 4, 8192)));
        foreach (string script in new[] { RemoteLlamaLinuxScripts.ReadArchitecture, RemoteLlamaLinuxScripts.ReadGpu,
            RemoteLlamaLinuxScripts.ReadFreeDisk, RemoteLlamaLinuxScripts.TestExecutable, RemoteLlamaLinuxScripts.ReadLogTail,
            RemoteLlamaLinuxScripts.ReadCacheSize, install, configure, DecodeLinuxWrapper(configure) })
        {
            Assert.True(script.All(c => c < 128));
            Assert.True(RemoteLlamaLinuxScripts.Encode(script).All(c => c < 128));
        }
    }

    [Theory]
    [InlineData(SshOperation.ReadOperatingSystem)]
    [InlineData(SshOperation.ReadLinuxArchitecture)]
    [InlineData(SshOperation.ReadNvidiaGpuLinux)]
    [InlineData(SshOperation.ReadSystemDiskLinux)]
    [InlineData(SshOperation.TestLlamaServerLinux)]
    [InlineData(SshOperation.InstallLlamaCppLinux)]
    [InlineData(SshOperation.ConfigureLlamaServerLinux)]
    [InlineData(SshOperation.ReadLlamaServerLogTailLinux)]
    [InlineData(SshOperation.ReadLlamaCacheSizeLinux)]
    [InlineData(SshOperation.DiagnoseNvidiaDriverLinux)]
    public void Linux_ssh_operations_resolve_to_plain_or_base64_shell_commands(SshOperation operation)
    {
        string json = JsonSerializer.Serialize(new RemoteLlamaTaskSpecification("owner/repo:Q4", 11434, 4, 8192));
        string command = SshCommands.Resolve(operation, json);
        if (operation == SshOperation.ReadOperatingSystem) Assert.Equal("cat /etc/os-release", command);
        else if (operation == SshOperation.ReadLinuxArchitecture) Assert.Equal("uname -m", command);
        else
        {
            Assert.StartsWith("printf '%s' '", command); Assert.EndsWith("| base64 -d | sh", command);
            string expected = operation switch
            {
                SshOperation.ReadNvidiaGpuLinux => RemoteLlamaLinuxScripts.ReadGpu,
                SshOperation.ReadSystemDiskLinux => RemoteLlamaLinuxScripts.ReadFreeDisk,
                SshOperation.TestLlamaServerLinux => RemoteLlamaLinuxScripts.TestExecutable,
                SshOperation.InstallLlamaCppLinux => RemoteLlamaLinuxScripts.InstallPackage,
                SshOperation.ConfigureLlamaServerLinux => RemoteLlamaLinuxScripts.ConfigureServer(json),
                SshOperation.ReadLlamaServerLogTailLinux => RemoteLlamaLinuxScripts.ReadLogTail,
                SshOperation.ReadLlamaCacheSizeLinux => RemoteLlamaLinuxScripts.ReadCacheSize,
                SshOperation.DiagnoseNvidiaDriverLinux => RemoteLlamaLinuxScripts.DiagnoseDriver,
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            Assert.Equal(expected, DecodeLinuxWrapper(command));
        }
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

    private static string DecodeLinuxWrapper(string script)
    {
        const string marker = "printf '%s' '";
        int markerIndex = script.IndexOf(marker, StringComparison.Ordinal); Assert.True(markerIndex >= 0);
        int start = markerIndex + marker.Length, end = script.IndexOf("' | base64 -d", start, StringComparison.Ordinal);
        Assert.True(end > start);
        return Encoding.UTF8.GetString(Convert.FromBase64String(script[start..end]));
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

    internal sealed class FakeSession(bool executableExists) : ISshSession
    {
        private bool _installed = executableExists;
        public bool IsWindows { get; set; } = true;
        public string OsRelease { get; set; } = "PRETTY_NAME=\"Ubuntu 22.04.4 LTS\"\nNAME=\"Ubuntu\"\nID=ubuntu";
        public int OsReleaseExitCode { get; set; }
        public string LinuxArchitecture { get; set; } = "x86_64";
        public string LinuxGpuOutput { get; set; } = "NVIDIA GeForce RTX 3090, 24576";
        public int LinuxGpuExitCode { get; set; }
        public string LinuxDriverDiagnosis { get; set; } = "";
        public bool LinuxInstallFails { get; set; }
        public string LinuxInstallError { get; set; } = "SHA256_MISMATCH synthetic-linux-archive.tar.gz";
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
                SshOperation.ReadOperatingSystem => new(OsReleaseExitCode, OsRelease, ""),
                SshOperation.ReadWindowsVersion => new(0, IsWindows ? "Microsoft Windows" : "", ""),
                SshOperation.ReadWindowsInfo => new(0, "Microsoft Windows Server 2022 10.0.20348", ""),
                SshOperation.ReadNvidiaGpuWindows => new(0, "NVIDIA RTX 4090, 24564", ""),
                SshOperation.ReadSystemDiskWindows => new(0, "300000000000", ""),
                SshOperation.TestLlamaServerExecutable => _installed ? new(0, "installed", "") : new(1, "", "missing"),
                SshOperation.ExtractLlamaCpp => Install(),
                SshOperation.ReadLlamaCacheSize => new(0, "1073741824", ""),
                SshOperation.ReadLlamaServerLogTail => new(0, LogTail, ""),
                SshOperation.ReadLinuxArchitecture => new(0, LinuxArchitecture, ""),
                SshOperation.ReadNvidiaGpuLinux => new(LinuxGpuExitCode, LinuxGpuOutput, ""),
                SshOperation.DiagnoseNvidiaDriverLinux => new(0, LinuxDriverDiagnosis, ""),
                SshOperation.ReadSystemDiskLinux => new(0, "300000000000", ""),
                SshOperation.TestLlamaServerLinux => _installed ? new(0, "installed", "") : new(1, "", "missing"),
                SshOperation.InstallLlamaCppLinux => LinuxInstallFails ? new(4, "", LinuxInstallError) : Install(),
                SshOperation.ReadLlamaCacheSizeLinux => new(0, "1073741824", ""),
                SshOperation.ReadLlamaServerLogTailLinux => new(0, LogTail, ""),
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
