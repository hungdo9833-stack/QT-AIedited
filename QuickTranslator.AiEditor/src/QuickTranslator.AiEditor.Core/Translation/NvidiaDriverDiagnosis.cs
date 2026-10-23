using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum NvidiaDriverProblem { Unknown, NoNvidiaDevice, DriverNotInstalled, KernelModuleMissing, ModuleNotLoaded, LoadedButUnreachable }

public sealed record NvidiaDriverDiagnosis(NvidiaDriverProblem Problem, string Message, string? FixCommand);

/// <summary>
/// Explains why nvidia-smi fails on a rented Ubuntu GPU from <see cref="RemoteLlamaLinuxScripts.DiagnoseDriver"/>
/// output. The common rental case: the box booted a newer kernel than its prebuilt linux-modules-nvidia package, so the
/// card is present but no driver module exists for the running kernel. Pure; performs no I/O.
/// </summary>
public static class NvidiaDriverDiagnoser
{
    private const string Reboot = "rồi khởi động lại máy thuê";
    // linux-modules-nvidia-580-open-7.0.0-28-generic → series "580-open", kernel "7.0.0-28-generic".
    private static readonly Regex ModulePackage = new(@"^linux-modules-nvidia-(?<series>\d+(?:-server)?(?:-open)?)-(?<kernel>\d+\.\d+\.\d+-\d+-[a-z0-9]+)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex DriverPackage = new(@"^nvidia-driver-(?<series>\d+(?:-server)?(?:-open)?)$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static NvidiaDriverDiagnosis Diagnose(string output)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var packages = new List<string>();
        foreach (string line in (output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int equals = line.IndexOf('=');
            if (equals <= 0) continue;
            string key = line[..equals], value = line[(equals + 1)..].Trim();
            if (key == "package") packages.Add(value);
            else values[key] = value;
        }
        string kernel = values.GetValueOrDefault("kernel", "");
        if (kernel.Length == 0 || !int.TryParse(values.GetValueOrDefault("pci"), out int devices))
            return new(NvidiaDriverProblem.Unknown, "Máy Linux chưa có driver NVIDIA (nvidia-smi không chạy được).", null);
        if (devices == 0)
            return new(NvidiaDriverProblem.NoNvidiaDevice,
                "Máy thuê không có GPU NVIDIA (không thấy thiết bị NVIDIA trên PCI). Hãy chọn gói thuê có GPU NVIDIA.", null);

        bool loaded = values.GetValueOrDefault("loaded") == "1";
        string module = values.GetValueOrDefault("module", "");
        if (loaded)
            return new(NvidiaDriverProblem.LoadedButUnreachable,
                "Driver NVIDIA đã nạp nhưng nvidia-smi không giao tiếp được (thường do vừa nâng cấp driver). Khởi động lại máy thuê.", null);
        if (module.Length > 0)
            return new(NvidiaDriverProblem.ModuleNotLoaded,
                $"Driver NVIDIA {module} đã cài cho kernel {kernel} nhưng chưa được nạp. Chạy lệnh dưới {Reboot}.", "sudo modprobe nvidia && nvidia-smi");

        var modules = packages.Select(p => ModulePackage.Match(p)).Where(m => m.Success).ToArray();
        string? series = modules.Select(m => m.Groups["series"].Value).FirstOrDefault()
            ?? packages.Select(p => DriverPackage.Match(p)).Where(m => m.Success).Select(m => m.Groups["series"].Value).FirstOrDefault();
        if (series is null && !packages.Any(p => p.StartsWith("nvidia-dkms-", StringComparison.Ordinal)))
            return new(NvidiaDriverProblem.DriverNotInstalled,
                $"Máy thuê có GPU NVIDIA nhưng chưa cài driver. Chạy lệnh dưới {Reboot}.",
                "sudo apt-get update && sudo ubuntu-drivers install");

        string[] otherKernels = modules.Select(m => m.Groups["kernel"].Value).Where(k => k != kernel).Distinct().ToArray();
        string builtFor = otherKernels.Length == 0 ? "" : $" (driver chỉ có module cho kernel {string.Join(", ", otherKernels)})";
        // Prebuilt Ubuntu module for the running kernel when the driver came that way; otherwise DKMS builds one.
        string fix = modules.Length > 0
            ? $"sudo apt-get update && sudo apt-get install -y linux-modules-nvidia-{series}-{kernel}"
            : $"sudo apt-get update && sudo apt-get install -y nvidia-dkms-{series ?? "580-open"}";
        return new(NvidiaDriverProblem.KernelModuleMissing,
            $"Kernel đang chạy {kernel} chưa có module driver NVIDIA{builtFor}, thường do máy tự nâng cấp kernel. Chạy lệnh dưới {Reboot}.", fix);
    }
}
