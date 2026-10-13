using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum OllamaBackendKind { Local, Remote }

public sealed class OllamaBackendProfile
{
    public static Guid DefaultLocalId { get; } = Guid.Parse("4e384f1e-dbd8-4fb7-b14f-86a88ececf51");
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Ollama Local";
    public OllamaBackendKind Kind { get; set; }
    public string Endpoint { get; set; } = OllamaClient.DefaultEndpoint.ToString();
    public string Model { get; set; } = "translategemma:12b";
    public bool RemoteDisclosureAccepted { get; set; }
    [JsonIgnore] public string? Token { get; set; }

    public static OllamaBackendProfile CreateDefaultLocal() => new()
    {
        Id = DefaultLocalId,
        Name = "Ollama Local",
        Kind = OllamaBackendKind.Local,
        Endpoint = OllamaClient.DefaultEndpoint.ToString(),
        Model = "translategemma:12b"
    };

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 100 ||
            !Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(Model) || Model.Trim().Length > 200)
            throw new InvalidDataException("Profile Ollama thiếu tên, ID, loại backend hoặc model hợp lệ.");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out Uri? endpoint))
            throw new InvalidDataException("Endpoint Ollama không phải URL tuyệt đối hợp lệ.");
        Endpoint = (Kind == OllamaBackendKind.Local
            ? OllamaClient.ValidateLocalEndpoint(endpoint)
            : OllamaClient.ValidateRemoteEndpoint(endpoint)).ToString();
        Name = Name.Trim();
        Model = Model.Trim();
        if (Kind == OllamaBackendKind.Local && !string.IsNullOrEmpty(Token))
            throw new InvalidDataException("Profile Local không lưu hoặc gửi token.");
        if (Token?.Length > 8192 || Token?.Any(char.IsControl) == true)
            throw new InvalidDataException("Token xác thực không hợp lệ.");
    }

    public OllamaBackendProfile Clone() => new()
    {
        Id = Id, Name = Name, Kind = Kind, Endpoint = Endpoint, Model = Model,
        RemoteDisclosureAccepted = RemoteDisclosureAccepted, Token = Token
    };
}

public sealed class OllamaProfileCatalog
{
    public int SchemaVersion { get; set; } = 1;
    public Guid ActiveProfileId { get; set; } = OllamaBackendProfile.DefaultLocalId;
    public List<OllamaBackendProfile> Profiles { get; set; } = [OllamaBackendProfile.CreateDefaultLocal()];

    public static OllamaProfileCatalog CreateDefault() => new();

    public void Validate()
    {
        if (SchemaVersion != 1 || Profiles is null || Profiles.Count is < 1 or > 100 ||
            Profiles.Any(profile => profile is null) || Profiles.Select(profile => profile.Id).Distinct().Count() != Profiles.Count ||
            Profiles.All(profile => profile.Id != ActiveProfileId))
            throw new InvalidDataException("Danh sách profile Ollama không hợp lệ.");
        foreach (OllamaBackendProfile profile in Profiles) profile.Validate();
    }

    public OllamaProfileCatalog Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        ActiveProfileId = ActiveProfileId,
        Profiles = Profiles.Select(profile => profile.Clone()).ToList()
    };
}

public interface IOllamaSecretProtector
{
    string Protect(string value);
    string Unprotect(string value);
}

public sealed class WindowsDpapiOllamaSecretProtector : IOllamaSecretProtector
{
    public string Protect(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Convert.ToBase64String(ProtectOrUnprotect(Encoding.UTF8.GetBytes(value), protect: true));
    }

    public string Unprotect(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return new UTF8Encoding(false, true).GetString(ProtectOrUnprotect(Convert.FromBase64String(value), protect: false));
    }

