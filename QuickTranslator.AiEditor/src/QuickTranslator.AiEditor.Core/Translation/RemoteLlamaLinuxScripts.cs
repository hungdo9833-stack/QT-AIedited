using System.Text;
using System.Text.Json;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>The llama.cpp Linux CUDA build a rented Ubuntu GPU downloads itself (same pinned release as Windows).</summary>
public static class LlamaCppLinuxRelease
{
    public const string ServerArchive = "llama-" + LlamaCppRelease.Build + "-bin-ubuntu-cuda-12.8-x64.tar.gz";
    public const string ServerSha256 = "18f1ce31bfc6aefea98d4ab5816bb9278894f8dcbefcdd0f259b15dea7ca33ca";
    public const string CudaRuntimeArchive = "cudart-llama-" + LlamaCppRelease.Build + "-bin-ubuntu-cuda-12.8-x64.tar.gz";
    public const string CudaRuntimeSha256 = "07a2fc13cf76a8ddaa99ba348548bf0635529bc97762e033b014a09cf34aca51";
}

/// <summary>
/// POSIX sh commands for a rented Linux GPU. Everything lives under ~/.qtstudio, so no sudo is needed, and the rental
/// downloads the pinned llama.cpp release itself (a datacenter link beats uploading from this PC).
/// </summary>
public static class RemoteLlamaLinuxScripts
{
    private const string Vars = "R=\"$HOME/.qtstudio\";D=\"$R/llamacpp/" + LlamaCppRelease.Build + "\";";

    /// <summary>Runs a script through <c>sh</c> from base64, so the login shell never parses it (bash, dash or zsh alike).</summary>
    public static string Encode(string script) =>
        "printf '%s' '" + Convert.ToBase64String(Encoding.UTF8.GetBytes(script)) + "' | base64 -d | sh";

    public const string ReadArchitecture = "uname -m";
    public const string ReadGpu =
        "if command -v nvidia-smi >/dev/null 2>&1; then nvidia-smi --query-gpu=name,memory.total --format=csv,noheader,nounits; " +
        "else echo 'NONVIDIA:'; fi";
    /// <summary>
    /// Read-only facts for when nvidia-smi fails: running kernel, NVIDIA PCI devices (sysfs vendor 0x10de, no lspci
    /// needed), device node, loaded driver, a module for this kernel, and the installed driver/module packages.
    /// </summary>
    public const string DiagnoseDriver =
        "echo \"kernel=$(uname -r)\";" +
        "n=0;for v in /sys/bus/pci/devices/*/vendor;do [ \"$(cat \"$v\" 2>/dev/null)\" = 0x10de ] && n=$((n+1));done;echo \"pci=$n\";" +
        "if [ -e /dev/nvidia0 ];then echo dev=1;else echo dev=0;fi;" +
        "if [ -r /proc/driver/nvidia/version ];then echo loaded=1;else echo loaded=0;fi;" +
        "echo \"module=$(modinfo -F version nvidia 2>/dev/null | head -n 1)\";" +
        "dpkg-query -W -f='${db:Status-Abbrev} ${Package}\\n' 'nvidia-driver-*' 'linux-modules-nvidia-*' 'nvidia-dkms-*' 2>/dev/null" +
        " | awk '$1==\"ii\"{print \"package=\"$2}'";
    public const string ReadFreeDisk = "df -P -B1 \"$HOME\" | awk 'NR==2{print $4}'";
    public const string TestExecutable = Vars +
        "if [ -x \"$D/llama-server\" ] && LD_LIBRARY_PATH=\"$D\" \"$D/llama-server\" --version >/dev/null; then echo installed; else exit 1; fi";
    public const string ReadLogTail = Vars + "if [ -f \"$R/logs/llama-server.log\" ]; then tail -n 80 \"$R/logs/llama-server.log\"; else echo '(log empty)'; fi";
    public const string ReadCacheSize = Vars + "if [ -d \"$R/models/llama\" ]; then du -sb \"$R/models/llama\" | cut -f1; else echo 0; fi";

