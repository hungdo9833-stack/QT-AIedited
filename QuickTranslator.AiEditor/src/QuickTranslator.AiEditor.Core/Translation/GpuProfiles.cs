using System.Text.Json.Serialization;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum SshAuthenticationKind { Password, PrivateKey }
public enum GpuConnectionState { Disconnected, Connecting, TrustRequired, Connected, Reconnecting, Offline, Blocked, Error }
public enum RemoteSetupAction { InstallOllamaUbuntu, StartOllamaService, PullModel }

public sealed class GpuSshSettings
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "";
    public SshAuthenticationKind Authentication { get; set; }
    public string? HostKeyFingerprint { get; set; }
    public int RemoteOllamaPort { get; set; } = 11434;
    [JsonIgnore] public string? Password { get; set; }
    [JsonIgnore] public string? PrivateKey { get; set; }
    [JsonIgnore] public string? PrivateKeyPassphrase { get; set; }

    public void Validate(bool requireTrustedHost = false)
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 || Host.Any(char.IsWhiteSpace) ||
            Host.Contains('/') || Host.Contains('@') || Host.Any(char.IsControl) || Port is < 1 or > 65535 ||
            string.IsNullOrWhiteSpace(Username) || Username.Length > 128 || Username.Any(char.IsControl) ||
            !Enum.IsDefined(Authentication) || RemoteOllamaPort is < 1 or > 65535)
            throw new InvalidDataException("Cấu hình SSH thiếu host, port hoặc username hợp lệ.");
        if (requireTrustedHost && string.IsNullOrWhiteSpace(HostKeyFingerprint))
            throw new InvalidOperationException("Chưa có SSH host-key fingerprint đã đối chiếu ngoài băng.");
        if (!string.IsNullOrWhiteSpace(HostKeyFingerprint) && !IsFingerprint(HostKeyFingerprint))
            throw new InvalidDataException("SSH fingerprint phải có dạng SHA256:<base64>.");
        if (Authentication == SshAuthenticationKind.Password && string.IsNullOrEmpty(Password))
            throw new InvalidOperationException("Profile SSH chưa có password.");
        if (Authentication == SshAuthenticationKind.PrivateKey && string.IsNullOrWhiteSpace(PrivateKey))
            throw new InvalidOperationException("Profile SSH chưa có private key.");
        if (Password?.Any(character => character is '\r' or '\n' or '\0') == true ||
            PrivateKeyPassphrase?.Any(character => character is '\r' or '\n' or '\0') == true ||
            Password?.Length > 8192 || PrivateKey?.Length > 128_000 || PrivateKeyPassphrase?.Length > 8192)
            throw new InvalidDataException("Credential SSH không hợp lệ.");
        Host = Host.Trim(); Username = Username.Trim();
        if (!string.IsNullOrWhiteSpace(HostKeyFingerprint))
            HostKeyFingerprint = NormalizeFingerprint(HostKeyFingerprint);
    }

    public GpuSshSettings Clone() => new()
    {
        Host = Host, Port = Port, Username = Username, Authentication = Authentication,
        HostKeyFingerprint = HostKeyFingerprint, RemoteOllamaPort = RemoteOllamaPort,
        Password = Password, PrivateKey = PrivateKey, PrivateKeyPassphrase = PrivateKeyPassphrase
    };

    public static string NormalizeFingerprint(string value)
    {
        value = value.Trim();
        return value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)
            ? "SHA256:" + value[7..].TrimEnd('=') : "SHA256:" + value.TrimEnd('=');
    }

    private static bool IsFingerprint(string value)
    {
        string normalized = NormalizeFingerprint(value);
        if (normalized.Length is < 20 or > 100) return false;
        return normalized[7..].All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '/' or '-' or '_');
    }
}

public sealed record GpuProfileStatus(Guid ProfileId, GpuConnectionState State, string Message,
    string? GpuName = null, long? VramBytes = null, Uri? TunnelEndpoint = null, DateTimeOffset? CheckedAt = null);

public sealed record RemoteGpuInspection(string OperatingSystem, string? GpuName, long? VramBytes,
    long? FreeDiskBytes, bool OllamaInstalled, bool OllamaServiceActive, IReadOnlyList<string> Models,
    bool SupportsAutomaticSetup, string? BlockedReason = null);

public sealed record SshCommandResult(int ExitStatus, string Output, string Error)
{
    public bool Success => ExitStatus == 0;
}

public sealed class SshHostKeyVerificationException(string message, string observedFingerprint,
    string? expectedFingerprint = null) : InvalidOperationException(message)
{
    public string ObservedFingerprint { get; } = observedFingerprint;
    public string? ExpectedFingerprint { get; } = expectedFingerprint;
}

