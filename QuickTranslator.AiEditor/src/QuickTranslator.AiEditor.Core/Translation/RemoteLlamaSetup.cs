using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum RemoteLlamaSetupStage { Inspect, PreparePackage, Upload, Extract, Configure, DownloadModel, Verify, Done }
public sealed record RemoteLlamaSetupProgress(RemoteLlamaSetupStage Stage, string Message, double? Percent = null);
public enum RemoteHostPlatform { Unsupported, Windows, Linux }
public sealed record RemoteLlamaInspection(string OperatingSystem, string? GpuName, long? VramBytes, long? FreeDiskBytes,
    bool LlamaServerRunning, int Port, IReadOnlyList<string> Models, bool SupportsAutomaticSetup, string? BlockedReason = null,
    RemoteHostPlatform Platform = RemoteHostPlatform.Unsupported);
public sealed record RemoteLlamaSetupResult(bool Success, string Message, RemoteLlamaInspection? Inspection);
public sealed record LlamaServerHealth(bool Healthy, IReadOnlyList<string> Models);

public interface IRemoteLlamaHealthClient
{
    Task<LlamaServerHealth> CheckAsync(Uri tunnelEndpoint, CancellationToken cancellationToken = default);
}

public sealed class RemoteLlamaHealthClient(Func<HttpClient>? clientFactory = null) : IRemoteLlamaHealthClient
{
    private readonly Func<HttpClient> _clientFactory = clientFactory ?? (() => new HttpClient(new HttpClientHandler
        { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) });

    public async Task<LlamaServerHealth> CheckAsync(Uri tunnelEndpoint, CancellationToken cancellationToken = default)
    {
        Uri root = ValidateLoopback(tunnelEndpoint);
        using HttpClient client = _clientFactory();
        using HttpResponseMessage health = await client.GetAsync(new Uri(root, "/health"), cancellationToken).ConfigureAwait(false);
        if (health.StatusCode != HttpStatusCode.OK) return new(false, []);
        using HttpResponseMessage modelsResponse = await client.GetAsync(new Uri(root, "/v1/models"), cancellationToken).ConfigureAwait(false);
        if (!modelsResponse.IsSuccessStatusCode) return new(true, []);
        using JsonDocument document = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        var models = new List<string>();
        if (document.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in data.EnumerateArray())
                if (item.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.String) models.Add(id.GetString()!);
        return new(true, models);
    }

    private static Uri ValidateLoopback(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(endpoint.Host, out IPAddress? address) || !IPAddress.IsLoopback(address) ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Chỉ kiểm tra llama-server qua SSH loopback.", nameof(endpoint));
        return new UriBuilder(endpoint) { Path = "/", Query = "", Fragment = "" }.Uri;
    }
}

