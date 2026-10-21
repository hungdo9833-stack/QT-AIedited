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
public enum BackendApi { Ollama, OpenAiCompatible }

public sealed class OllamaBackendProfile
{
    public const string DefaultLocalModel = "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M";
    public static Guid DefaultLocalId { get; } = Guid.Parse("4e384f1e-dbd8-4fb7-b14f-86a88ececf51");
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Ollama Local";
    public OllamaBackendKind Kind { get; set; }
    public BackendApi Api { get; set; }
    public int ParallelRequests { get; set; } = 1;
    public string Endpoint { get; set; } = OllamaClient.DefaultEndpoint.ToString();
    public string Model { get; set; } = "translategemma:12b";
    public AiRuntimeProfile? Runtime { get; set; }
    public decimal? HourlyPrice { get; set; }
    public GpuSshSettings? Ssh { get; set; }
    public bool RemoteDisclosureAccepted { get; set; }
    [JsonIgnore] public string? Token { get; set; }

    public static OllamaBackendProfile CreateDefaultLocal() => new()
    {
        Id = DefaultLocalId,
        Name = "llama-server Local",
        Kind = OllamaBackendKind.Local,
        Api = BackendApi.OpenAiCompatible,
        ParallelRequests = 4,
        Endpoint = "http://127.0.0.1:11450/api/chat",
        Model = DefaultLocalModel,
        Runtime = new AiRuntimeProfile
        {
            ModelOptions = new() { Model = DefaultLocalModel, ContextSize = 8192, MaximumOutputTokens = 3072 },
            SegmentationOptions = new()
        }
    };