    private static byte[] ProtectOrUnprotect(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI chỉ khả dụng trên Windows.");
        DataBlob source = default, destination = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            source.Size = input.Length;
            source.Data = Marshal.AllocHGlobal(input.Length);
            Marshal.Copy(input, 0, source.Data, input.Length);
            bool success = protect
                ? CryptProtectData(ref source, "QT Studio Ollama token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out destination)
                : CryptUnprotectData(ref source, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out destination);
            if (!success)
            {
                int error = Marshal.GetLastWin32Error();
                throw new CryptographicException("Windows DPAPI không thể bảo vệ thông tin xác thực.", new Win32Exception(error));
            }
            var output = new byte[destination.Size];
            Marshal.Copy(destination.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            if (source.Data != IntPtr.Zero) Marshal.FreeHGlobal(source.Data);
            if (destination.Data != IntPtr.Zero) LocalFree(destination.Data);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, out IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class OllamaProfileStore
{
    private readonly IOllamaSecretProtector _protector;
    public OllamaProfileStore(string? path = null, IOllamaSecretProtector? protector = null)
    {
        PathName = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "QTStudio", "ollama-profiles.json"));
        _protector = protector ?? new WindowsDpapiOllamaSecretProtector();
    }

    public string PathName { get; }
    public RecoveryInfo? LastRecovery { get; private set; }

    public async Task<OllamaProfileCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        RecoveryRead<StoredCatalog> read = await RecoveryFiles.ReadAsync(PathName, DecodeStored,
            () => EncodeCatalog(OllamaProfileCatalog.CreateDefault()), cancellationToken).ConfigureAwait(false);
        LastRecovery = read.Info;
        return DecodeCatalog(read.Value);
    }

    public async Task SaveAsync(OllamaProfileCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        OllamaProfileCatalog copy = catalog.Clone();
        copy.Validate();
        StoredCatalog stored = EncodeCatalog(copy);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(stored,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        LastRecovery = await RecoveryFiles.WriteAsync(PathName, bytes, DecodeStored, cancellationToken).ConfigureAwait(false);
    }

    private StoredCatalog EncodeCatalog(OllamaProfileCatalog catalog) => new()
    {
        ActiveProfileId = catalog.ActiveProfileId,
        Profiles = catalog.Profiles.Select(profile => new StoredProfile
        {
            Id = profile.Id, Name = profile.Name, Kind = profile.Kind, Endpoint = profile.Endpoint,
            Model = profile.Model, RemoteDisclosureAccepted = profile.RemoteDisclosureAccepted,
            ProtectedToken = string.IsNullOrEmpty(profile.Token) ? null : _protector.Protect(profile.Token)
        }).ToList()
    };

    private OllamaProfileCatalog DecodeCatalog(StoredCatalog stored)
    {
        var catalog = new OllamaProfileCatalog
        {
            SchemaVersion = stored.SchemaVersion,
            ActiveProfileId = stored.ActiveProfileId,
            Profiles = stored.Profiles.Select(profile => new OllamaBackendProfile
            {
                Id = profile.Id, Name = profile.Name, Kind = profile.Kind, Endpoint = profile.Endpoint,
                Model = profile.Model, RemoteDisclosureAccepted = profile.RemoteDisclosureAccepted,
                Token = string.IsNullOrEmpty(profile.ProtectedToken) ? null : _protector.Unprotect(profile.ProtectedToken)
            }).ToList()
        };
        catalog.Validate();
        return catalog;
    }

    private static StoredCatalog DecodeStored(byte[] bytes) => RecoveryFiles.DecodeJson<StoredCatalog>(bytes, stored =>
    {
        if (stored.SchemaVersion != 1 || stored.Profiles is null || stored.Profiles.Count is < 1 or > 100 ||
            stored.Profiles.Any(profile => profile is null || profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) ||
                string.IsNullOrWhiteSpace(profile.Endpoint) || string.IsNullOrWhiteSpace(profile.Model)) ||
            stored.Profiles.Select(profile => profile.Id).Distinct().Count() != stored.Profiles.Count ||
            stored.Profiles.All(profile => profile.Id != stored.ActiveProfileId))
            throw new InvalidDataException("File profile Ollama không hợp lệ.");
    }, "schemaVersion", "activeProfileId", "profiles");

    private sealed class StoredCatalog
    {
        public int SchemaVersion { get; set; } = 1;
        public Guid ActiveProfileId { get; set; }
        public List<StoredProfile> Profiles { get; set; } = [];
    }

    private sealed class StoredProfile
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public OllamaBackendKind Kind { get; set; }
        public string Endpoint { get; set; } = "";
        public string Model { get; set; } = "";
        public bool RemoteDisclosureAccepted { get; set; }
        public string? ProtectedToken { get; set; }
    }
}

