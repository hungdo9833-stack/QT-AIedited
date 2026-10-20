using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>
/// Quota scope must represent the Google AI project rather than a QT Studio novel.
/// Explicit Google project ID is preferred. With only one API key, its deterministic
/// fingerprint keeps quota shared across novels without storing the secret.
/// Separate keys belonging to the same Google project require explicit project ID.
/// </summary>
public static class GeminiSurveyProjectScope
{
    public static Guid Resolve(string apiKey, string? googleProjectId)
    {
        string identity = !string.IsNullOrWhiteSpace(googleProjectId)
            ? "google-project:" + googleProjectId.Trim()
            : "google-api-key:" + apiKey;
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        return new Guid(digest.AsSpan(0, 16));
    }
}
