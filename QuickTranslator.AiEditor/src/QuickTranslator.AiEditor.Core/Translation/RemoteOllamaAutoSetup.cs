using System.Text.Json;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum AutoSetupStage { Inspect, InstallOllama, StartOllama, PullModel, Verify, Done }

public sealed record AutoSetupProgress(AutoSetupStage Stage, string Message, double? Percent = null);

public sealed record AutoSetupResult(bool Success, string Message, RemoteGpuInspection? Inspection);

/// <summary>
/// One confirmed plan that detects the rented machine (Ubuntu or Windows), installs and starts Ollama when it is
/// missing, downloads the chosen model through the SSH loopback tunnel and checks that the model really answers.
/// It never installs or changes NVIDIA drivers.
/// </summary>
public sealed class RemoteOllamaAutoSetup
{
    public const long MinimumFreeDiskBytes = 12L * 1024 * 1024 * 1024;

    private readonly RemoteOllamaSetupService _setup;
    private readonly RemoteOllamaModelPullService _pull;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _pollInterval;
    private readonly TimeSpan _provisionTimeout;

    public RemoteOllamaAutoSetup(RemoteOllamaSetupService? setup = null, RemoteOllamaModelPullService? pull = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? pollInterval = null,
        TimeSpan? provisionTimeout = null)
    {
        _setup = setup ?? new RemoteOllamaSetupService();
        _pull = pull ?? new RemoteOllamaModelPullService();
        _delay = delay ?? Task.Delay;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(3);
        _provisionTimeout = provisionTimeout ?? TimeSpan.FromMinutes(45);
    }

    public async Task<AutoSetupResult> RunAsync(ISshSession session, Uri tunnelEndpoint, string model,
        Func<string?>? requestSudoPassword = null, IProgress<AutoSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tunnelEndpoint);
        if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("Hãy nhập tên model cần tải.", nameof(model));

        progress?.Report(new(AutoSetupStage.Inspect, "Đang nhận diện máy thuê…"));
        RemoteGpuInspection inspection = await _setup.InspectAsync(session, cancellationToken).ConfigureAwait(false);
        if (!inspection.SupportsAutomaticSetup)
            return new(false, inspection.BlockedReason ?? "Máy thuê chưa hỗ trợ cài đặt tự động.", inspection);
        if (inspection.GpuName is null)
            return new(false, "Không đọc được GPU NVIDIA trên máy thuê (nvidia-smi không trả kết quả).", inspection);

        bool modelPresent = HasModel(inspection, model);
        if (!modelPresent && inspection.FreeDiskBytes is long free && free < MinimumFreeDiskBytes)
            return new(false, $"Ổ đĩa máy thuê chỉ còn {free / 1024d / 1024 / 1024:0.0} GB, không đủ để tải model.", inspection);

        if (!inspection.OllamaInstalled || !inspection.OllamaServiceActive)
        {
            AutoSetupResult? failure = inspection.OsFamily == RemoteOsFamily.Windows
                ? await ProvisionWindowsAsync(session, progress, cancellationToken).ConfigureAwait(false)
                : await ProvisionLinuxAsync(session, inspection, requestSudoPassword, progress, cancellationToken)
                    .ConfigureAwait(false);
            if (failure is not null) return failure with { Inspection = inspection };
        }

        if (!modelPresent)
        {
            progress?.Report(new(AutoSetupStage.PullModel, $"Đang tải model {model}…", 0));
            var pullProgress = new InlineProgress(p => progress?.Report(new(AutoSetupStage.PullModel,
                p.Percentage is double percent ? $"{p.Status} · {percent:0.0}%" : p.Status, p.Percentage)));
            await _pull.PullAsync(tunnelEndpoint, model, pullProgress, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new(AutoSetupStage.Verify, "Đang kiểm tra Ollama và model…"));
        RemoteGpuInspection final = await _setup.InspectAsync(session, cancellationToken).ConfigureAwait(false);
        if (!final.OllamaServiceActive)
            return new(false, "Ollama đã cài nhưng chưa phản hồi trên máy thuê.", final);
        if (!HasModel(final, model))
            return new(false, $"Ollama chạy nhưng chưa thấy model {model}.", final);
        progress?.Report(new(AutoSetupStage.Done, "Đã sẵn sàng.", 100));
        return new(true, $"Đã sẵn sàng: {final.GpuName} · model {model}.", final);
    }

    private static bool HasModel(RemoteGpuInspection inspection, string model) =>
        inspection.Models.Any(name => name.Equals(model, StringComparison.OrdinalIgnoreCase) ||
                                      (!model.Contains(':') && name.Equals(model + ":latest", StringComparison.OrdinalIgnoreCase)));

    private async Task<AutoSetupResult?> ProvisionLinuxAsync(ISshSession session, RemoteGpuInspection inspection,
        Func<string?>? requestSudoPassword, IProgress<AutoSetupProgress>? progress, CancellationToken cancellationToken)
    {
        if (!inspection.OllamaInstalled)
        {
            progress?.Report(new(AutoSetupStage.InstallOllama, "Đang cài Ollama (script chính thức)…"));
            SshCommandResult install = await RunWithSudoAsync(session, RemoteSetupAction.InstallOllamaUbuntu,
                requestSudoPassword, cancellationToken).ConfigureAwait(false);
            if (!install.Success) return new(false, "Cài Ollama thất bại: " + install.Error.Trim(), null);
        }
        if (!inspection.OllamaServiceActive)
        {
            progress?.Report(new(AutoSetupStage.StartOllama, "Đang bật dịch vụ Ollama…"));
            SshCommandResult start = await RunWithSudoAsync(session, RemoteSetupAction.StartOllamaService,
                requestSudoPassword, cancellationToken).ConfigureAwait(false);
            if (!start.Success) return new(false, "Bật Ollama thất bại: " + start.Error.Trim(), null);
        }
        return null;
    }

