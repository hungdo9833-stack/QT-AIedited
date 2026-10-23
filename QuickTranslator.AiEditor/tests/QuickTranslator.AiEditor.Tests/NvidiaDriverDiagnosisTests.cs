using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class NvidiaDriverDiagnosisTests
{
    // Facts from a real rental (Ubuntu 24.04, RTX 5060 Ti): the kernel moved to 7.0.0-38 but the prebuilt
    // module package only existed for 7.0.0-28, so nvidia-smi could not reach a driver.
    private const string KernelMismatch = """
        kernel=7.0.0-38-generic
        pci=2
        dev=0
        loaded=0
        module=
        package=linux-modules-nvidia-580-open-7.0.0-28-generic
        package=linux-modules-nvidia-580-open-generic-hwe-24.04
        package=nvidia-driver-580-open
        """;

    [Fact]
    public void Newer_kernel_without_prebuilt_module_names_the_exact_package_to_install()
    {
        NvidiaDriverDiagnosis d = NvidiaDriverDiagnoser.Diagnose(KernelMismatch);
        Assert.Equal(NvidiaDriverProblem.KernelModuleMissing, d.Problem);
        Assert.Contains("7.0.0-38-generic", d.Message);
        Assert.Contains("7.0.0-28-generic", d.Message);
        Assert.Equal("sudo apt-get update && sudo apt-get install -y linux-modules-nvidia-580-open-7.0.0-38-generic", d.FixCommand);
    }

    [Fact]
    public void Dkms_driver_without_module_for_the_kernel_suggests_rebuilding_with_dkms()
    {
        NvidiaDriverDiagnosis d = NvidiaDriverDiagnoser.Diagnose("kernel=6.8.0-90-generic\npci=1\nloaded=0\nmodule=\npackage=nvidia-driver-550\n");
        Assert.Equal(NvidiaDriverProblem.KernelModuleMissing, d.Problem);
        Assert.Equal("sudo apt-get update && sudo apt-get install -y nvidia-dkms-550", d.FixCommand);
    }

    [Theory]
    [InlineData("kernel=6.8.0-90-generic\npci=0\nloaded=0\nmodule=\n", NvidiaDriverProblem.NoNvidiaDevice)]
    [InlineData("kernel=6.8.0-90-generic\npci=1\nloaded=0\nmodule=\n", NvidiaDriverProblem.DriverNotInstalled)]
    [InlineData("kernel=6.8.0-90-generic\npci=1\nloaded=0\nmodule=580.178.04\npackage=nvidia-driver-580-open\n", NvidiaDriverProblem.ModuleNotLoaded)]
    [InlineData("kernel=6.8.0-90-generic\npci=1\nloaded=1\nmodule=580.178.04\npackage=nvidia-driver-580-open\n", NvidiaDriverProblem.LoadedButUnreachable)]
    [InlineData("ok", NvidiaDriverProblem.Unknown)]
    [InlineData("", NvidiaDriverProblem.Unknown)]
    public void Each_failure_mode_is_classified(string facts, NvidiaDriverProblem expected)
    {
        NvidiaDriverDiagnosis d = NvidiaDriverDiagnoser.Diagnose(facts);
        Assert.Equal(expected, d.Problem);
        Assert.Contains("NVIDIA", d.Message);
    }

    [Fact]
    public async Task Probe_explains_kernel_mismatch_and_only_diagnoses_when_nvidia_smi_fails()
    {
        var broken = new RemoteLlamaSetupTests.FakeSession(true)
            { IsWindows = false, LinuxGpuExitCode = 9, LinuxGpuOutput = "", LinuxDriverDiagnosis = KernelMismatch };
        RemoteLlamaInspection result = await new RemoteGpuProbe().InspectAsync(broken, 11434);
        Assert.False(result.SupportsAutomaticSetup);
        Assert.Contains("linux-modules-nvidia-580-open-7.0.0-38-generic", result.BlockedReason!);

        var healthy = new RemoteLlamaSetupTests.FakeSession(true) { IsWindows = false };
        await new RemoteGpuProbe().InspectAsync(healthy, 11434);
        Assert.DoesNotContain(healthy.Commands, c => c.Operation == SshOperation.DiagnoseNvidiaDriverLinux);
    }
}
