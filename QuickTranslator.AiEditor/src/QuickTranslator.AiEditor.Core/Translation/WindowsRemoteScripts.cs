using System.Text;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>Windows probe and port-multiplexer commands shared by remote GPU setup.</summary>
public static class WindowsRemoteScripts
{
    public const string RemoteRoot = @"C:\ProgramData\QTStudio";

    public static string Encode(string script)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference='SilentlyContinue';" + script));
        return "powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
    }

    public const string ReadInfo = "$o=Get-CimInstance Win32_OperatingSystem; \"$($o.Caption) $($o.Version)\"";
    public const string ReadGpu =
        "$e=@(\"$env:SystemRoot\\System32\\nvidia-smi.exe\",\"$env:ProgramFiles\\NVIDIA Corporation\\NVSMI\\nvidia-smi.exe\")" +
        "|Where-Object{Test-Path $_}|Select-Object -First 1;" +
        "if($e){& $e --query-gpu=name,memory.total --format=csv,noheader,nounits}" +
        "else{'NONVIDIA:'+((Get-CimInstance Win32_VideoController).Name -join '; ')}";
    public const string ReadFreeDisk = "(Get-PSDrive -Name $env:SystemDrive.TrimEnd(':')).Free";
    public const string ConfirmMux =
        "Unregister-ScheduledTask -TaskName 'QTStudio Mux Revert' -Confirm:$false -ErrorAction SilentlyContinue;'confirmed'";
}
