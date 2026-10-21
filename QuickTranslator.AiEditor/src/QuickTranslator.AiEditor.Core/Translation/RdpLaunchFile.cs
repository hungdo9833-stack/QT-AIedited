using System.Globalization;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>Accepts "host", "host:port" and "[ipv6]:port" as typed or pasted from a provider's panel.</summary>
public static class RemoteAddress
{
    public static (string Host, int? Port) Split(string text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            if (close > 0)
            {
                string rest = text[(close + 1)..];
                return (text[1..close], rest.StartsWith(':') && TryPort(rest[1..], out int v6Port) ? v6Port : null);
            }
        }
        int colon = text.LastIndexOf(':');
        // A single colon is host:port; several colons without brackets are a bare IPv6 address.
        if (colon > 0 && text.IndexOf(':') == colon && TryPort(text[(colon + 1)..], out int port))
            return (text[..colon].Trim(), port);
        return (text, null);
    }

    private static bool TryPort(string text, out int port) =>
        int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;
}

/// <summary>
/// A throw-away .rdp file that signs in to a rented Windows machine for the user and, instead of the desktop, starts
/// a tiny PowerShell that waits for the setup command QT Studio put on the (redirected) clipboard and runs it.
/// The saved password is a DPAPI blob that only this Windows user can decrypt; the file is deleted right after launch.
/// </summary>
public static class RdpLaunchFile
{
    // Fits the 256-character limit of the RDP "alternate shell" field with room to spare.
    public const string AlternateShell =
        "powershell.exe -NoProfile -Command \"for($i=0;$i -lt 150;$i++){$c=Get-Clipboard -Raw;" +
        "if($c -like 'powershell.exe*EncodedCommand*'){iex $c;break};sleep 1}\"";

    public static string Build(string host, int port, string username, string password)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '/' or '@') ||
            host.Length > 253 || port is < 1 or > 65535 || string.IsNullOrWhiteSpace(username) ||
            username.Any(char.IsControl) || string.IsNullOrEmpty(password) || password.Any(c => c is '\r' or '\n' or '\0'))
            throw new ArgumentException("Thông tin máy thuê không hợp lệ để mở Remote Desktop.");
        string address = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
        string blob = Convert.ToHexString(WindowsDpapiOllamaSecretProtector.ProtectBytes(Encoding.Unicode.GetBytes(password)));
        return string.Join("\r\n",
            "full address:s:" + address,
            "username:s:" + username.Trim(),
            "password 51:b:" + blob,
            "prompt for credentials:i:0",
            "promptcredentialonce:i:0",
            "authentication level:i:0", // Rented machines use self-signed certificates.
            "enablecredsspsupport:i:1",
            "negotiate security layer:i:1",
            "redirectclipboard:i:1",
            "redirectprinters:i:0",
            "redirectcomports:i:0",
            "redirectsmartcards:i:0",
            "drivestoredirect:s:",
            "audiomode:i:2",
            "autoreconnection enabled:i:1",
            "screen mode id:i:1",
            "desktopwidth:i:1024",
            "desktopheight:i:640",
            "session bpp:i:16",
            "disable wallpaper:i:1",
            "alternate shell:s:" + AlternateShell,
            "shell working directory:s:C:\\",
            "");
    }
}
