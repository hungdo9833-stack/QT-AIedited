using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>Stable ids and fingerprints stored in the addressing ledger. Changing either changes saved data.</summary>
public static class EntityMemoryIdentity
{
    public static Guid StableId(Guid project, string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(project.ToString("D") + "|" + key)).AsSpan(0, 16));
    public static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, DocumentJson.Options)));
}
