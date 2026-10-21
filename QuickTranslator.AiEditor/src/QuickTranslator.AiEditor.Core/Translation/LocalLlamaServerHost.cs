using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum LocalLlamaServerState { Stopped, Installing, Starting, DownloadingModel, Ready, Error }

public sealed record LocalLlamaServerStatus(LocalLlamaServerState State, string Message, int? Port = null)
{
    public bool Ready => State == LocalLlamaServerState.Ready;
}

public interface ILocalLlamaProcess : IDisposable
{
    bool HasExited { get; }
    int ExitCode { get; }
    void Kill();
}

public interface ILocalLlamaProcessLauncher
{
    ILocalLlamaProcess Start(ProcessStartInfo startInfo);
}

public sealed class LocalLlamaServerHost : IAsyncDisposable
{
    private readonly ILlamaCppPackageProvider _packages;
    private readonly ILocalLlamaProcessLauncher _launcher;
    private readonly HttpClient _healthClient;
    private readonly string _dataRoot;
    private readonly Func<int> _portSelector;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ILocalLlamaProcess? _process;
    private LocalConfiguration? _configuration;
    private JobHandle? _job;

    public LocalLlamaServerHost(ILlamaCppPackageProvider? packageProvider = null,
        ILocalLlamaProcessLauncher? processLauncher = null, HttpClient? healthClient = null,
        string? dataRoot = null, Func<int>? portSelector = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _packages = packageProvider ?? new LlamaCppPackageProvider();
        _launcher = processLauncher ?? new WindowsProcessLauncher();
        _healthClient = healthClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        _dataRoot = Path.GetFullPath(dataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QTStudio"));
        _portSelector = portSelector ?? SelectPort;
        _delay = delay ?? Task.Delay;
    }

    public Uri? Endpoint { get; private set; }
    public LocalLlamaServerStatus Status { get; private set; } =
        new(LocalLlamaServerState.Stopped, "llama-server máy này: đã dừng");
    public event EventHandler<LocalLlamaServerStatus>? StatusChanged;

    public async Task<Uri> StartAsync(OllamaBackendProfile profile, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile = profile.Clone();
        profile.Validate();
        if (profile.Kind != OllamaBackendKind.Local || profile.Api != BackendApi.OpenAiCompatible)
            throw new InvalidOperationException("llama-server chỉ chạy profile Local dùng API OpenAI Compatible.");
        GgufModelReference model = GgufModelReference.Parse(profile.Model);
        var wanted = new LocalConfiguration(model.ToString(), profile.ParallelRequests,
            profile.Runtime!.ModelOptions.ContextSize);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _configuration == wanted && Endpoint is not null) return Endpoint;
            StopCore();
            SetStatus(LocalLlamaServerState.Installing, "llama-server máy này: đang tải llama.cpp");
            var packageProgress = new Progress<string>(text => { progress?.Report(text); SetStatus(LocalLlamaServerState.Installing, text); });
            LlamaCppPackage package = await _packages.EnsureAsync(packageProgress, cancellationToken).ConfigureAwait(false);
            int port = _portSelector();
            var launch = new LlamaServerLaunch(model, port, profile.ParallelRequests,
                profile.Runtime.ModelOptions.ContextSize);
            string logs = Path.Combine(_dataRoot, "logs");
            string cache = Path.Combine(_dataRoot, "models");
            Directory.CreateDirectory(logs);
            Directory.CreateDirectory(cache);
            string logPath = Path.Combine(logs, "llama-server.log");
            var start = new ProcessStartInfo(package.ServerExecutable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in launch.Arguments()) start.ArgumentList.Add(argument);
            start.Environment["LLAMA_CACHE"] = cache;
            start.Environment["QTSTUDIO_LLAMA_LOG"] = logPath;
            _process = _launcher.Start(start);
            if (_process is WindowsProcess windowsProcess)
            {
                _job = JobHandle.Create();
                _job.Assign(windowsProcess.Process);
                windowsProcess.CopyOutputTo(logPath);
            }
            _configuration = wanted;
            Endpoint = new Uri($"http://127.0.0.1:{port}/api/chat");
            SetStatus(LocalLlamaServerState.Starting, "llama-server máy này: đang tải model…", port);
            await WaitForHealthAsync(port, cache, logPath, progress, cancellationToken).ConfigureAwait(false);
            SetStatus(LocalLlamaServerState.Ready, $"llama-server máy này: sẵn sàng ở cổng {port}", port);
            return Endpoint;
        }
        catch (OperationCanceledException)
        {
            StopCore();
            SetStatus(LocalLlamaServerState.Stopped, "llama-server máy này: đã hủy khởi động");
            throw;
        }
        catch (Exception error)
        {
            StopCore();
            SetStatus(LocalLlamaServerState.Error, "llama-server máy này: lỗi — " + error.Message);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { StopCore(); SetStatus(LocalLlamaServerState.Stopped, "llama-server máy này: đã dừng"); }
        finally { _gate.Release(); }
    }

