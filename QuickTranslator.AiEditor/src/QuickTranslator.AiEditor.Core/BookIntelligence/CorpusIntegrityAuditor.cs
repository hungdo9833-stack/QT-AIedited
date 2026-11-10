using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public static class CorpusIntegrityAuditor
{
    /// <summary>SHA-256 of a chapter's original text, stored with the census. Changing it invalidates saved hashes.</summary>
    public static string SourceHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}
