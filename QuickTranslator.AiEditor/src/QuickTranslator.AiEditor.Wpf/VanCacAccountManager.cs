using System.Net.Http;
using QuickTranslator.AiEditor.Core.Publishing;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>Shared local account state. Construction reads local stores only; HTTP is explicit and lazy.</summary>
internal sealed class VanCacAccountManager
{
    private static readonly Lazy<VanCacAccountManager> Shared = new(() => new());
    public static VanCacAccountManager Default => Shared.Value;
    internal const string SessionExpiredMessage = "Phiên đăng nhập Sắc Viện đã hết hạn hoặc bị thu hồi. Hãy đăng nhập lại.";
    private readonly string? _path;
    private readonly Action<string> _write;
    private readonly Action _delete;
    private readonly HttpMessageHandler? _handler;
    private bool _needsLogin;
    public VanCacPublisherPreferences Preferences { get; }
    public string? Token { get; private set; }
    public bool IsSignedIn => Token is not null && !_needsLogin;
    public event EventHandler? Changed;
    public string StatusText => IsSignedIn ? $"Sắc Viện: {AccountName}" : "Sắc Viện: chưa đăng nhập";
    public string AccountName => string.IsNullOrWhiteSpace(Preferences.DisplayName) ? "Tài khoản đã lưu" : Preferences.DisplayName;
    public string? LocalError { get; private set; }

    internal VanCacAccountManager(string? preferencesPath = null, IGeminiCredentialStore? store = null,
        HttpMessageHandler? handler = null, Func<string?>? tokenReader = null)
    {
        _path = preferencesPath;
        Preferences = VanCacPublisherPreferences.Load(preferencesPath);
        _handler = handler;
        Func<string?> read = tokenReader ?? (store is null ? VanCacTokenVault.Read : () => VanCacTokenVault.Read(store));
        _write = store is null ? VanCacTokenVault.Save : token => VanCacTokenVault.Save(store, token);
        _delete = store is null ? VanCacTokenVault.Delete : () => VanCacTokenVault.Delete(store);
        try { Token = read(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.InvalidDataException)
        { LocalError = ex.Message; }
    }

    public VanCacPublisherClient CreateClient(string? baseUrl = null, string? token = null) =>
        new(baseUrl ?? Preferences.BaseUrl, token ?? Token ?? throw new InvalidOperationException("Cần đăng nhập Sắc Viện."), _handler);

    public void SavePreferences() => Preferences.Save(_path);

    public void ExpireSession()
    {
        _needsLogin = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoginAsync(string baseUrl, string email, string password, CancellationToken cancellationToken)
    {
        string origin = VanCacPublisherClient.NormalizeBaseUrl(baseUrl).ToString().TrimEnd('/');
        if (Token is null) Preferences.BaseUrl = origin;
        string deviceId = Preferences.EnsureDeviceId();
        SavePreferences();
        string deviceName = Environment.MachineName.Trim();
        if (deviceName.Length > 60) deviceName = deviceName[..60];
        if (deviceName.Length == 0) deviceName = "QT Studio";
        VanCacSession session = await VanCacPublisherClient.LoginAsync(origin, email.Trim(), password,
            deviceId, deviceName, _handler, cancellationToken);
        // Even if the window closes while HTTP completes, do not silently sign in afterwards;
        // revoke the token the website just issued so it does not hold a slot for 90 days.
        if (cancellationToken.IsCancellationRequested)
        {
            _ = RevokeQuietlyAsync(origin, session.Token);
            cancellationToken.ThrowIfCancellationRequested();
        }
        _write(session.Token);
        Token = session.Token;
        _needsLogin = false;
        Preferences.BaseUrl = origin;
        Preferences.ExpiresAt = session.ExpiresAt;
        CacheUser(session.User);
    }

    private async Task RevokeQuietlyAsync(string origin, string token)
    {
        try
        {
            using VanCacPublisherClient client = CreateClient(origin, token);
            await client.LogoutAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is VanCacApiException or ArgumentException or System.Text.Json.JsonException) { }
    }

    public void CacheUser(VanCacUser user)
    {
        Preferences.DisplayName = user.DisplayName;
        Preferences.Roles = user.Roles.ToList();
        try { SavePreferences(); }
        finally { Changed?.Invoke(this, EventArgs.Empty); }
    }

    public void SaveManualConnection(string baseUrl, string? token, VanCacUser user)
    {
        Preferences.EnsureDeviceId();
        if (token is not null)
        {
            _write(token);
            Token = token;
            _needsLogin = false;
            Preferences.ExpiresAt = null;
        }
        Preferences.BaseUrl = baseUrl;
        CacheUser(user);
    }

    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Token is not null)
            {
                using VanCacPublisherClient client = CreateClient();
                await client.LogoutAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is VanCacApiException or OperationCanceledException or ArgumentException or System.Text.Json.JsonException) { }
        finally
        {
            _delete();
            Token = null;
            Preferences.DisplayName = "";
            Preferences.Roles = [];
            Preferences.ExpiresAt = null;
            try { SavePreferences(); }
            finally { Changed?.Invoke(this, EventArgs.Empty); }
        }
    }
}