public interface IOllamaBackendClient : IOllamaClient, IDisposable
{
    Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class OllamaBackendManager : IOllamaBackendClient
{
    private readonly OllamaProfileStore _store;
    private readonly Func<OllamaBackendProfile, IOllamaBackendClient> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private OllamaProfileCatalog _catalog = OllamaProfileCatalog.CreateDefault();
    private IOllamaBackendClient _client;
    private bool _disposed;

    public OllamaBackendManager(OllamaProfileStore? store = null,
        Func<OllamaBackendProfile, IOllamaBackendClient>? clientFactory = null)
    {
        _store = store ?? new OllamaProfileStore();
        _factory = clientFactory ?? OllamaClient.Create;
        _client = _factory(_catalog.Profiles[0]);
    }

    public event EventHandler? ActiveProfileChanged;
    public OllamaBackendProfile ActiveProfile => _catalog.Profiles.Single(profile => profile.Id == _catalog.ActiveProfileId).Clone();
    public IReadOnlyList<OllamaBackendProfile> Profiles => _catalog.Profiles.Select(profile => profile.Clone()).ToArray();
    public OllamaProfileCatalog Catalog => _catalog.Clone();

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        OllamaProfileCatalog loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Replace(loaded); }
        finally { _gate.Release(); }
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public OllamaBackendProfile GetProfile(Guid? profileId)
    {
        Guid resolved = profileId ?? OllamaBackendProfile.DefaultLocalId;
        return _catalog.Profiles.SingleOrDefault(profile => profile.Id == resolved)?.Clone()
            ?? throw new InvalidOperationException("Profile Ollama đã chọn không còn trong vùng dữ liệu người dùng. Hãy chọn lại profile; không tự chuyển backend.");
    }

    public async Task SaveProfileAsync(OllamaBackendProfile profile, bool activate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        OllamaBackendProfile proposed = profile.Clone();
        proposed.Validate();
        if (activate && proposed.Kind == OllamaBackendKind.Remote && !proposed.RemoteDisclosureAccepted)
            throw new InvalidOperationException("Cần xác nhận gửi nội dung truyện tới server remote trước khi kích hoạt profile.");
        OllamaProfileCatalog next = _catalog.Clone();
        int index = next.Profiles.FindIndex(existing => existing.Id == proposed.Id);
        if (index < 0) next.Profiles.Add(proposed); else next.Profiles[index] = proposed;
        if (activate) next.ActiveProfileId = proposed.Id;
        next.Validate();
        await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (activate) Replace(next); else _catalog = next;
        }
        finally { _gate.Release(); }
        if (activate) ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ActivateAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        OllamaProfileCatalog next = _catalog.Clone();
        OllamaBackendProfile profile = next.Profiles.SingleOrDefault(item => item.Id == profileId)
            ?? throw new KeyNotFoundException("Không tìm thấy profile Ollama.");
        if (profile.Kind == OllamaBackendKind.Remote && !profile.RemoteDisclosureAccepted)
            throw new InvalidOperationException("Cần xác nhận gửi nội dung truyện tới server remote trước khi kích hoạt profile.");
        next.ActiveProfileId = profileId;
        await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { Replace(next); }
        finally { _gate.Release(); }
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ActiveProfile.Kind == OllamaBackendKind.Remote && !ActiveProfile.RemoteDisclosureAccepted)
                throw new InvalidOperationException("Profile remote chưa được xác nhận quyền gửi nội dung truyện.");
            return await _client.TranslateAsync(prompt, options, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await _client.CheckConnectionAsync(model, timeout, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await _client.ListModelsAsync(timeout, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private void Replace(OllamaProfileCatalog catalog)
    {
        catalog.Validate();
        OllamaBackendProfile profile = catalog.Profiles.Single(item => item.Id == catalog.ActiveProfileId);
        IOllamaBackendClient replacement = _factory(profile.Clone());
        IOllamaBackendClient old = _client;
        _catalog = catalog.Clone();
        _client = replacement;
        old.Dispose();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        _gate.Dispose();
    }
}