    private async Task<SshCommandResult> RunWithSudoAsync(ISshSession session, RemoteSetupAction action,
        Func<string?>? requestSudoPassword, CancellationToken cancellationToken)
    {
        SshCommandResult result = await _setup.ExecuteConfirmedAsync(session, action, true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.Success || !result.Error.StartsWith("SUDO_PASSWORD_REQUIRED:", StringComparison.Ordinal))
            return result;
        string? password = requestSudoPassword?.Invoke();
        if (string.IsNullOrEmpty(password)) return result with { Error = "Đã hủy nhập mật khẩu sudo." };
        return await _setup.ExecuteConfirmedAsync(session, action, true, cancellationToken: cancellationToken,
            sudoPassword: password).ConfigureAwait(false);
    }

    private async Task<AutoSetupResult?> ProvisionWindowsAsync(ISshSession session,
        IProgress<AutoSetupProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new(AutoSetupStage.InstallOllama, "Đang gửi script cài đặt lên máy Windows…"));
        IReadOnlyList<string> chunks = WindowsRemoteScripts.SplitProvisionScript();
        for (int index = 0; index < chunks.Count; index++)
            await Require(session, SshOperation.WriteWindowsProvisionChunk, $"{index}:{chunks[index]}",
                cancellationToken).ConfigureAwait(false);
        await Require(session, SshOperation.FinalizeWindowsProvisionScript, null, cancellationToken).ConfigureAwait(false);
        await Require(session, SshOperation.StartWindowsProvision, null, cancellationToken).ConfigureAwait(false);

        DateTimeOffset deadline = DateTimeOffset.UtcNow + _provisionTimeout;
        while (true)
        {
            await _delay(_pollInterval, cancellationToken).ConfigureAwait(false);
            SshCommandResult status = await session.ExecuteAsync(SshOperation.ReadWindowsProvisionStatus,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (status.Success && TryParseStatus(status.Output, out WindowsProvisionStatus parsed))
            {
                progress?.Report(new(parsed.Stage == "ready" ? AutoSetupStage.StartOllama : AutoSetupStage.InstallOllama,
                    DescribeStage(parsed.Stage), parsed.Percent));
                if (parsed.Done)
                    return parsed.Ok ? null : new(false, DescribeError(parsed.Error), null);
            }
            if (DateTimeOffset.UtcNow > deadline)
                return new(false, "Quá thời gian chờ cài Ollama trên máy Windows (45 phút).", null);
        }
    }

    private static async Task Require(ISshSession session, SshOperation operation, string? argument,
        CancellationToken cancellationToken)
    {
        SshCommandResult result = await session.ExecuteAsync(operation, argument, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            throw new InvalidOperationException("Máy Windows từ chối bước chuẩn bị: " + result.Error.Trim());
    }

    internal sealed record WindowsProvisionStatus(string Stage, double Percent, bool Done, bool Ok, string Error,
        string Warning);

    internal static bool TryParseStatus(string json, out WindowsProvisionStatus status)
    {
        status = new("", 0, false, false, "", "");
        try
        {
            using JsonDocument document = JsonDocument.Parse(json.Trim());
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            status = new(Text(root, "stage"), root.TryGetProperty("percent", out JsonElement percent) &&
                                              percent.TryGetDouble(out double value) ? value : 0,
                root.TryGetProperty("done", out JsonElement done) && done.ValueKind == JsonValueKind.True,
                root.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True,
                Text(root, "error"), Text(root, "warning"));
            return status.Stage.Length > 0;
        }
        catch (JsonException) { return false; }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";

    internal static string DescribeStage(string stage) => stage switch
    {
        "check" => "Đang kiểm tra máy Windows…",
        "download" => "Đang tải Ollama về máy thuê…",
        "extract" => "Đang giải nén Ollama…",
        "service" => "Đang đăng ký dịch vụ Ollama tự khởi động…",
        "wait" => "Đang chờ Ollama phản hồi…",
        "ready" => "Ollama đã chạy.",
        _ => "Đang cài đặt trên máy Windows…"
    };

    internal static string DescribeError(string code) => code switch
    {
        "NOT_ADMIN" => "Tài khoản SSH không có quyền Administrator nên không cài được dịch vụ Ollama.",
        "DOWNLOAD_FAILED" => "Máy thuê không tải được Ollama từ GitHub (mạng bị chặn hoặc quá chậm). Hãy thử lại sau.",
        "EXTRACT_FAILED" => "Giải nén Ollama thất bại.",
        "OLLAMA_NOT_READY" => "Ollama đã cài nhưng không phản hồi sau 2 phút. Xem log C:\\ProgramData\\QTStudio\\logs\\ollama.log trên máy thuê.",
        _ => "Cài Ollama trên Windows thất bại: " + code
    };

    private sealed class InlineProgress(Action<OllamaModelPullProgress> report) : IProgress<OllamaModelPullProgress>
    {
        public void Report(OllamaModelPullProgress value) => report(value);
    }
}