/// <summary>Tells a rented Windows machine from a Linux one over SSH and reads its NVIDIA GPU, VRAM and free disk.</summary>
public sealed class RemoteGpuProbe
{
    public async Task<RemoteLlamaInspection> InspectAsync(ISshSession session, int port,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        SshCommandResult unix = await session.ExecuteAsync(SshOperation.ReadOperatingSystem, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (unix.Success && LinuxRelease(unix.Output) is string release)
            return await InspectLinuxAsync(session, release, port, cancellationToken).ConfigureAwait(false);
        SshCommandResult version = await session.ExecuteAsync(SshOperation.ReadWindowsVersion, cancellationToken: cancellationToken).ConfigureAwait(false);
        string combined = unix.Output + unix.Error + version.Output + version.Error;
        bool windows = !unix.Success && (combined.Contains("Windows", StringComparison.OrdinalIgnoreCase) ||
            combined.Contains("is not recognized as an internal or external command", StringComparison.OrdinalIgnoreCase));
        if (!windows)
            return new("Hệ điều hành không được hỗ trợ", null, null, null, false, port, [], false,
                "Cài đặt tự động llama-server chỉ hỗ trợ máy thuê Windows hoặc Linux (Ubuntu).");
        await session.ExecuteAsync(SshOperation.ConfirmWindowsMux, cancellationToken: cancellationToken).ConfigureAwait(false);
        SshCommandResult info = await session.ExecuteAsync(SshOperation.ReadWindowsInfo, cancellationToken: cancellationToken).ConfigureAwait(false);
        SshCommandResult gpu = await session.ExecuteAsync(SshOperation.ReadNvidiaGpuWindows, cancellationToken: cancellationToken).ConfigureAwait(false);
        SshCommandResult disk = await session.ExecuteAsync(SshOperation.ReadSystemDiskWindows, cancellationToken: cancellationToken).ConfigureAwait(false);
        string gpuText = gpu.Output.Trim(); bool noNvidia = !gpu.Success || gpuText.StartsWith("NONVIDIA:", StringComparison.Ordinal);
        (string? name, long? vram) = noNvidia ? (null, null) : ParseGpu(gpuText);
        long? free = long.TryParse(disk.Output.Trim(), out long bytes) ? bytes : null;
        string os = string.IsNullOrWhiteSpace(info.Output) ? "Windows" : info.Output.Trim();
        string? blocked = noNvidia ? "Máy Windows chưa có driver NVIDIA (không tìm thấy nvidia-smi)." : null;
        return new(os, name, vram, free, false, port, [], blocked is null, blocked, RemoteHostPlatform.Windows);
    }

    private static async Task<RemoteLlamaInspection> InspectLinuxAsync(ISshSession session, string release, int port,
        CancellationToken cancellationToken)
    {
        SshCommandResult arch = await session.ExecuteAsync(SshOperation.ReadLinuxArchitecture, cancellationToken: cancellationToken).ConfigureAwait(false);
        SshCommandResult gpu = await session.ExecuteAsync(SshOperation.ReadNvidiaGpuLinux, cancellationToken: cancellationToken).ConfigureAwait(false);
        SshCommandResult disk = await session.ExecuteAsync(SshOperation.ReadSystemDiskLinux, cancellationToken: cancellationToken).ConfigureAwait(false);
        string machine = arch.Output.Trim();
        string gpuText = gpu.Output.Trim(); bool noNvidia = !gpu.Success || gpuText.Length == 0 || gpuText.StartsWith("NONVIDIA:", StringComparison.Ordinal);
        (string? name, long? vram) = noNvidia ? (null, null) : ParseGpu(gpuText);
        long? free = long.TryParse(disk.Output.Trim(), out long bytes) ? bytes : null;
        string? blocked = !string.Equals(machine, "x86_64", StringComparison.Ordinal)
            ? $"Cài đặt tự động trên Linux cần máy x86_64 (máy này: {(machine.Length == 0 ? "không rõ" : machine)})."
            : noNvidia ? "Máy Linux chưa có driver NVIDIA (nvidia-smi không chạy được)." : null;
        string os = machine.Length == 0 ? release : release + " · " + machine;
        return new(os, name, vram, free, false, port, [], blocked is null, blocked, RemoteHostPlatform.Linux);
    }

    /// <summary>PRETTY_NAME (or NAME) from /etc/os-release; null when the text is not an os-release file.</summary>
    internal static string? LinuxRelease(string osRelease)
    {
        string? pretty = null, name = null;
        foreach (string raw in osRelease.Split('\n'))
        {
            string line = raw.Trim(); int equals = line.IndexOf('=');
            if (equals < 1) continue;
            string value = line[(equals + 1)..].Trim().Trim('"', '\'');
            switch (line[..equals])
            {
                case "PRETTY_NAME": pretty = value; break;
                case "NAME": name = value; break;
                case "ID": name ??= value; break;
            }
        }
        string? result = !string.IsNullOrWhiteSpace(pretty) ? pretty : name;
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static (string?, long?) ParseGpu(string output)
    {
        string line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        int comma = line.LastIndexOf(',');
        if (comma < 1 || !long.TryParse(line[(comma + 1)..].Trim(), out long mib)) return (line.Length == 0 ? null : line, null);
        return (line[..comma].Trim(), checked(mib * 1024L * 1024L));
    }
}

public sealed class RemoteLlamaSetup
{
    public const long MinimumFreeDiskBytes = 12L * 1024 * 1024 * 1024;
    private readonly ILlamaCppPackageProvider _packages;
    private readonly IRemoteLlamaHealthClient _health;
    private readonly RemoteGpuProbe _probe;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _pollInterval, _healthTimeout;

    /// <summary>The commands one setup run uses, per rented operating system.</summary>
    private sealed record PlatformOperations(SshOperation TestExecutable, SshOperation Configure,
        SshOperation CacheSize, SshOperation LogTail);
    private static readonly PlatformOperations WindowsOperations = new(SshOperation.TestLlamaServerExecutable,
        SshOperation.ConfigureLlamaServerTask, SshOperation.ReadLlamaCacheSize, SshOperation.ReadLlamaServerLogTail);
    private static readonly PlatformOperations LinuxOperations = new(SshOperation.TestLlamaServerLinux,
        SshOperation.ConfigureLlamaServerLinux, SshOperation.ReadLlamaCacheSizeLinux, SshOperation.ReadLlamaServerLogTailLinux);

    public RemoteLlamaSetup(ILlamaCppPackageProvider packages, IRemoteLlamaHealthClient? health = null,
        RemoteGpuProbe? probe = null, Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? pollInterval = null, TimeSpan? healthTimeout = null)
    {
        _packages = packages ?? throw new ArgumentNullException(nameof(packages)); _health = health ?? new RemoteLlamaHealthClient();
        _probe = probe ?? new(); _delay = delay ?? Task.Delay; _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
        _healthTimeout = healthTimeout ?? TimeSpan.FromMinutes(45);
    }

    public async Task<RemoteLlamaInspection> InspectAsync(ISshSession session, Uri tunnelEndpoint, int port,
        CancellationToken cancellationToken = default)
    {
        RemoteLlamaInspection result = await _probe.InspectAsync(session, port, cancellationToken).ConfigureAwait(false);
        if (!result.SupportsAutomaticSetup) return result;
        try { LlamaServerHealth health = await _health.CheckAsync(tunnelEndpoint, cancellationToken).ConfigureAwait(false);
            return result with { LlamaServerRunning = health.Healthy, Models = health.Models }; }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return result; }
    }

    public async Task<RemoteLlamaSetupResult> RunAsync(ISshSession session, Uri tunnelEndpoint, string model,
        int port, int slots, int contextPerSlot, IProgress<RemoteLlamaSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var launch = new LlamaServerLaunch(GgufModelReference.Parse(model), port, slots, contextPerSlot); launch.Validate();
        progress?.Report(new(RemoteLlamaSetupStage.Inspect, "Đang nhận diện máy thuê…"));
        RemoteLlamaInspection inspection = await _probe.InspectAsync(session, port, cancellationToken).ConfigureAwait(false);
        if (!inspection.SupportsAutomaticSetup) return new(false, inspection.BlockedReason!, inspection);
        if (inspection.FreeDiskBytes is long free && free < MinimumFreeDiskBytes)
            return new(false, $"Ổ đĩa máy thuê chỉ còn {free / 1024d / 1024 / 1024:0.0} GB, không đủ để tải model.", inspection);
        bool linux = inspection.Platform == RemoteHostPlatform.Linux;
        PlatformOperations operations = linux ? LinuxOperations : WindowsOperations;

        SshCommandResult executable = await session.ExecuteAsync(operations.TestExecutable,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!executable.Success && linux)
        {
            // The rental downloads the pinned Linux CUDA build from GitHub itself and checks its SHA-256.
            progress?.Report(new(RemoteLlamaSetupStage.Extract, "Máy thuê đang tải và giải nén llama.cpp CUDA cho Linux (khoảng 770 MB)…"));
            await RequireAsync(session, SshOperation.InstallLlamaCppLinux, null, cancellationToken).ConfigureAwait(false);
            await RequireAsync(session, SshOperation.TestLlamaServerLinux, null, cancellationToken).ConfigureAwait(false);
        }
        else if (!executable.Success)
        {
            progress?.Report(new(RemoteLlamaSetupStage.PreparePackage, "Đang chuẩn bị llama.cpp trên máy này…"));
            LlamaCppPackage package = await _packages.EnsureAsync(new TextProgress(text =>
                progress?.Report(new(RemoteLlamaSetupStage.PreparePackage, text))), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(package.Build, LlamaCppRelease.Build, StringComparison.Ordinal) || package.ZipPaths.Count != 2)
                throw new InvalidOperationException("Gói llama.cpp không đúng bản đã ghim " + LlamaCppRelease.Build + ".");
            long total = package.ZipPaths.Sum(path => new FileInfo(path).Length), completed = 0;
            foreach (string zip in package.ZipPaths)
            {
                long prior = completed, length = new FileInfo(zip).Length;
                var upload = new ByteProgress(bytes => progress?.Report(new(RemoteLlamaSetupStage.Upload,
                    $"Đang tải {Path.GetFileName(zip)} lên máy thuê…", Math.Clamp((prior + bytes) * 100d / total, 0, 100))));
                await session.UploadFileAsync(zip, RemoteLlamaWindowsScripts.DownloadDirectory + "\\" + Path.GetFileName(zip), upload, cancellationToken).ConfigureAwait(false);
                completed += length;
            }
            progress?.Report(new(RemoteLlamaSetupStage.Extract, "Đang giải nén llama.cpp và CUDA runtime…"));
            await RequireAsync(session, SshOperation.ExtractLlamaCpp, null, cancellationToken).ConfigureAwait(false);
            await RequireAsync(session, SshOperation.TestLlamaServerExecutable, null, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new(RemoteLlamaSetupStage.Configure, linux ? "Đang khởi động lại llama-server…" : "Đang đăng ký llama-server tự khởi động…"));
        string specification = JsonSerializer.Serialize(new RemoteLlamaTaskSpecification(model, port, slots, contextPerSlot));
        await RequireAsync(session, operations.Configure, specification, cancellationToken).ConfigureAwait(false);
        DateTimeOffset deadline = DateTimeOffset.UtcNow + _healthTimeout;
        while (DateTimeOffset.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                LlamaServerHealth state = await _health.CheckAsync(tunnelEndpoint, cancellationToken).ConfigureAwait(false);
                if (state.Healthy)
                {
                    RemoteLlamaInspection final = inspection with { LlamaServerRunning = true, Models = state.Models };
                    progress?.Report(new(RemoteLlamaSetupStage.Done, "llama-server đã sẵn sàng.", 100));
                    return new(true, $"Đã sẵn sàng: {final.GpuName} · {launch.Model.DisplayName}.", final);
                }
            }
            // A probe timeout (server still starting or downloading) is "not ready yet"; only the caller's token cancels.
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
            SshCommandResult size = await session.ExecuteAsync(operations.CacheSize, cancellationToken: cancellationToken).ConfigureAwait(false);
            string downloaded = long.TryParse(size.Output.Trim(), out long bytes) ? $" · đã tải {bytes / 1024d / 1024 / 1024:0.0} GB" : "";
            progress?.Report(new(RemoteLlamaSetupStage.DownloadModel, "llama-server đang tải/khởi tạo model" + downloaded + "…"));
            await _delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }
        SshCommandResult log = await session.ExecuteAsync(operations.LogTail, cancellationToken: cancellationToken).ConfigureAwait(false);
        string tail = SafeTail(log.Output + (string.IsNullOrWhiteSpace(log.Error) ? "" : "\n" + log.Error));
        return new(false, "llama-server không sẵn sàng trước thời hạn." + (tail.Length == 0 ? "" : "\nLog cuối:\n" + tail), inspection);
    }

    private static async Task RequireAsync(ISshSession session, SshOperation operation, string? argument, CancellationToken token)
    { SshCommandResult result = await session.ExecuteAsync(operation, argument, token).ConfigureAwait(false);
        if (!result.Success) throw new InvalidOperationException("Máy thuê từ chối bước cài đặt: " +
            SafeTail(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error)); }
    private static string SafeTail(string value) => value.Trim().Length <= 3000 ? value.Trim() : value.Trim()[^3000..];
    private sealed class TextProgress(Action<string> report) : IProgress<string> { public void Report(string value) => report(value); }
    private sealed class ByteProgress(Action<long> report) : IProgress<long> { public void Report(long value) => report(value); }
}

internal sealed record RemoteLlamaTaskSpecification(string Model, int Port, int Slots, int ContextPerSlot);

public static class RemoteLlamaWindowsScripts
{
    public const string Root = @"C:\ProgramData\QTStudio";
    public const string DownloadDirectory = Root + @"\dl";
    public const string InstallDirectory = Root + @"\llamacpp\" + LlamaCppRelease.Build;
    public const string Executable = InstallDirectory + @"\llama-server.exe";
    public const string LogPath = Root + @"\logs\llama-server.log";
    public const string TestExecutable = "if(Test-Path '" + Executable + "'){'installed'}else{exit 1}";
    public const string ExtractPackage =
        "$ErrorActionPreference='Stop';$d='" + InstallDirectory + "';New-Item -ItemType Directory -Force $d|Out-Null;" +
        "Expand-Archive -LiteralPath '" + DownloadDirectory + @"\" + LlamaCppRelease.ServerZip + "' -DestinationPath $d -Force;" +
        "Expand-Archive -LiteralPath '" + DownloadDirectory + @"\" + LlamaCppRelease.CudaRuntimeZip + "' -DestinationPath $d -Force;" +
        "if(-not(Test-Path '" + Executable + "')){throw 'LLAMA_SERVER_EXE_MISSING'}";
    public const string ReadLogTail = "if(Test-Path '" + LogPath + "'){Get-Content -LiteralPath '" + LogPath + "' -Tail 80}else{'(log empty)'}";
    public const string ReadCacheSize = "$p='" + Root + @"\models\llama';if(Test-Path $p){(Get-ChildItem -LiteralPath $p -File -Recurse -ErrorAction SilentlyContinue|Measure-Object Length -Sum).Sum}else{0}";

