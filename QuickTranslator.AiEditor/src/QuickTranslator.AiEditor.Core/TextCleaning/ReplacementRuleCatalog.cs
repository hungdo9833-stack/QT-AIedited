using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.TextCleaning;

public sealed class ReplacementRuleCatalog
{
    public IReadOnlyList<BatchReplacementRule> Rules { get; }
    public IReadOnlyList<ChineseMappingCandidate> Candidates { get; }

    private ReplacementRuleCatalog(IReadOnlyList<BatchReplacementRule> rules,
        IReadOnlyList<ChineseMappingCandidate> candidates)
    {
        Rules = rules;
        Candidates = candidates;
    }

    public static ReplacementRuleCatalog FromChapter(ChapterDocument chapter,
        IEnumerable<BatchReplacementRule>? suggestions = null)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var candidates = new List<ChineseMappingCandidate>();
        candidates.AddRange(chapter.ConfirmedTerms
            .Where(IsUsable)
            .Select(x => new ChineseMappingCandidate(x.Chinese, x.Vietnamese,
                ReplacementRuleSource.ConfirmedGlossary, true, false, "Glossary đã xác nhận")));
        candidates.AddRange(chapter.CharacterProject.Profiles
            .Where(x => !string.IsNullOrWhiteSpace(x.ChineseName) && !string.IsNullOrWhiteSpace(x.VietnameseName))
            .Select(x => new ChineseMappingCandidate(x.ChineseName.Trim(), x.VietnameseName.Trim(),
                ReplacementRuleSource.CharacterProfile, true, false, $"Hồ sơ nhân vật: {x.VietnameseName.Trim()}")));
        if (suggestions is not null)
            candidates.AddRange(suggestions.Where(IsUsable).Select(x => new ChineseMappingCandidate(
                x.Find.Trim(), x.Replacement.Trim(), x.Source, x.Confirmed, false,
                string.IsNullOrWhiteSpace(x.SourceLabel) ? SourceName(x.Source) : x.SourceLabel)));

        var marked = new List<ChineseMappingCandidate>();
        var rules = new List<BatchReplacementRule>();
        foreach (var group in candidates.GroupBy(x => x.Chinese, StringComparer.Ordinal))
        {
            var values = group.Select(x => x.Vietnamese).Distinct(StringComparer.Ordinal).ToArray();
            bool conflict = values.Length > 1;
            marked.AddRange(group.Select(x => x with { Conflict = conflict }));
            if (conflict) continue;
            var preferred = group.OrderByDescending(x => x.Confirmed)
                .ThenBy(x => x.Source == ReplacementRuleSource.CharacterProfile ? 0 : 1).First();
            rules.Add(new()
            {
                Find = group.Key,
                Replacement = values[0],
                Source = preferred.Source,
                Confirmed = preferred.Confirmed,
                SourceLabel = preferred.SourceLabel
            });
        }
        return new(rules.OrderByDescending(x => x.Find.Length).ThenBy(x => x.Find, StringComparer.Ordinal).ToArray(), marked);
    }

    public static IReadOnlyList<BatchReplacementRule> ParseManualRules(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        var rules = new List<BatchReplacementRule>();
        foreach (string raw in input.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            int separator = line.IndexOf("=>", StringComparison.Ordinal);
            int width = 2;
            if (separator < 0) { separator = line.IndexOf('='); width = 1; }
            if (separator <= 0) throw new ArgumentException($"Quy tắc không hợp lệ: {raw}. Dùng dạng chữ_cần_tìm => chữ_thay_thế.");
            string find = line[..separator].Trim();
            string replacement = line[(separator + width)..].Trim();
            if (find.Length == 0)
                throw new ArgumentException($"Quy tắc phải có nội dung cần tìm: {raw}.");
            rules.Add(new() { Find = find, Replacement = replacement, Source = ReplacementRuleSource.Manual,
                Confirmed = true, SourceLabel = "Quy tắc nhập trực tiếp" });
        }
        return rules;
    }

    private static bool IsUsable(ConfirmedTerm term) =>
        !string.IsNullOrWhiteSpace(term.Chinese) && !string.IsNullOrWhiteSpace(term.Vietnamese);
    private static bool IsUsable(BatchReplacementRule rule) =>
        !string.IsNullOrWhiteSpace(rule.Find) && !string.IsNullOrWhiteSpace(rule.Replacement);
    public static string SourceName(ReplacementRuleSource source) => source switch
    {
        ReplacementRuleSource.ConfirmedGlossary => "Glossary đã xác nhận",
        ReplacementRuleSource.CharacterProfile => "Hồ sơ nhân vật đã xác nhận",
        ReplacementRuleSource.TraditionalNames => "Gợi ý Names/Names2 truyền thống",
        ReplacementRuleSource.TraditionalHanViet => "Gợi ý Hán Việt truyền thống",
        _ => "Quy tắc nhập trực tiếp"
    };
}
