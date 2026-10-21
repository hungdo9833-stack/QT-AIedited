using System.IO.Compression;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// For a rented Windows machine whose provider forwards only the Remote Desktop port. One pasted command turns on
/// OpenSSH on loopback and runs a tiny splitter on the public RDP port: a connection that starts with "S" (the
/// "SSH-" banner) goes to sshd, everything else goes to Remote Desktop, which is moved to another port. The switch
/// runs as a detached SYSTEM task, checks both routes through the public port and rolls the RDP port back on any
/// failure, so a broken setup does not lock the machine out.
/// </summary>
public static class WindowsPortMux
{
    public const int LoopbackSshPort = 22222;

    /// <summary>C# 5 source (compiled on the rented machine with Windows PowerShell's Add-Type, no SDK needed).</summary>
    public const string MuxSource = """
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;

public static class QtMux
{
    public static int Main(string[] args)
    {
        int listenPort = int.Parse(args[0]);
        int rdpPort = int.Parse(args[1]);
        int sshPort = int.Parse(args[2]);
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.IPv6Any, listenPort);
            listener.Server.DualMode = true;
            listener.Start(128);
        }
        catch (SocketException)
        {
            listener = new TcpListener(IPAddress.Any, listenPort);
            listener.Start(128);
        }
        while (true)
        {
            TcpClient client = listener.AcceptTcpClient();
            ThreadPool.QueueUserWorkItem(delegate { Route(client, rdpPort, sshPort); });
        }
    }

    private static void Route(TcpClient client, int rdpPort, int sshPort)
    {
        TcpClient target = null;
        try
        {
            client.NoDelay = true;
            NetworkStream from = client.GetStream();
            byte[] first = new byte[1024];
            client.ReceiveTimeout = 5000;
            int count = from.Read(first, 0, first.Length);
            if (count <= 0) return;
            client.ReceiveTimeout = 0;
            target = new TcpClient();
            target.NoDelay = true;
            target.Connect(IPAddress.Loopback, first[0] == (byte)'S' ? sshPort : rdpPort);
            NetworkStream to = target.GetStream();
            to.Write(first, 0, count);
            TcpClient a = client;
            TcpClient b = target;
            Thread back = new Thread(delegate() { Pump(to, from, a, b); });
            back.IsBackground = true;
            back.Start();
            Pump(from, to, a, b);
        }
        catch (Exception) { }
        finally { Close(client); Close(target); }
    }

    private static void Pump(NetworkStream input, NetworkStream output, TcpClient a, TcpClient b)
    {
        byte[] buffer = new byte[16384];
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) output.Write(buffer, 0, read);
        }
        catch (Exception) { }
        Close(a);
        Close(b);
    }

    private static void Close(TcpClient client)
    {
        if (client == null) return;
        try { client.Close(); } catch (Exception) { }
    }
}
""";

