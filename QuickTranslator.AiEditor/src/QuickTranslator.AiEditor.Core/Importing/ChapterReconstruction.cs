using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Importing;

public enum ChapterDuplicateKind
{
    None,
    ExactDuplicate,
    ContainedDuplicate,
    PartialOverlap,
    Continuation,
    ConflictingVersion
}

public sealed record ChapterDuplicateEvidence(
    Guid FirstItemId,
    Guid SecondItemId,
    ChapterDuplicateKind Kind,
    string FirstSha256,
    string SecondSha256,
    int OverlapUtf16,
    bool IsAutomaticallyVerifiable,
    string Reason);

public static class ChapterReconstructionAnalyzer
{
    public static ImmutableArray<ChapterDuplicateEvidence> Analyze(RawSourceSnapshot source,
        ImmutableArray<ImportPreviewItem> items)
    {
        var evidence = ImmutableArray.CreateBuilder<ChapterDuplicateEvidence>();
        foreach (IGrouping<(string Volume, int Number), ImportPreviewItem> group in items
                     .Where(x => x.IsChapter && x.Number is > 0)
                     .GroupBy(x => (x.VolumeTitle ?? "", x.Number!.Value)))
        {
            ImportPreviewItem[] values = group.OrderBy(x => x.Span.StartUtf16).ToArray();
            if (values.Length < 2) continue;
            for (int left = 0; left < values.Length - 1; left++)
            for (int right = left + 1; right < values.Length; right++)
                evidence.Add(Compare(source, values[left], values[right]));
        }
        return evidence.ToImmutable();
    }

    private static ChapterDuplicateEvidence Compare(RawSourceSnapshot source, ImportPreviewItem first,
        ImportPreviewItem second)
    {
        string left = source.Text.Substring(first.ContentSpan.StartUtf16, first.ContentSpan.LengthUtf16);
        string right = source.Text.Substring(second.ContentSpan.StartUtf16, second.ContentSpan.LengthUtf16);
        string leftHash = Hash(left), rightHash = Hash(right);
        if (string.Equals(left, right, StringComparison.Ordinal))
            return Result(ChapterDuplicateKind.ExactDuplicate, left.Length, true,
                "SHA-256 và nguyên văn UTF-16 giống hoàn toàn.");

        if (left.Length > 0 && right.Length > 0 &&
            (left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal)))
            return Result(ChapterDuplicateKind.ContainedDuplicate, Math.Min(left.Length, right.Length), true,
                "Một bản nguyên văn nằm trọn trong bản còn lại; chưa tự loại vì có thể là phiên bản rút gọn.");

        int forward = LongestSuffixPrefix(left, right);
        int reverse = LongestSuffixPrefix(right, left);
        int overlap = Math.Max(forward, reverse);
        int meaningful = Math.Max(4, Math.Min(left.Length, right.Length) / 20);
        if (overlap >= meaningful)
            return Result(ChapterDuplicateKind.PartialOverlap, overlap, false,
                $"Phát hiện giao nhau nguyên văn {overlap:N0} UTF-16 ở cuối/đầu hai bản.");

        bool adjacent = first.Span.EndUtf16 == second.Span.StartUtf16;
        bool continuationMarker = HasContinuationMarker(first.Title) || HasContinuationMarker(second.Title);
        if (adjacent && continuationMarker)
            return Result(ChapterDuplicateKind.Continuation, 0, false,
                "Hai phần liền kề vật lý và tiêu đề có tín hiệu tiếp nối; cần xác nhận trước khi ghép.");

        return Result(ChapterDuplicateKind.ConflictingVersion, 0, false,
            "Cùng số chương/quyển nhưng nội dung khác và chưa chứng minh được quan hệ.");

        ChapterDuplicateEvidence Result(ChapterDuplicateKind kind, int overlap, bool verified, string reason) =>
            new(first.Id, second.Id, kind, leftHash, rightHash, overlap, verified, reason);
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // KMP prefix function: longest prefix of right that is also a suffix of left, O(n + m).
    private static int LongestSuffixPrefix(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0) return 0;
        string combined = right + '\0' + left;
        int[] prefix = new int[combined.Length];
        for (int index = 1; index < combined.Length; index++)
        {
            int matched = prefix[index - 1];
            while (matched > 0 && combined[index] != combined[matched]) matched = prefix[matched - 1];
            if (combined[index] == combined[matched]) matched++;
            prefix[index] = Math.Min(matched, right.Length);
        }
        return prefix[^1];
    }

    private static bool HasContinuationMarker(string title) =>
        title.Contains('上') || title.Contains('下') || title.Contains('续') || title.Contains('續') ||
        title.Contains("前篇", StringComparison.Ordinal) || title.Contains("后篇", StringComparison.Ordinal) ||
        title.Contains("後篇", StringComparison.Ordinal);
}
