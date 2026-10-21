using System.Diagnostics;
using System.Net;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class LocalLlamaServerHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-host-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Starts_with_launch_contract_and_stops()
    {
        var process = new FakeProcess();
        var launcher = new FakeLauncher(process);
        await using var host = Host(launcher, _ => new(HttpStatusCode.OK));

        Uri endpoint = await host.StartAsync(GpuBackendProfile.CreateDefaultLocal());

        Assert.Equal("http://127.0.0.1:12001/api/chat", endpoint.ToString());
        Assert.Contains("-hf", launcher.Info!.ArgumentList);
        Assert.Contains("-np", launcher.Info.ArgumentList);
        Assert.Contains("4", launcher.Info.ArgumentList);
        Assert.Contains((8192 * 4).ToString(), launcher.Info.ArgumentList);
        Assert.Equal(Path.Combine(_root, "models"), launcher.Info.Environment["LLAMA_CACHE"]);
        await host.StopAsync();
        Assert.True(process.Killed);
    }

    [Fact]
    public async Task Same_configuration_reuses_process_but_change_restarts()
    {
        var launcher = new FakeLauncher();
        await using var host = Host(launcher, _ => new(HttpStatusCode.OK));
        GpuBackendProfile profile = GpuBackendProfile.CreateDefaultLocal();
        await host.StartAsync(profile);
        await host.StartAsync(profile);
        profile.ParallelRequests = 2;
        await host.StartAsync(profile);

        Assert.Equal(2, launcher.Starts);
        Assert.True(launcher.Processes[0].Killed);
    }

    [Fact]
    public async Task Exited_process_reports_exit_code_and_log_tail()
    {
        var process = new FakeProcess { HasExitedValue = true, ExitCodeValue = 17 };
        var launcher = new FakeLauncher(process, info =>
        {
            string log = info.Environment["QTSTUDIO_LLAMA_LOG"]!;
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            File.WriteAllText(log, "synthetic last line");
        });
        await using var host = Host(launcher, _ => throw new HttpRequestException());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(GpuBackendProfile.CreateDefaultLocal()));

        Assert.Contains("17", error.Message);
        Assert.Contains("synthetic last line", error.Message);
        Assert.Equal(LocalLlamaServerState.Error, host.Status.State);
    }

    private LocalLlamaServerHost Host(FakeLauncher launcher, Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new FakePackage(_root), launcher, new HttpClient(new Handler(response)), _root, () => 12001,
            (_, token) => Task.Delay(1, token));

    private sealed class FakePackage(string root) : ILlamaCppPackageProvider
    {
        public Task<LlamaCppPackage> EnsureAsync(IProgress<string>? progress = null, CancellationToken token = default)
        {
            string exe = Path.Combine(root, "package", "llama-server.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllText(exe, "fake");
            return Task.FromResult(new LlamaCppPackage(LlamaCppRelease.Build, [], exe));
        }
    }
    private sealed class FakeLauncher(FakeProcess? process = null, Action<ProcessStartInfo>? started = null) : ILocalLlamaProcessLauncher
    {
        public int Starts { get; private set; }
        public ProcessStartInfo? Info { get; private set; }
        public List<FakeProcess> Processes { get; } = [];
        public ILocalLlamaProcess Start(ProcessStartInfo info)
        {
            Starts++; Info = info; started?.Invoke(info);
            FakeProcess next = Starts == 1 && process is not null ? process : new FakeProcess();
            Processes.Add(next); return next;
        }
    }
    private sealed class FakeProcess : ILocalLlamaProcess
    {
        public bool HasExitedValue { get; set; }
        public int ExitCodeValue { get; set; }
        public bool Killed { get; private set; }
        public bool HasExited => HasExitedValue;
        public int ExitCode => ExitCodeValue;
        public void Kill() { Killed = true; HasExitedValue = true; }
        public void Dispose() { }
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