    private const string SwitchScript = """
param([int]$Public, [int]$Rdp, [int]$Ssh)
$ErrorActionPreference = 'Stop'
$root = 'C:\ProgramData\QTStudio'
$key = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp'
$exe = "$root\mux\qt-mux.exe"
$log = "$root\logs\mux-switch.log"
function Log($text) { Add-Content -Path $log -Value ((Get-Date -Format s) + ' ' + $text) }
function Probe($port, [byte[]]$send) {
  $c = New-Object Net.Sockets.TcpClient
  try {
    $c.Connect('127.0.0.1', $port)
    $s = $c.GetStream(); $s.ReadTimeout = 5000
    $s.Write($send, 0, $send.Length)
    $b = New-Object byte[] 64
    $n = $s.Read($b, 0, 64)
    if ($n -le 0) { return ,@() }
    return ,$b[0..($n - 1)]
  } catch { return ,@() } finally { $c.Close() }
}
function IsSsh($r) { return ($r.Count -ge 4 -and [Text.Encoding]::ASCII.GetString([byte[]]$r, 0, 4) -eq 'SSH-') }
function IsRdp($r) { return ($r.Count -ge 2 -and $r[0] -eq 3 -and $r[1] -eq 0) }
$sshHello = [Text.Encoding]::ASCII.GetBytes("SSH-2.0-QTStudio`r`n")
$rdpHello = [byte[]](0x03,0x00,0x00,0x13,0x0E,0xE0,0x00,0x00,0x00,0x00,0x00,0x01,0x00,0x08,0x00,0x03,0x00,0x00,0x00)
try {
  Log "switch public=$Public rdp=$Rdp ssh=$Ssh"
  if ([int](Get-ItemProperty $key).PortNumber -ne $Rdp) {
    Set-ItemProperty -Path $key -Name PortNumber -Value $Rdp
    Restart-Service -Name TermService -Force
    Start-Sleep -Seconds 4
  }
  $action = New-ScheduledTaskAction -Execute $exe -Argument "$Public $Rdp $Ssh"
  $trigger = New-ScheduledTaskTrigger -AtStartup
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 99 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName 'QTStudio Mux' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
  Start-ScheduledTask -TaskName 'QTStudio Mux'
  Start-Sleep -Seconds 4
  if (-not (IsSsh (Probe $Public $sshHello))) { throw 'SSH does not answer through the public port.' }
  if (-not (IsRdp (Probe $Public $rdpHello))) { throw 'Remote Desktop does not answer through the public port.' }
  @{ publicPort = $Public; rdpPort = $Rdp; sshPort = $Ssh } | ConvertTo-Json | Set-Content -Path "$root\mux\state.json" -Encoding ascii
  Log 'ok'
} catch {
  Log ('failed, rolling back: ' + $_.Exception.Message)
  try { Unregister-ScheduledTask -TaskName 'QTStudio Mux' -Confirm:$false -ErrorAction SilentlyContinue } catch { }
  Get-Process -Name qt-mux -ErrorAction SilentlyContinue | Stop-Process -Force
  Set-ItemProperty -Path $key -Name PortNumber -Value $Public
  Restart-Service -Name TermService -Force
  Log 'rolled back'
}
""";

    private const string BootstrapHead = """
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = 'C:\ProgramData\QTStudio'
$sshPort = 22222
$rdpKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp'
function Step($text) { Write-Host ('[QT Studio] ' + $text) }
function Probe($port, [byte[]]$send) {
  $c = New-Object Net.Sockets.TcpClient
  try {
    $c.Connect('127.0.0.1', $port)
    $s = $c.GetStream(); $s.ReadTimeout = 5000
    $s.Write($send, 0, $send.Length)
    $b = New-Object byte[] 64
    $n = $s.Read($b, 0, 64)
    if ($n -le 0) { return ,@() }
    return ,$b[0..($n - 1)]
  } catch { return ,@() } finally { $c.Close() }
}
function IsSsh($r) { return ($r.Count -ge 4 -and [Text.Encoding]::ASCII.GetString([byte[]]$r, 0, 4) -eq 'SSH-') }
function IsRdp($r) { return ($r.Count -ge 2 -and $r[0] -eq 3 -and $r[1] -eq 0) }
$sshHello = [Text.Encoding]::ASCII.GetBytes("SSH-2.0-QTStudio`r`n")
$rdpHello = [byte[]](0x03,0x00,0x00,0x13,0x0E,0xE0,0x00,0x00,0x00,0x00,0x00,0x01,0x00,0x08,0x00,0x03,0x00,0x00,0x00)
try {
  $principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
  if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Hay mo PowerShell bang Run as administrator roi dan lai lenh.'
  }
  New-Item -ItemType Directory -Force "$root\mux", "$root\logs" | Out-Null
  $statePath = "$root\mux\state.json"
  $rdpNow = [int](Get-ItemProperty $rdpKey).PortNumber
  $public = $rdpNow
  $rdpPort = $rdpNow
  if (Test-Path $statePath) {
    $state = Get-Content -Raw $statePath | ConvertFrom-Json
    if ($rdpNow -ne [int]$state.publicPort) { $public = [int]$state.publicPort }
  }
  if ($public -ne $rdpNow -and (IsSsh (Probe $public $sshHello))) {
    Step "Da san sang: SSH va Remote Desktop cung dung cong $public."
    return
  }

  Step 'Cai OpenSSH Server...'
  if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    try { Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null } catch { }
  }
  if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) {
    $zip = "$root\OpenSSH-Win64.zip"
    & curl.exe -L --fail --retry 3 -o $zip 'https://github.com/PowerShell/Win32-OpenSSH/releases/latest/download/OpenSSH-Win64.zip'
    Expand-Archive -Force $zip $env:ProgramFiles
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$env:ProgramFiles\OpenSSH-Win64\install-sshd.ps1"
  }
  if (-not (Get-Service sshd -ErrorAction SilentlyContinue)) { throw 'Khong cai duoc OpenSSH Server.' }
  Set-Service sshd -StartupType Automatic
  $config = "$env:ProgramData\ssh\sshd_config"
  if (-not (Test-Path $config)) { Start-Service sshd; Start-Sleep -Seconds 2; Stop-Service sshd -Force }
  $kept = @(Get-Content $config | Where-Object { $_ -notmatch '^\s*(Port|ListenAddress)\s' })
  $header = @('# QT Studio: sshd only listens on loopback; qt-mux forwards SSH here.', "Port $sshPort", 'ListenAddress 127.0.0.1')
  Set-Content -Path $config -Value ($header + $kept) -Encoding ascii
  Restart-Service sshd
  Start-Sleep -Seconds 2
  if (-not (IsSsh (Probe $sshPort $sshHello))) { throw 'sshd khong tra loi tren loopback.' }

  Step 'Tao bo chia cong...'
  $exe = "$root\mux\qt-mux.exe"
  Get-Process -Name qt-mux -ErrorAction SilentlyContinue | Stop-Process -Force
  Remove-Item $exe -Force -ErrorAction SilentlyContinue
  $source = @'

""";

