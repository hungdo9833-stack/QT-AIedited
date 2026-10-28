using System.IO;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Publishing;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>The Văn Các publishing token lives only in Windows Credential Manager, never in Project or settings files.</summary>
internal static class VanCacTokenVault
{
    internal const string Target = "QTStudio.VanCac.PublisherToken.v1";
    private static readonly IGeminiCredentialStore NativeStore = new WindowsCredentialStore("mã kết nối Văn Các");

    public static string? Read() => Read(NativeStore);
    internal static string? Read(IGeminiCredentialStore store)
    {
        string? token = store.Read(Target)?.Trim();
        return VanCacPublisherClient.IsValidToken(token) ? token : null;
    }

    public static void Save(string token) => Save(NativeStore, token);
    internal static void Save(IGeminiCredentialStore store, string token)
    {
        string value = token?.Trim() ?? "";
        if (!VanCacPublisherClient.IsValidToken(value))
            throw new ArgumentException("Mã kết nối phải có dạng vcp_ và 64 ký tự 0–9, a–f. Sao chép lại từ trang Tài khoản trên website.");
        store.Write(Target, value);
    }

    public static void Delete() => NativeStore.Delete(Target);

    /// <summary>Shows only the last 4 characters, matching the hint on the website's token list.</summary>
    public static string Mask(string token) => "vcp_…" + token[^4..];
}

/// <summary>
/// Website address and the target novel per Project (by ProjectId). Stored in LocalAppData, not in the Project,
/// so moving or sharing a Project never carries publishing configuration.
/// </summary>
internal sealed class VanCacPublisherPreferences
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslator.AiEditor", "vancac-publisher.json");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string BaseUrl { get; set; } = "";
    public Dictionary<Guid, string> NovelByProject { get; set; } = [];
    public bool PublishImmediately { get; set; }

    public static VanCacPublisherPreferences Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        if (!File.Exists(file)) return new();
        try { return JsonSerializer.Deserialize<VanCacPublisherPreferences>(File.ReadAllBytes(file), Json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(string? path = null)
    {
        string file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temporary = file + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(this, Json));
        File.Move(temporary, file, true);
    }
}
