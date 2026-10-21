using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// Dialogue register from the project's story setting: one prompt line, and a post-translation warning when a
/// modern book's dialogue still carries archaic addressing. Terms a confirmed project rule uses are never banned.
/// </summary>
public static class StorySettingRules
{
    // "ta" only as a standalone pronoun: "chúng ta", "anh ta", "người ta"… are modern and skipped by the caller.
    private static readonly string[] ArchaicTerms = ["ngươi", "ta", "huynh", "muội", "tại hạ", "bản tọa", "lão phu"];
    private static readonly HashSet<string> ModernBeforeTa = new(StringComparer.OrdinalIgnoreCase)
    {
        "chúng", "anh", "cô", "hắn", "ông", "bà", "chị", "cậu", "bọn", "người", "gã", "thằng", "lão", "ả", "y", "mụ",
        "nó", "em", "con", "cụ", "nàng", "chàng", "cha", "mẹ", "bố", "chú", "dì"
    };

    public static string PromptBlock(CharacterProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Setting switch
        {
            StorySetting.Modern => "STORY SETTING: modern. Dialogue must use modern Vietnamese addressing chosen from the speakers' " +
                "relationship and mood (tôi, anh, em, chị, cô, cậu, ông, bà, con, bố, mẹ, tao, mày…)." +
                (BannedTerms(project) is { Count: > 0 } banned ? $" Never use archaic addressing: {string.Join(", ", banned)}." : ""),
            StorySetting.Historical => "STORY SETTING: historical. Dialogue may use classical Vietnamese addressing (ta, ngươi, huynh, " +
                "muội, tại hạ…) that fits each speaker's rank and relationship; keep each pair's addressing consistent.",
            _ => ""
        };
    }

    /// <summary>One warning per archaic term found inside quoted dialogue of a modern book.</summary>
    public static IReadOnlyList<TranslationIssue> CheckDialogue(CharacterProject project, string translatedText)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Setting != StorySetting.Modern || string.IsNullOrWhiteSpace(translatedText)) return [];
        var banned = BannedTerms(project);
        if (banned.Count == 0) return [];
        var pattern = new Regex(@"(?<![\p{L}\p{M}])(" + string.Join("|", banned.OrderByDescending(t => t.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{M}])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var issues = new List<TranslationIssue>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (start, end) in PronounResolutionEngine.DialogueRanges(translatedText))
        {
            string quote = translatedText[start..end];
            foreach (Match match in pattern.Matches(quote))
            {
                string term = match.Value.ToLowerInvariant();
                if (term == "ta" && ModernBeforeTa.Contains(PreviousWord(quote, match.Index))) continue;
                if (!seen.Add(term)) continue;
                issues.Add(new(IssueCode.ArchaicAddressing, IssueSeverity.Warning,
                    $"Bối cảnh hiện đại nhưng lời thoại dùng “{match.Value}”: {Excerpt(quote)}"));
            }
        }
        return issues;
    }

    private static IReadOnlyList<string> BannedTerms(CharacterProject project)
    {
        var used = project.Profiles.SelectMany(p => p.Relationships).Where(r => r.Enabled)
            .SelectMany(r => new[] { r.SelfReference, r.AddresseeReference })
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ArchaicTerms.Where(t => !used.Contains(t)).ToArray();
    }

    private static string PreviousWord(string text, int index)
    {
        int end = index;
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        int start = end;
        while (start > 0 && (char.IsLetter(text[start - 1]) ||
                   char.GetUnicodeCategory(text[start - 1]) == System.Globalization.UnicodeCategory.NonSpacingMark)) start--;
        return text[start..end];
    }

    private static string Excerpt(string quote) => quote.Length <= 80 ? quote : quote[..77] + "…";
}
