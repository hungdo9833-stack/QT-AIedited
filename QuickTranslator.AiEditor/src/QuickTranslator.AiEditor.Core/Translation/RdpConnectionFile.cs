using System.Globalization;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// The few fields of a provider's .rdp file that QT Studio needs. A saved password blob (<c>password 51:b:</c>) is
/// never read: it is bound to the provider's machine and would be a secret we have no business handling.
/// </summary>
public sealed record RdpConnectionFile(string Host, int Port, string? Username, string? Domain, string? GatewayHost)
{
    public const int DefaultPort = 3389;

    public static RdpConnectionFile Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string? address = null, username = null, domain = null, gateway = null;
        int? port = null;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim().TrimStart('﻿');
            // key:type:value, where type is s (string), i (integer) or b (binary).
            int first = line.IndexOf(':');
            if (first < 1) continue;
            int second = line.IndexOf(':', first + 1);
            if (second < 0) continue;
            string key = line[..first].Trim().ToLowerInvariant();
            string value = line[(second + 1)..].Trim();
            switch (key)
            {
                case "full address": address = value; break;
                case "server port" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int explicitPort):
                    port = explicitPort; break;
                case "username": username = value; break;
                case "domain": domain = value; break;
                case "gatewayhostname": gateway = value; break;
            }
        }

        if (string.IsNullOrWhiteSpace(address))
            throw new InvalidDataException("File RDP không có dòng “full address”.");
        (string host, int? addressPort) = SplitAddress(address);
        int finalPort = addressPort ?? port ?? DefaultPort;
        if (host.Length == 0 || host.Length > 253 || host.Any(char.IsWhiteSpace) || host.Any(char.IsControl) ||
            host.Contains('/') || host.Contains('@') || finalPort is < 1 or > 65535)
            throw new InvalidDataException("Địa chỉ trong file RDP không hợp lệ.");
        return new(host, finalPort, NullIfEmpty(username), NullIfEmpty(domain), NullIfEmpty(gateway));
    }

    public static RdpConnectionFile Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>"user" as the provider wrote it, without a machine or domain prefix, for the SSH login field.</summary>
    public string? SshUsername
    {
        get
        {
            if (Username is null) return null;
            int slash = Username.LastIndexOf('\\');
            return slash >= 0 ? Username[(slash + 1)..] : Username;
        }
    }

    private static (string Host, int? Port) SplitAddress(string address)
    {
        address = address.Trim();
        if (address.StartsWith('['))
        {
            int close = address.IndexOf(']');
            if (close > 0)
            {
                string rest = address[(close + 1)..];
                return (address[1..close], rest.StartsWith(':') && int.TryParse(rest[1..], out int v6Port) ? v6Port : null);
            }
        }
        int colon = address.LastIndexOf(':');
        if (colon > 0 && address.IndexOf(':') == colon && int.TryParse(address[(colon + 1)..], out int port))
            return (address[..colon], port);
        return (address, null);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