    public const string InstallPackage = "set -e;" + Vars + "T=\"$R/dl\";mkdir -p \"$T\" \"$D\";" +
        "a=$(uname -m);if [ \"$a\" != x86_64 ]; then echo \"UNSUPPORTED_ARCH $a\" >&2;exit 2;fi;" +
        "fetch(){ if command -v curl >/dev/null 2>&1; then curl -fsSL --retry 3 -C - -o \"$2\" \"$1\";" +
        "elif command -v wget >/dev/null 2>&1; then wget -q -c -O \"$2\" \"$1\";" +
        "else echo 'NO_CURL_OR_WGET' >&2;exit 3;fi; };" +
        "get(){ f=\"$T/$1\";if [ -f \"$f\" ] && echo \"$2  $f\" | sha256sum -c - >/dev/null 2>&1; then return 0;fi;" +
        "fetch \"https://github.com/ggml-org/llama.cpp/releases/download/" + LlamaCppRelease.Build + "/$1\" \"$f.partial\";" +
        "if ! echo \"$2  $f.partial\" | sha256sum -c - >/dev/null 2>&1; then rm -f \"$f.partial\";echo \"SHA256_MISMATCH $1\" >&2;exit 4;fi;" +
        "mv -f \"$f.partial\" \"$f\"; };" +
        "get " + LlamaCppLinuxRelease.ServerArchive + " " + LlamaCppLinuxRelease.ServerSha256 + ";" +
        "get " + LlamaCppLinuxRelease.CudaRuntimeArchive + " " + LlamaCppLinuxRelease.CudaRuntimeSha256 + ";" +
        "tar -xzf \"$T/" + LlamaCppLinuxRelease.ServerArchive + "\" -C \"$D\" --strip-components=1;" +
        "tar -xzf \"$T/" + LlamaCppLinuxRelease.CudaRuntimeArchive + "\" -C \"$D\" --strip-components=1;" +
        "if [ ! -x \"$D/llama-server\" ]; then echo LLAMA_SERVER_MISSING >&2;exit 5;fi;" +
        // The archives are ~770 MB and the model needs that disk; a reinstall downloads them again.
        "rm -f \"$T/" + LlamaCppLinuxRelease.ServerArchive + "\" \"$T/" + LlamaCppLinuxRelease.CudaRuntimeArchive + "\";echo installed";

    /// <summary>Stops QT Studio's llama-server (its whole process group), then starts it again detached with the new launch.</summary>
    public static string ConfigureServer(string? json)
    {
        RemoteLlamaTaskSpecification spec;
        try { spec = JsonSerializer.Deserialize<RemoteLlamaTaskSpecification>(json ?? "") ?? throw new JsonException(); }
        catch (JsonException e) { throw new ArgumentException("Cấu hình llama-server không hợp lệ.", nameof(json), e); }
        var launch = new LlamaServerLaunch(GgufModelReference.Parse(spec.Model), spec.Port, spec.Slots, spec.ContextPerSlot);
        string wrapper = "#!/bin/sh\n" + Vars + "\nexport LD_LIBRARY_PATH=\"$D${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}\"\n" +
            "export LLAMA_CACHE=\"$R/models/llama\"\nexec \"$D/llama-server\" " +
            string.Join(" ", launch.Arguments().Select(ShellQuote)) + "\n";
        string encodedWrapper = Convert.ToBase64String(Encoding.UTF8.GetBytes(wrapper));
        return "set -e;" + Vars + "mkdir -p \"$R/logs\" \"$R/models/llama\";" +
            "printf '%s' '" + encodedWrapper + "' | base64 -d > \"$R/llama-server.sh\";chmod +x \"$R/llama-server.sh\";" +
            // setsid makes the server a process-group leader, so the pid file names the group to stop next time.
            "if [ -f \"$R/llama-server.pid\" ]; then p=$(cat \"$R/llama-server.pid\");" +
            "if [ -n \"$p\" ] && kill -0 \"$p\" 2>/dev/null; then kill -TERM -- \"-$p\" 2>/dev/null || kill -TERM \"$p\" 2>/dev/null || true;" +
            "i=0;while kill -0 \"$p\" 2>/dev/null && [ $i -lt 15 ]; do sleep 1;i=$((i+1));done;" +
            "kill -KILL -- \"-$p\" 2>/dev/null || kill -KILL \"$p\" 2>/dev/null || true;fi;rm -f \"$R/llama-server.pid\";fi;" +
            "if command -v pkill >/dev/null 2>&1; then pkill -f \"$D/llama-server\" 2>/dev/null || true;fi;" +
            "cd \"$R\";echo \"=== $(date) QT Studio start\" >> \"$R/logs/llama-server.log\";" +
            "if command -v setsid >/dev/null 2>&1; then setsid nohup \"$R/llama-server.sh\" >> \"$R/logs/llama-server.log\" 2>&1 < /dev/null & " +
            "else nohup \"$R/llama-server.sh\" >> \"$R/logs/llama-server.log\" 2>&1 < /dev/null & fi;" +
            "p=$!;echo \"$p\" > \"$R/llama-server.pid\";" +
            // A taken port or a missing CUDA library ends the server within seconds; report it now, not after the health timeout.
            "sleep 3;if ! kill -0 \"$p\" 2>/dev/null; then echo 'LLAMA_SERVER_EXITED' >&2;tail -n 40 \"$R/logs/llama-server.log\" >&2;exit 1;fi;" +
            "echo started";
    }

    /// <summary>Single-quotes a word for sh: everything is literal, an embedded quote becomes '\''.</summary>
    internal static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
