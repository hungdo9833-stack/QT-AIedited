using System.Text;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// Fixed PowerShell scripts for a rented GPU machine that runs Windows. Every script is a constant owned by QT Studio;
/// only the Ollama model name and the base64 chunks of <see cref="ProvisionScript"/> are ever inserted into a command.
/// All remote text is ASCII so it survives any console code page; the app translates status codes to Vietnamese.
/// </summary>
public static class WindowsRemoteScripts
{
    public const string RemoteRoot = @"C:\ProgramData\QTStudio";
    public const int ProvisionChunkBytes = 1500; // Multiple of 3, so concatenated chunk base64 stays valid.

    /// <summary>Wraps a script so it runs the same under a cmd.exe or a PowerShell SSH default shell.</summary>
    public static string Encode(string script)
    {
        // Silencing progress keeps PowerShell from writing CLIXML progress records into the output stream.
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';" + script));
        return "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
    }

    public const string ReadInfo =
        "$o=Get-CimInstance Win32_OperatingSystem; \"$($o.Caption) $($o.Version)\"";

    public const string ReadGpu =
        "$e=@(\"$env:SystemRoot\\System32\\nvidia-smi.exe\",\"$env:ProgramFiles\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe\")" +
        "|Where-Object{Test-Path $_}|Select-Object -First 1;" +
        "if($e){& $e --query-gpu=name,memory.total --format=csv,noheader,nounits}" +
        "else{'NONVIDIA:'+((Get-CimInstance Win32_VideoController).Name -join '; ')}";

    public const string ReadFreeDisk =
        "(Get-PSDrive -Name $env:SystemDrive.TrimEnd(':')).Free";

    public const string FindOllama =
        "$c=@((Get-Command ollama.exe -ErrorAction SilentlyContinue).Source)" +
        "+@(Get-ChildItem 'C:\\Users\\*\\AppData\\Local\\Programs\\Ollama\\ollama.exe' -ErrorAction SilentlyContinue|ForEach-Object{$_.FullName})" +
        "+@(\"$env:ProgramData\\QTStudio\\ollama\\ollama.exe\");" +
        "$f=$c|Where-Object{$_ -and (Test-Path $_)}|Select-Object -First 1; if($f){$f}else{exit 1}";

    public const string ReadOllamaService =
        "try{$null=Invoke-RestMethod -TimeoutSec 3 'http://127.0.0.1:11434/api/version';'active'}catch{'inactive';exit 3}";

    public const string ListModels =
        "try{'NAME';(Invoke-RestMethod -TimeoutSec 5 'http://127.0.0.1:11434/api/tags').models|ForEach-Object{$_.name}}catch{exit 1}";

    public const string StartProvision =
        "$d='C:\\ProgramData\\QTStudio';Remove-Item \"$d\\status.json\" -Force -ErrorAction SilentlyContinue;" +
        "$a=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -ExecutionPolicy Bypass -File C:\\ProgramData\\QTStudio\\provision.ps1';" +
        "$p=New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest;" +
        "Register-ScheduledTask -TaskName 'QTStudio Provision' -Action $a -Principal $p -Force|Out-Null;" +
        "Start-ScheduledTask -TaskName 'QTStudio Provision'";

    public const string ReadProvisionStatus =
        "$s='C:\\ProgramData\\QTStudio\\status.json';if(Test-Path $s){Get-Content -Raw $s}else{exit 1}";

    public const string FinalizeProvisionScript =
        "$d='C:\\ProgramData\\QTStudio';" +
        "[IO.File]::WriteAllBytes(\"$d\\provision.ps1\",[Convert]::FromBase64String(([IO.File]::ReadAllText(\"$d\\provision.b64\")).Trim()));" +
        "Remove-Item \"$d\\provision.b64\" -Force";

    /// <summary>One piece of the uploaded provisioning script. <paramref name="index"/> 0 starts the file again.</summary>
    public static string WriteProvisionChunk(int index, string base64Chunk) =>
        "$d='C:\\ProgramData\\QTStudio';New-Item -ItemType Directory -Force $d|Out-Null;$p=\"$d\\provision.b64\";" +
        $"$c='{base64Chunk}';" +
        (index == 0
            ? "Set-Content -Path $p -Value $c -NoNewline -Encoding ascii"
            : "Add-Content -Path $p -Value $c -NoNewline -Encoding ascii");