    private async Task WaitForHealthAsync(int port, string cache, string logPath, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var health = new Uri($"http://127.0.0.1:{port}/health");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process?.HasExited != false)
            {
                string tail = ReadTail(logPath, 30);
                throw new InvalidOperationException($"llama-server đã thoát (mã {_process?.ExitCode})." +
                    (tail.Length == 0 ? "" : Environment.NewLine + "Nhật ký cuối:" + Environment.NewLine + tail));
            }
            try
            {
                using HttpResponseMessage response = await _healthClient.GetAsync(health, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            string message;
            if (TryReadDownloadProgress(logPath, out double completedGb, out double totalGb))
                message = $"llama-server máy này: đang tải model {completedGb:0.00}/{totalGb:0.00} GB";
            else
            {
                long bytes = Directory.Exists(cache) ? Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories)
                    .Select(path => { try { return new FileInfo(path).Length; } catch (IOException) { return 0; } }).Sum() : 0;
                message = $"llama-server máy này: đang tải model {bytes / 1024d / 1024d / 1024d:0.00} GB";
            }
            progress?.Report(message);
            SetStatus(LocalLlamaServerState.DownloadingModel, message, port);
            await _delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private void StopCore()
    {
        try { if (_process is { HasExited: false }) _process.Kill(); } catch (InvalidOperationException) { }
        _process?.Dispose();
        _process = null;
        _job?.Dispose();
        _job = null;
        _configuration = null;
        Endpoint = null;
    }

    private void SetStatus(LocalLlamaServerState state, string message, int? port = null)
    {
        Status = new(state, message, port);
        StatusChanged?.Invoke(this, Status);
    }

    private static int SelectPort()
    {
        if (CanBind(11450)) return 11450;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool CanBind(int port)
    {
        try { using var listener = new TcpListener(IPAddress.Loopback, port); listener.Start(); return true; }
        catch (SocketException) { return false; }
    }

    private static string ReadTail(string path, int lines)
    {
        try { return string.Join(Environment.NewLine, File.ReadLines(path).TakeLast(lines)); }
        catch (IOException) { return ""; }
    }

    private static bool TryReadDownloadProgress(string logPath, out double completedGb, out double totalGb)
    {
        completedGb = totalGb = 0;
        MatchCollection matches = Regex.Matches(ReadTail(logPath, 200),
            @"(?<done>\d+(?:[.,]\d+)?)\s*(?<doneUnit>GiB|GB|MiB|MB)\s*/\s*(?<total>\d+(?:[.,]\d+)?)\s*(?<totalUnit>GiB|GB|MiB|MB)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (matches.Count == 0) return false;
        Match match = matches[^1];
        if (!double.TryParse(match.Groups["done"].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double completed) ||
            !double.TryParse(match.Groups["total"].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double total)) return false;
        completedGb = ToGb(completed, match.Groups["doneUnit"].Value);
        totalGb = ToGb(total, match.Groups["totalUnit"].Value);
        return totalGb > 0;
    }

    private static double ToGb(double value, string unit) => unit.StartsWith("M", StringComparison.OrdinalIgnoreCase)
        ? value / 1024d : value;

    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); _gate.Dispose(); }
    private sealed record LocalConfiguration(string Model, int Slots, int Context);
}

internal sealed class WindowsProcessLauncher : ILocalLlamaProcessLauncher
{
    public ILocalLlamaProcess Start(ProcessStartInfo startInfo) =>
        new WindowsProcess(Process.Start(startInfo) ?? throw new InvalidOperationException("Không khởi động được llama-server."));
}

internal sealed class WindowsProcess(Process process) : ILocalLlamaProcess
{
    public Process Process { get; } = process;
    public bool HasExited => Process.HasExited;
    public int ExitCode => Process.ExitCode;
    public void Kill() { Process.Kill(entireProcessTree: true); Process.WaitForExit(5000); }
    public void Dispose() => Process.Dispose();
    public void CopyOutputTo(string path)
    {
        // A fresh log per start: download progress and the error tail must never come from an earlier run.
        TextWriter writer = TextWriter.Synchronized(new StreamWriter(new FileStream(path, FileMode.Create,
            FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
        Process.OutputDataReceived += (_, e) => { if (e.Data is not null) writer.WriteLine(e.Data); };
        Process.ErrorDataReceived += (_, e) => { if (e.Data is not null) writer.WriteLine(e.Data); };
        Process.Exited += (_, _) => writer.Dispose();
        Process.EnableRaisingEvents = true;
        Process.BeginOutputReadLine();
        Process.BeginErrorReadLine();
    }
}

internal sealed class JobHandle : SafeHandle
{
    private JobHandle() : base(IntPtr.Zero, true) { }
    public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);
    public static JobHandle Create()
    {
        JobHandle job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = 0x2000 }
        };
        int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        IntPtr memory = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, memory, false);
            if (!SetInformationJobObject(job, 9, memory, (uint)size))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(memory); }
        return job;
    }
    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(this, process.Handle))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
    protected override bool ReleaseHandle() => CloseHandle(handle);
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectBasicLimitInformation
    { public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct JobObjectExtendedLimitInformation
    { public JobObjectBasicLimitInformation BasicLimitInformation; public IoCounters IoInfo; public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern JobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(JobHandle job, int infoClass, IntPtr info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(JobHandle job, IntPtr process);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr value);
}
