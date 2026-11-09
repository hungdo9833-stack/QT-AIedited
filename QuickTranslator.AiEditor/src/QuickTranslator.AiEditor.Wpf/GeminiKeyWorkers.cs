using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>One saved Gemini API key as a translation worker: its own quota, so each key translates one book at a time.</summary>
internal sealed record GeminiKeyWorker(string Key, int Index, string Fingerprint, Guid ProfileId, string? CustomName)
{
    public string Name => string.IsNullOrWhiteSpace(CustomName) ? $"Key {Index + 1}" : CustomName.Trim();
    public string Masked => GeminiApiKeyVault.Mask(Key);
}

/// <summary>
/// Gemini keys as workers, and the names the user gives them. Names live in a plain settings file keyed by a short
/// fingerprint of the key (a hash, never the key itself); the keys stay in Windows Credential Manager.
/// </summary>
internal static class GeminiKeyWorkers
{
    internal static Func<IReadOnlyList<string>> KeyReader = GeminiApiKeyVault.ReadAll;
    internal static string NamesPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QTStudio", "gemini-key-names.json");
    private static readonly object Gate = new();

    public static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key.Trim())))[..12].ToLowerInvariant();

    /// <summary>A stable worker id per key, so a book's queue remembers which key translated it.</summary>
    public static Guid ProfileId(string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes("qtstudio-gemini-key:" + key.Trim())).AsSpan(0, 16));

    public static IReadOnlyList<GeminiKeyWorker> All()
    {
        IReadOnlyList<string> keys = KeyReader();
        IReadOnlyDictionary<string, string> names = ReadNames();
        return keys.Select((key, index) =>
        {
            string fingerprint = Fingerprint(key);
            return new GeminiKeyWorker(key, index, fingerprint, ProfileId(key), names.GetValueOrDefault(fingerprint));
        }).ToList();
    }

    public static GeminiKeyWorker? ByFingerprint(string fingerprint) =>
        All().FirstOrDefault(worker => worker.Fingerprint == fingerprint);

    public static GeminiKeyWorker? ByProfile(Guid profileId) =>
        All().FirstOrDefault(worker => worker.ProfileId == profileId);

    public static bool IsKeyProfile(Guid profileId)
    {
        try { return ByProfile(profileId) is not null; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The name given to a key, or "" when it has none.</summary>
    public static string NameOf(string fingerprint) => ReadNames().GetValueOrDefault(fingerprint) ?? "";

    /// <summary>Gives a key a name; an empty name goes back to "Key N".</summary>
    public static void SetName(string fingerprint, string? name)
    {
        lock (Gate)
        {
            var names = new Dictionary<string, string>(ReadNames(), StringComparer.Ordinal);
            string value = (name ?? "").Trim();
            if (value.Length == 0) names.Remove(fingerprint);
            else names[fingerprint] = value.Length > 40 ? value[..40] : value;
            Directory.CreateDirectory(Path.GetDirectoryName(NamesPath)!);
            File.WriteAllText(NamesPath, JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
    }

    private static IReadOnlyDictionary<string, string> ReadNames()
    {
        lock (Gate)
        {
            try
            {
                return File.Exists(NamesPath)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(NamesPath)) ?? []
                    : new Dictionary<string, string>();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                return new Dictionary<string, string>();
            }
        }
    }
}