    public static IReadOnlyList<string> SplitProvisionScript(string script = ProvisionScript)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(script);
        var chunks = new List<string>();
        for (int offset = 0; offset < bytes.Length; offset += ProvisionChunkBytes)
            chunks.Add(Convert.ToBase64String(bytes, offset, Math.Min(ProvisionChunkBytes, bytes.Length - offset)));
        return chunks;
    }

    /// <summary>
    /// Installs the portable Ollama build as a machine-wide start-up task (loopback only, so no firewall rule is
    /// opened) and writes progress to status.json. It is meant to run as SYSTEM through a scheduled task so it
    /// survives the SSH session closing. It never touches NVIDIA drivers.
    /// </summary>
    public const string ProvisionScript = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = 'C:\ProgramData\QTStudio'
$statusPath = "$root\status.json"
$port = 11434
$warning = ''
function Save($stage, $pct, $msg, $done = $false, $ok = $false, $err = '') {
  $clean = ([string]$err) -replace '[^\x20-\x7E]', '?'
  $o = [ordered]@{ stage = $stage; percent = $pct; message = $msg; done = $done; ok = $ok; error = $clean; warning = $script:warning }
  $tmp = "$statusPath.tmp"
  ($o | ConvertTo-Json -Compress) | Set-Content -Path $tmp -Encoding ascii
  Move-Item -Force $tmp $statusPath
}
function Up {
  try { return (Invoke-WebRequest -UseBasicParsing -TimeoutSec 3 "http://127.0.0.1:$port/api/version").StatusCode -eq 200 } catch { return $false }
}
try {
  New-Item -ItemType Directory -Force "$root\models", "$root\dl", "$root\logs" | Out-Null
  Save 'check' 2 'check'
  $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if (-not $admin) { throw 'NOT_ADMIN' }
  $smi = Join-Path $env:SystemRoot 'System32\nvidia-smi.exe'
  if (-not (Test-Path $smi)) { $script:warning = 'NO_NVIDIA_DRIVER' }
  if (Up) { Save 'ready' 100 'already' $true $true; return }

  $exe = (Get-Command ollama.exe -ErrorAction SilentlyContinue).Source
  if (-not $exe) { $exe = Get-ChildItem 'C:\Users\*\AppData\Local\Programs\Ollama\ollama.exe' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName }
  if (-not $exe -and (Test-Path "$root\ollama\ollama.exe")) { $exe = "$root\ollama\ollama.exe" }

  if (-not $exe) {
    $zip = "$root\dl\ollama-windows-amd64.zip"
    $url = 'https://github.com/ollama/ollama/releases/latest/download/ollama-windows-amd64.zip'
    $total = 0
    try {
      $heads = & curl.exe -sIL $url 2>$null | Where-Object { $_ -match '^content-length:\s*(\d+)' }
      if ($heads) { $total = [int64](($heads | Select-Object -Last 1) -replace '[^\d]', '') }
    } catch { }
    Save 'download' 5 'download'
    $curl = Start-Process -FilePath 'curl.exe' -ArgumentList @('-L', '--fail', '--retry', '5', '--retry-delay', '3', '-C', '-', '-o', "`"$zip`"", "`"$url`"") -WindowStyle Hidden -PassThru
    $null = $curl.Handle
    while (-not $curl.HasExited) {
      Start-Sleep -Seconds 2
      $size = if (Test-Path $zip) { (Get-Item $zip).Length } else { 0 }
      $pct = if ($total -gt 0) { [int](5 + 55 * [math]::Min(1, $size / $total)) } else { 20 }
      Save 'download' $pct 'download'
    }
    if ($curl.ExitCode -ne 0 -or -not (Test-Path $zip) -or (Get-Item $zip).Length -lt 50MB) { throw 'DOWNLOAD_FAILED' }
    Save 'extract' 62 'extract'
    if (Test-Path "$root\ollama") { Remove-Item -Recurse -Force "$root\ollama" }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, "$root\ollama")
    $exe = "$root\ollama\ollama.exe"
    if (-not (Test-Path $exe)) { throw 'EXTRACT_FAILED' }
    Remove-Item -Force $zip -ErrorAction SilentlyContinue
  }

  Save 'service' 80 'service'
  $cmdLine = '/c set "OLLAMA_HOST=127.0.0.1:' + $port + '" && set "OLLAMA_MODELS=' + $root + '\models" && "' + $exe + '" serve >> "' + $root + '\logs\ollama.log" 2>&1'
  $action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument $cmdLine
  $trigger = New-ScheduledTaskTrigger -AtStartup
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName 'QTStudio Ollama' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
  Start-ScheduledTask -TaskName 'QTStudio Ollama'

  Save 'wait' 90 'wait'
  $deadline = (Get-Date).AddSeconds(120)
  while (-not (Up)) {
    if ((Get-Date) -gt $deadline) { throw 'OLLAMA_NOT_READY' }
    Start-Sleep -Seconds 2
  }
  Save 'ready' 100 'ready' $true $true
} catch {
  Save 'error' 0 'error' $true $false $_.Exception.Message
}
""";
}