    public static string ConfigureTask(string? json)
    {
        RemoteLlamaTaskSpecification spec;
        try { spec = JsonSerializer.Deserialize<RemoteLlamaTaskSpecification>(json ?? "") ?? throw new JsonException(); }
        catch (JsonException e) { throw new ArgumentException("Cấu hình llama-server không hợp lệ.", nameof(json), e); }
        var launch = new LlamaServerLaunch(GgufModelReference.Parse(spec.Model), spec.Port, spec.Slots, spec.ContextPerSlot);
        // A .cmd wrapper, not PowerShell: Windows PowerShell 5.1 turns each stderr line of a redirected native program
        // into an error record, so llama-server's first log line ended the script (exit 1, empty log) on a real rental.
        string arguments = string.Join(" ", launch.Arguments().Select(CmdQuote));
        string wrapper = "@echo off\r\nchcp 65001 >nul\r\nset \"LLAMA_CACHE=" + Root + "\\models\\llama\"\r\n" +
            CmdQuote(Executable) + " " + arguments + " >> " + CmdQuote(LogPath) + " 2>&1\r\nexit /b %ERRORLEVEL%\r\n";
        string encodedWrapper = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(wrapper));
        return "$ErrorActionPreference='Stop';$root='" + Root + "';New-Item -ItemType Directory -Force \"$root\\logs\",\"$root\\models\\llama\"|Out-Null;" +
            "$script=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + encodedWrapper + "'));" +
            "[IO.File]::WriteAllText(\"$root\\llama-server.cmd\",$script,(New-Object Text.UTF8Encoding($false)));" +
            "Stop-ScheduledTask -TaskName 'QTStudio llama-server' -ErrorAction SilentlyContinue;" +
            // Stopping a task leaves its ollama.exe child running (seen on a real rental), and the task would come back
            // at the next boot: disable it, then end QT Studio's Ollama and any hand-started llama-server on this port.
            "$old=Get-ScheduledTask -TaskName 'QTStudio Ollama' -ErrorAction SilentlyContinue;if($old){Stop-ScheduledTask -TaskName 'QTStudio Ollama' -ErrorAction SilentlyContinue;Disable-ScheduledTask -TaskName 'QTStudio Ollama' -ErrorAction SilentlyContinue|Out-Null};" +
            "Get-Process ollama*,llama-server -ErrorAction SilentlyContinue|Where-Object{$_.Path -like ($root+'\\*')}|Stop-Process -Force -ErrorAction SilentlyContinue;" +
            "$a=New-ScheduledTaskAction -Execute 'cmd.exe' -Argument '/d /c \"C:\\ProgramData\\QTStudio\\llama-server.cmd\"';" +
            "$t=New-ScheduledTaskTrigger -AtStartup;$p=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest;" +
            "$s=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 99 -RestartInterval (New-TimeSpan -Minutes 1);" +
            "Register-ScheduledTask -TaskName 'QTStudio llama-server' -Action $a -Trigger $t -Principal $p -Settings $s -Force|Out-Null;" +
            "Start-ScheduledTask -TaskName 'QTStudio llama-server'";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    // Inside double quotes cmd treats & | < > ^ literally; only % still expands. Windows paths cannot contain '"'.
    internal static string CmdQuote(string value) => value.Contains('"', StringComparison.Ordinal)
        ? throw new ArgumentException("Tham số llama-server không được chứa dấu nháy kép.", nameof(value))
        : "\"" + value.Replace("%", "%%", StringComparison.Ordinal) + "\"";
}
