using System.Text;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.TextCleaning;

public sealed class ChineseTextScanner
{
    public ChineseTextScanReport Scan(ChapterDocument chapter, DateTimeOffset? createdAt = null) =>
        Scan([chapter], chapter.CharacterProject.Id, createdAt);

    public ChineseTextScanReport Scan(IEnumerable<ChapterDocument> chapters, Guid characterProjectId,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        var chapterList = chapters.ToArray();
        if (chapterList.Any(x => x.CharacterProject.Id != characterProjectId))
            throw new InvalidOperationException("Không thể quét lẫn chương thuộc bộ truyện/dự án khác.");
        var occurrences = new List<ChineseTextOccurrence>();
        foreach (var chapter in chapterList)
        {
            var catalog = ReplacementRuleCatalog.FromChapter(chapter);
            var ignored = chapter.TextCleaner.IgnoredChineseText.ToHashSet(StringComparer.Ordinal);
            foreach (var segment in chapter.Segments.OrderBy(x => x.Order))
                ScanSegment(chapter, segment, catalog, ignored, occurrences);
        }
        var groups = occurrences.GroupBy(x => x.Text, StringComparer.Ordinal)
            .Select(group => new ChineseTextGroup(group.Key, MostImportant(group.Select(x => x.Classification)),
                group.Count(), group.SelectMany(x => x.Candidates).Distinct().ToArray(), group.ToArray()))
            .OrderByDescending(x => x.Count).ThenBy(x => x.Text, StringComparer.Ordinal).ToArray();
        return new(characterProjectId, createdAt ?? DateTimeOffset.UtcNow, occurrences, groups);
    }

    public static bool ContainsHan(string text) => EnumerateClusters(text).Any();

    public static IReadOnlyList<(int StartUtf16, int LengthUtf16, string Text)> FindClusters(string text) =>
        EnumerateClusters(text).ToArray();

    private static void ScanSegment(ChapterDocument chapter, TranslationSegment segment,
        ReplacementRuleCatalog catalog, HashSet<string> ignored, List<ChineseTextOccurrence> output)
    {
        string text = segment.EditableText;
        foreach (var cluster in EnumerateClusters(text))
        {
            var candidates = catalog.Candidates.Where(x => cluster.Text.Contains(x.Chinese, StringComparison.Ordinal))
                .Distinct().ToArray();
            ChineseTextClassification classification;
            if (ignored.Contains(cluster.Text)) classification = ChineseTextClassification.Ignored;
            else if (candidates.Any(x => x.Conflict)) classification = ChineseTextClassification.Conflict;
            else if (candidates.Any(x => x.Source == ReplacementRuleSource.CharacterProfile &&
                                         string.Equals(x.Chinese, cluster.Text, StringComparison.Ordinal)))
                classification = ChineseTextClassification.ConfirmedCharacter;
            else if (candidates.Any(x => x.Confirmed && string.Equals(x.Chinese, cluster.Text, StringComparison.Ordinal)))
                classification = ChineseTextClassification.ConfirmedGlossary;
            else classification = ChineseTextClassification.Unknown;
            output.Add(new(Guid.NewGuid(), chapter.Id, chapter.Title, segment.Id, segment.Order,
                cluster.StartUtf16, cluster.LengthUtf16, cluster.Text,
                SentenceAround(text, cluster.StartUtf16, cluster.LengthUtf16), classification,
                candidates, segment.IsProtectedFromAutomaticTranslation));
        }
    }

    private static IEnumerable<(int StartUtf16, int LengthUtf16, string Text)> EnumerateClusters(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        int clusterStart = -1, clusterEnd = -1, offset = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            int length = rune.Utf16SequenceLength;
            if (IsHan(rune.Value))
            {
                if (clusterStart < 0) clusterStart = offset;
                clusterEnd = offset + length;
            }
            else if (clusterStart >= 0)
            {
                yield return (clusterStart, clusterEnd - clusterStart, text[clusterStart..clusterEnd]);
                clusterStart = clusterEnd = -1;
            }
            offset += length;
        }
        if (clusterStart >= 0) yield return (clusterStart, clusterEnd - clusterStart, text[clusterStart..clusterEnd]);
    }

    private static bool IsHan(int value) =>
        value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or >= 0xF900 and <= 0xFAFF or
            >= 0x20000 and <= 0x2A6DF or >= 0x2A700 and <= 0x2B73F or >= 0x2B740 and <= 0x2B81F or
            >= 0x2B820 and <= 0x2CEAF or >= 0x2CEB0 and <= 0x2EBEF or >= 0x2EBF0 and <= 0x2EE5F or
            >= 0x2F800 and <= 0x2FA1F or >= 0x30000 and <= 0x3134F or >= 0x31350 and <= 0x323AF;

    internal static string SentenceAround(string text, int start, int length)
    {
        int from = start;
        while (from > 0 && !IsBoundary(text[from - 1])) from--;
        int to = start + length;
        while (to < text.Length && !IsBoundary(text[to])) to++;
        if (to < text.Length) to++;
        const int limit = 180;
        if (start - from > limit) from = start - limit;
        if (to - (start + length) > limit) to = start + length + limit;
        return text[from..to].Trim();
    }

    private static bool IsBoundary(char value) => value is '\r' or '\n' or '.' or '!' or '?' or '。' or '！' or '？' or '…';
    private static ChineseTextClassification MostImportant(IEnumerable<ChineseTextClassification> values) =>
        values.OrderBy(x => x switch
        {
            ChineseTextClassification.Conflict => 0,
            ChineseTextClassification.Unknown => 1,
            ChineseTextClassification.ConfirmedCharacter => 2,
            ChineseTextClassification.ConfirmedGlossary => 3,
            _ => 4
        }).First();
}
