using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.WebImport;

/// <summary>Advisory checks only. Messages contain chapter indexes/counts, never source text or URLs.</summary>
public static class WebImportQualityReport
{
    public static IReadOnlyList<WebImportQualityIssue> Analyze(IReadOnlyList<WebChapterContent> chapters)
    {
        var issues = new List<WebImportQualityIssue>();
        if (chapters.Count == 0) return issues;
        var lengths = chapters.Select(c => WebPage.CjkCount(c.Text)).Order().ToArray();
        double median = lengths.Length % 2 == 1 ? lengths[lengths.Length / 2]
            : (lengths[lengths.Length / 2 - 1] + (double)lengths[lengths.Length / 2]) / 2;

        void Add(WebImportQualityIssueKind kind, IEnumerable<WebChapterContent> affected, string message, int corrected = 0)
        {
            var indexes = affected.Select(c => c.Link.Index).Distinct().ToArray();
            if (indexes.Length > 0) issues.Add(new(kind, indexes, $"Chương {string.Join(", ", indexes)}: {message}", corrected));
        }

        Add(WebImportQualityIssueKind.EmptyChapter, chapters.Where(c => string.IsNullOrWhiteSpace(c.Text)), "nội dung trống.");
        Add(WebImportQualityIssueKind.ShortChapter,
            chapters.Where(c => !string.IsNullOrWhiteSpace(c.Text) && WebPage.CjkCount(c.Text) < median * 0.25 && WebPage.CjkCount(c.Text) < 300),
            "quá ít chữ Hán so với độ dài trung vị; có thể là trang lỗi hoặc nội dung bị giới hạn.");
        foreach (var group in chapters.Where(c => !string.IsNullOrWhiteSpace(c.Text)).GroupBy(c => ContentHash(c.Text)).Where(g => g.Count() > 1))
            Add(WebImportQualityIssueKind.DuplicateContent, group, "nội dung trùng nhau.");
        foreach (var group in chapters.Where(c => !string.IsNullOrWhiteSpace(c.Title)).GroupBy(c => NormalizeForComparison(c.Title)).Where(g => g.Count() > 1))
            Add(WebImportQualityIssueKind.DuplicateTitle, group, "tiêu đề trùng nhau.");

        var numbered = chapters.Select(c => (Chapter: c, Number: ChapterListDetector.ChapterNumber(c.Title)))
            .Where(c => c.Number.HasValue).ToArray();
        foreach (var group in numbered.GroupBy(c => c.Number).Where(g => g.Count() > 1))
            Add(WebImportQualityIssueKind.NumberingRepeat, group.Select(c => c.Chapter), "số chương lặp lại.");
        var gaps = numbered.Zip(numbered.Skip(1)).Where(p => p.Second.Number > p.First.Number + 1).ToArray();
        Add(WebImportQualityIssueKind.NumberingGap, gaps.SelectMany(p => new[] { p.First.Chapter, p.Second.Chapter }),
            "có khoảng trống trong số chương giữa các vị trí này.");

        var differed = chapters.Where(c => c.BlockSignatureDiffered ||
            c.Link.ExpectedBlockSignature is not null && c.BlockSignature is not null && c.BlockSignature != c.Link.ExpectedBlockSignature).ToArray();
        int correctedCount = differed.Count(c => c.BlockSignatureCorrected);
        Add(WebImportQualityIssueKind.BlockSignatureMismatch, differed,
            $"khối nội dung khác mẫu; đã sửa {correctedCount}/{differed.Length} chương theo mẫu.", correctedCount);
        return issues;
    }

    internal static string NormalizeForComparison(string text) =>
        Regex.Replace(text.Normalize(NormalizationForm.FormKC), @"\s+", "");

    internal static string ContentHash(string text)
    {
        // The same body under different 第N章 headings is still a duplicate chapter.
        string normalized = WebPage.Normalize(text);
        int end = normalized.IndexOf('\n');
        if (end >= 0 && Regex.IsMatch(normalized[..end], @"^\s*第") && ChapterListDetector.HasChapterNumber(normalized[..end]))
            normalized = normalized[(end + 1)..];
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeForComparison(normalized))));
    }
}