    private const string BootstrapMiddle = """

'@
  Add-Type -TypeDefinition $source -OutputAssembly $exe -OutputType ConsoleApplication
  $testPort = 33998
  $test = Start-Process -FilePath $exe -ArgumentList @($testPort, $rdpPort, $sshPort) -WindowStyle Hidden -PassThru
  try {
    Start-Sleep -Seconds 2
    if (-not (IsSsh (Probe $testPort $sshHello))) { throw 'Bo chia cong khong chuyen duoc SSH.' }
    if (-not (IsRdp (Probe $testPort $rdpHello))) { throw 'Bo chia cong khong chuyen duoc Remote Desktop.' }
  } finally { Stop-Process -Id $test.Id -Force -ErrorAction SilentlyContinue }

  Remove-NetFirewallRule -Name 'QTStudio-Mux' -ErrorAction SilentlyContinue
  New-NetFirewallRule -Name 'QTStudio-Mux' -DisplayName 'QT Studio port mux' -Direction Inbound -Protocol TCP -LocalPort $public -Action Allow | Out-Null
  if ($rdpNow -eq $public) {
    $busy = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | ForEach-Object { $_.LocalPort })
    $rdpPort = 33890
    while ($busy -contains $rdpPort) { $rdpPort++ }
  }
  $switch = @'

""";

    private const string BootstrapTail = """

'@
  Set-Content -Path "$root\mux\switch.ps1" -Value $switch -Encoding ascii
  $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ("-NoProfile -ExecutionPolicy Bypass -File `"$root\mux\switch.ps1`" -Public $public -Rdp $rdpPort -Ssh $sshPort")
  $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
  Register-ScheduledTask -TaskName 'QTStudio Mux Switch' -Action $action -Principal $principal -Force | Out-Null
  Step "Dang chuyen cong ${public}: Remote Desktop se ngat vai giay, QT Studio tu nhan ra SSH. Ket noi lai RDP neu can."
  Start-ScheduledTask -TaskName 'QTStudio Mux Switch'
} catch {
  Write-Host ('[QT Studio] Loi: ' + $_.Exception.Message) -ForegroundColor Red
}
""";

    public static string BootstrapScript { get; } =
        BootstrapHead + MuxSource + BootstrapMiddle + SwitchScript + BootstrapTail;

    /// <summary>
    /// One line to paste into an administrator PowerShell on the rented machine. The script travels gzip-compressed
    /// so it stays well below the console command-line limit.
    /// </summary>
    public static string BuildBootstrapCommand()
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(BootstrapScript));
        string loader = "$b=[Convert]::FromBase64String('" + Convert.ToBase64String(buffer.ToArray()) + "');" +
            "$g=New-Object IO.Compression.GZipStream((New-Object IO.MemoryStream(,$b)),[IO.Compression.CompressionMode]::Decompress);" +
            "Invoke-Expression (New-Object IO.StreamReader($g,[Text.Encoding]::UTF8)).ReadToEnd()";
        return WindowsRemoteScripts.Encode(loader);
    }
}
