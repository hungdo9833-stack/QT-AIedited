using System.IO;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Publishing;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>The Sắc Viện publishing token lives only in Windows Credential Manager, never in Project or settings files.</summary>
internal static class VanCacTokenVault
{
    internal const string Target = "QTStudio.VanCac.PublisherToken.v1";
    private static readonly IGeminiCredentialStore NativeStore = new WindowsCredentialStore("mã kết nối Sắc Viện");

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
            throw new ArgumentException("Mã phiên đăng nhập Sắc Viện không hợp lệ. Hãy đăng nhập lại.");
        store.Write(Target, value);
    }

    public static void Delete() => Delete(NativeStore);
    internal static void Delete(IGeminiCredentialStore store) => store.Delete(Target);
}

/// <summary>How far a Project's novel is on Sắc Viện: the highest public chapter number and the counts by status.</summary>
internal sealed record VanCacWebProgress(int PublishedThrough, int PublishedCount, int DraftCount, DateTimeOffset CheckedAt)
{
    public static VanCacWebProgress From(IReadOnlyList<VanCacRemoteChapter> chapters, DateTimeOffset checkedAt)
    {
        int[] published = chapters.Where(c => c.PublicationStatus == "published").Select(c => c.Number).ToArray();
        return new(published.Length == 0 ? 0 : published.Max(), published.Length,
            chapters.Count(c => c.PublicationStatus == "draft"), checkedAt);
    }
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

    public string BaseUrl { get; set; } = "https://sacvien.com";
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public List<string> Roles { get; set; } = [];
    public DateTimeOffset? ExpiresAt { get; set; }
    public Dictionary<Guid, string> NovelByProject { get; set; } = [];
    /// <summary>Last chapter list Sắc Viện returned for each Project's linked novel (library cards show it offline).</summary>
    public Dictionary<Guid, VanCacWebProgress> WebProgressByProject { get; set; } = [];
    public bool PublishImmediately { get; set; }

    /// <summary>Remembers what the website holds for the Project linked to <paramref name="novelId"/>, if any.</summary>
    public bool RecordWebProgress(string novelId, IReadOnlyList<VanCacRemoteChapter> chapters)
    {
        Guid[] projects = NovelByProject.Where(link => link.Value == novelId).Select(link => link.Key).ToArray();
        foreach (Guid project in projects) WebProgressByProject[project] = VanCacWebProgress.From(chapters, DateTimeOffset.Now);
        return projects.Length > 0;
    }

    public string EnsureDeviceId()
    {
        DeviceId = Guid.TryParse(DeviceId, out Guid id) && id != Guid.Empty
            ? id.ToString("D") : Guid.NewGuid().ToString("D");
        return DeviceId;
    }

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