    public void Validate()
    {
        Runtime ??= AiRuntimeProfile.CreateDefault(Model);
        Runtime.Validate();
        Model = Runtime.ModelOptions.Model.Trim();
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Trim().Length > 100 ||
            !Enum.IsDefined(Kind) || !Enum.IsDefined(Api) || ParallelRequests is < 1 or > 8 ||
            string.IsNullOrWhiteSpace(Model) || Model.Trim().Length > 200)
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
        if (Kind == OllamaBackendKind.Local && Api == BackendApi.OpenAiCompatible)
            _ = GgufModelReference.Parse(Model);
        if (Token?.Length > 8192 || Token?.Any(char.IsControl) == true)
            throw new InvalidDataException("Token xác thực không hợp lệ.");
        if (HourlyPrice is < 0 or > 1_000_000) throw new InvalidDataException("Giá thuê GPU theo giờ không hợp lệ.");
        Ssh?.Validate();
        if (Ssh is not null && Kind != OllamaBackendKind.Remote)
            throw new InvalidDataException("Chỉ profile Remote mới có cấu hình SSH.");
    }

    public OllamaBackendProfile Clone() => new()
    {
        Id = Id, Name = Name, Kind = Kind, Api = Api, ParallelRequests = ParallelRequests,
        Endpoint = Endpoint, Model = Model,
        Runtime = (Runtime ?? AiRuntimeProfile.CreateDefault(Model)).Clone(),
        RemoteDisclosureAccepted = RemoteDisclosureAccepted, Token = Token, HourlyPrice = HourlyPrice, Ssh = Ssh?.Clone()
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

    /// <summary>Raw current-user DPAPI blob, for the one place that needs the binary form (an .rdp password).</summary>
    internal static byte[] ProtectBytes(byte[] input) => ProtectOrUnprotect(input, protect: true);

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

public class OllamaProfileStore
{
    private readonly IOllamaSecretProtector _protector;
    public OllamaProfileStore(string? path = null, IOllamaSecretProtector? protector = null)
    {
        PathName = Path.GetFullPath(path ?? Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData), "QTStudio", "ollama-profiles.json"));
        _protector = protector ?? new WindowsDpapiOllamaSecretProtector();
    }

    public string PathName { get; }
    public async Task<OllamaProfileCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        StoredCatalog read = await SimpleFileStorage.ReadOptionalAsync(PathName, DecodeStored,
            () => EncodeCatalog(OllamaProfileCatalog.CreateDefault()), cancellationToken).ConfigureAwait(false);
        (OllamaProfileCatalog catalog, bool migrated) = DecodeCatalog(read);
        if (migrated) await SaveAsync(catalog, cancellationToken).ConfigureAwait(false);
        return catalog;
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
        await SimpleFileStorage.WriteAsync(PathName, bytes, DecodeStored, cancellationToken).ConfigureAwait(false);
    }

    private StoredCatalog EncodeCatalog(OllamaProfileCatalog catalog) => new()
    {
        ActiveProfileId = catalog.ActiveProfileId,
        Profiles = catalog.Profiles.Select(profile => new StoredProfile
        {
            Id = profile.Id, Name = profile.Name, Kind = profile.Kind, Api = profile.Api,
            ParallelRequests = profile.ParallelRequests, Endpoint = profile.Endpoint,
            Model = profile.Model, RemoteDisclosureAccepted = profile.RemoteDisclosureAccepted,
            Runtime = (profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model)).Clone(),
            ProtectedToken = Protect(profile.Token), HourlyPrice = profile.HourlyPrice,
            Ssh = profile.Ssh is null ? null : new StoredSsh
            {
                Host = profile.Ssh.Host, Port = profile.Ssh.Port, Username = profile.Ssh.Username,
                Authentication = profile.Ssh.Authentication, HostKeyFingerprint = profile.Ssh.HostKeyFingerprint,
                RemoteOllamaPort = profile.Ssh.RemoteOllamaPort, RdpPort = profile.Ssh.RdpPort,
                ProtectedPassword = Protect(profile.Ssh.Password),
                ProtectedPrivateKey = Protect(profile.Ssh.PrivateKey),
                ProtectedPrivateKeyPassphrase = Protect(profile.Ssh.PrivateKeyPassphrase)
            }
        }).ToList()
    };

    private (OllamaProfileCatalog Catalog, bool Migrated) DecodeCatalog(StoredCatalog stored)
    {
        var catalog = new OllamaProfileCatalog
        {
            SchemaVersion = stored.SchemaVersion,
            ActiveProfileId = stored.ActiveProfileId,
            Profiles = stored.Profiles.Select(profile => new OllamaBackendProfile
            {
                Id = profile.Id, Name = profile.Name, Kind = profile.Kind, Api = profile.Api,
                ParallelRequests = profile.ParallelRequests, Endpoint = profile.Endpoint,
                Model = profile.Model, RemoteDisclosureAccepted = profile.RemoteDisclosureAccepted,
                Runtime = profile.Runtime!.Clone(),
                Token = Unprotect(profile.ProtectedToken), HourlyPrice = profile.HourlyPrice,
                Ssh = profile.Ssh is null ? null : new GpuSshSettings
                {
                    Host = profile.Ssh.Host, Port = profile.Ssh.Port, Username = profile.Ssh.Username,
                    Authentication = profile.Ssh.Authentication, HostKeyFingerprint = profile.Ssh.HostKeyFingerprint,
                    RemoteOllamaPort = profile.Ssh.RemoteOllamaPort, RdpPort = profile.Ssh.RdpPort,
                    Password = Unprotect(profile.Ssh.ProtectedPassword),
                    PrivateKey = Unprotect(profile.Ssh.ProtectedPrivateKey),
                    PrivateKeyPassphrase = Unprotect(profile.Ssh.ProtectedPrivateKeyPassphrase)
                }
            }).ToList()
        };
        bool migrated = false;
        // Every local profile still on Ollama moves to llama-server once. A model that is already a GGUF reference
        // (Ollama's "hf.co/owner/repo:quant" form parses) keeps its runtime; an Ollama-only tag gets the default.
        foreach (OllamaBackendProfile profile in catalog.Profiles.Where(item => item.Kind == OllamaBackendKind.Local &&
                     (item.Api != BackendApi.OpenAiCompatible || !IsGguf(item.Model))))
        {
            OllamaBackendProfile defaults = OllamaBackendProfile.CreateDefaultLocal();
            profile.Name = defaults.Name;
            profile.Api = defaults.Api;
            profile.ParallelRequests = defaults.ParallelRequests;
            profile.Endpoint = defaults.Endpoint;
            if (IsGguf(profile.Model) && profile.Runtime is not null)
            {
                profile.Model = GgufModelReference.Parse(profile.Model).ToString();
                profile.Runtime.ModelOptions = profile.Runtime.ModelOptions with { Model = profile.Model };
            }
            else { profile.Model = defaults.Model; profile.Runtime = defaults.Runtime; }
            migrated = true;
        }
        catalog.Validate();
        return (catalog, migrated);
    }

    private static bool IsGguf(string model)
    {
        try { _ = GgufModelReference.Parse(model); return true; }
        catch (ArgumentException) { return false; }
    }

    private static StoredCatalog DecodeStored(byte[] bytes) => JsonFileCodec.DecodeDirect<StoredCatalog>(bytes, stored =>
    {
        if (stored.SchemaVersion != 1 || stored.Profiles is null || stored.Profiles.Count is < 1 or > 100 ||
            stored.Profiles.Any(profile => profile is null || profile.Id == Guid.Empty || string.IsNullOrWhiteSpace(profile.Name) ||
                string.IsNullOrWhiteSpace(profile.Endpoint) || string.IsNullOrWhiteSpace(profile.Model) || profile.Runtime is null) ||
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
        public BackendApi Api { get; set; }
        public int ParallelRequests { get; set; } = 1;
        public string Endpoint { get; set; } = "";
        public string Model { get; set; } = "";
        public AiRuntimeProfile? Runtime { get; set; }
        public bool RemoteDisclosureAccepted { get; set; }
        public string? ProtectedToken { get; set; }
        public decimal? HourlyPrice { get; set; }
        public StoredSsh? Ssh { get; set; }
    }

    private sealed class StoredSsh
    {
        public string Host { get; set; } = "";
        public int Port { get; set; } = 22;
        public string Username { get; set; } = "";
        public SshAuthenticationKind Authentication { get; set; }
        public string? HostKeyFingerprint { get; set; }
        public int RemoteOllamaPort { get; set; } = 11434;
        public int? RdpPort { get; set; }
        public string? ProtectedPassword { get; set; }
        public string? ProtectedPrivateKey { get; set; }
        public string? ProtectedPrivateKeyPassphrase { get; set; }
    }

    private string? Protect(string? value) => string.IsNullOrEmpty(value) ? null : _protector.Protect(value);
    private string? Unprotect(string? value) => string.IsNullOrEmpty(value) ? null : _protector.Unprotect(value);
}

public interface IOllamaBackendClient : IOllamaClient, IDisposable
{
    Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class OllamaBackendManager : IOllamaBackendClient, IJsonSchemaOllamaClient
{
    private readonly OllamaProfileStore _store;
    private readonly Func<OllamaBackendProfile, IOllamaBackendClient> _factory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<Guid, IOllamaBackendClient> _runtimeClients = [];
    private readonly bool _usesDefaultFactory;
    private OllamaProfileCatalog _catalog = OllamaProfileCatalog.CreateDefault();
    private IOllamaBackendClient _client;
    private bool _disposed;

    public OllamaBackendManager(OllamaProfileStore? store = null,
        Func<OllamaBackendProfile, IOllamaBackendClient>? clientFactory = null)
    {
        _store = store ?? new OllamaProfileStore();
        _usesDefaultFactory = clientFactory is null;
        _factory = clientFactory ?? OllamaClient.Create;
        _client = BuildClient(_catalog.Profiles[0]);
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

    public async Task DeleteProfileAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        if (profileId == OllamaBackendProfile.DefaultLocalId)
            throw new InvalidOperationException("Không thể xóa profile Ollama Local mặc định.");
        OllamaProfileCatalog next = _catalog.Clone();
        if (next.Profiles.RemoveAll(profile => profile.Id == profileId) == 0)
            throw new KeyNotFoundException("Không tìm thấy profile Ollama cần xóa.");
        if (next.ActiveProfileId == profileId) next.ActiveProfileId = OllamaBackendProfile.DefaultLocalId;
        next.Validate();
        await _store.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { _runtimeClients.Remove(profileId); Replace(next); }
        finally { _gate.Release(); }
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task AttachRuntimeClientAsync(Guid profileId, IOllamaBackendClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_catalog.Profiles.All(profile => profile.Id != profileId))
                throw new KeyNotFoundException("Không tìm thấy profile Ollama cho SSH tunnel.");
            _runtimeClients[profileId] = client;
            if (_catalog.ActiveProfileId == profileId) ReplaceClient(_catalog.Profiles.Single(profile => profile.Id == profileId));
        }
        finally { _gate.Release(); }
        ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task DetachRuntimeClientAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _runtimeClients.Remove(profileId);
            if (_catalog.ActiveProfileId == profileId) ReplaceClient(_catalog.Profiles.Single(profile => profile.Id == profileId));
        }
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

    public async Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
        JsonElement responseSchema, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (ActiveProfile.Kind == OllamaBackendKind.Remote && !ActiveProfile.RemoteDisclosureAccepted)
                throw new InvalidOperationException("Profile remote chưa được xác nhận quyền gửi nội dung truyện.");
            return _client is IJsonSchemaOllamaClient structured
                ? await structured.TranslateStructuredAsync(prompt, options, responseSchema, cancellationToken).ConfigureAwait(false)
                : await _client.TranslateAsync(prompt, options, cancellationToken).ConfigureAwait(false);
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
        _catalog = catalog.Clone();
        ReplaceClient(profile);
    }

    private void ReplaceClient(OllamaBackendProfile profile)
    {
        IOllamaBackendClient replacement = BuildClient(profile);
        IOllamaBackendClient old = _client;
        _client = replacement;
        old.Dispose();
    }

    private IOllamaBackendClient BuildClient(OllamaBackendProfile profile)
    {
        if (_runtimeClients.TryGetValue(profile.Id, out IOllamaBackendClient? runtime))
            return new NonOwningOllamaBackendClient(runtime);
        if (_usesDefaultFactory && profile.Ssh is not null)
            return new UnavailableOllamaBackendClient("SSH tunnel chưa kết nối. Mở GPU Manager và bấm Kết nối trước khi dịch.");
        return _factory(profile.Clone());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
        _gate.Dispose();
    }
}

internal sealed class NonOwningOllamaBackendClient(IOllamaBackendClient inner) : IOllamaBackendClient, IJsonSchemaOllamaClient
{
    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default) =>
        inner.TranslateAsync(prompt, options, cancellationToken);
    public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        inner.CheckConnectionAsync(model, timeout, cancellationToken);
    public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        inner.ListModelsAsync(timeout, cancellationToken);
    public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
        JsonElement responseSchema, CancellationToken cancellationToken = default) =>
        inner is IJsonSchemaOllamaClient structured
            ? structured.TranslateStructuredAsync(prompt, options, responseSchema, cancellationToken)
            : inner.TranslateAsync(prompt, options, cancellationToken);
    public void Dispose() { }
}

internal sealed class UnavailableOllamaBackendClient(string message) : IOllamaBackendClient
{
    private InvalidOperationException Error() => new(message);
    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default) =>
        Task.FromException<OllamaCompletion>(Error());
    public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromException(Error());
    public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<string>>(Error());
    public void Dispose() { }
}
