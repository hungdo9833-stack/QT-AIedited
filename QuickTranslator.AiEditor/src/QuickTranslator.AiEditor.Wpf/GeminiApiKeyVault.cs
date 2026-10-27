using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace QuickTranslator.AiEditor.Wpf;

internal interface IGeminiCredentialStore
{
    string? Read(string target);
    void Write(string target, string value);
    void Delete(string target);
}

/// <summary>Stores the shared ordered Gemini key list only in Windows Credential Manager.</summary>
internal static class GeminiApiKeyVault
{
    internal const string ListTarget = "QTStudio.Gemini.ApiKeys.v2";
    internal const string LegacyTarget = "QTStudio.GeminiSurvey.ApiKey.v1";
    private static readonly IGeminiCredentialStore NativeStore = new WindowsCredentialStore("Gemini API key", "Danh sách Gemini API key");

    public static IReadOnlyList<string> ReadAll() => ReadAll(NativeStore);

    internal static IReadOnlyList<string> ReadAll(IGeminiCredentialStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        string? stored = store.Read(ListTarget);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            try
            {
                string[] values = JsonSerializer.Deserialize<string[]>(stored) ?? [];
                return Normalize(values);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Danh sách Gemini API key đã lưu có định dạng không hợp lệ.", ex);
            }
        }

        string? legacy = store.Read(LegacyTarget);
        if (string.IsNullOrWhiteSpace(legacy)) return [];
        string[] migrated = Normalize([legacy]);
        SaveAll(store, migrated);
        store.Delete(LegacyTarget);
        return migrated;
    }

    public static void SaveAll(IEnumerable<string> apiKeys) => SaveAll(NativeStore, apiKeys);

    internal static void SaveAll(IGeminiCredentialStore store, IEnumerable<string> apiKeys)
    {
        ArgumentNullException.ThrowIfNull(store);
        string[] keys = Normalize(apiKeys);
        if (keys.Length == 0)
        {
            store.Delete(ListTarget);
            return;
        }
        store.Write(ListTarget, JsonSerializer.Serialize(keys));
    }

    public static void Save(string apiKey) => SaveAll(ReadAll().Append(apiKey));

    public static void RemoveAt(int index) => RemoveAt(NativeStore, index);

    internal static void RemoveAt(IGeminiCredentialStore store, int index)
    {
        List<string> keys = ReadAll(store).ToList();
        if ((uint)index >= (uint)keys.Count) throw new ArgumentOutOfRangeException(nameof(index));
        keys.RemoveAt(index);
        SaveAll(store, keys);
    }

    public static string? Read() => ReadAll().FirstOrDefault();

    public static void Delete()
    {
        NativeStore.Delete(ListTarget);
        NativeStore.Delete(LegacyTarget);
    }

    public static string ResolveForSurvey() =>
        Read() ?? Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY") ?? "";

    public static string Mask(string apiKey)
    {
        string key = apiKey?.Trim() ?? "";
        return key.Length >= 8 ? key[..4] + "…" + key[^4..] : "••••••••";
    }

    private static string[] Normalize(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string? value in values)
        {
            string key = value?.Trim() ?? "";
            if (key.Length == 0) continue;
            if (key.Contains('\r') || key.Contains('\n'))
                throw new ArgumentException("Mỗi Gemini API key phải nằm trên một dòng riêng.", nameof(values));
            if (seen.Add(key)) result.Add(key);
        }
        return result.ToArray();
    }
}
