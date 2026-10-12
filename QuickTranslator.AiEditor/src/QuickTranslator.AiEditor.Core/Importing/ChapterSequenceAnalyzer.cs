using System.Collections.Immutable;

namespace QuickTranslator.AiEditor.Core.Importing;

internal static class ChapterSequenceAnalyzer
{
    public static ImmutableArray<DetectionIssue> Analyze(ImmutableArray<HeadingCandidate> candidates, int sourceLength)
    {
        var issues = ImmutableArray.CreateBuilder<DetectionIssue>();
        int? previous = null;
        var seen = new HashSet<int>();
        bool hasChapter = false;

        for (int index = 0; index < candidates.Length; index++)
        {
            HeadingCandidate candidate = candidates[index];
            if (candidate.Decision == CandidateDecision.Rejected) continue;
            if (candidate.Decision == CandidateDecision.NeedsReview)
            {
                issues.Add(Issue(DetectionIssueCode.AmbiguousHeading, DetectionIssueSeverity.Warning, candidate,
                    "Dòng giống tiêu đề nhưng chưa đủ chắc chắn; cần xác nhận trong Import Preview.", index));
                if (candidate.Kind is ChapterHeadingKind.Chapter or ChapterHeadingKind.SpecialChapter) hasChapter = true;
                continue;
            }
            if (candidate.Kind == ChapterHeadingKind.Volume)
            {
                previous = null;
                seen.Clear();
                continue;
            }
            if (candidate.Kind == ChapterHeadingKind.SpecialChapter) { hasChapter = true; continue; }
            hasChapter = true;
            if (candidate.Number is not int number) continue;

            if (seen.Contains(number))
                issues.Add(Issue(DetectionIssueCode.DuplicateChapterNumber, DetectionIssueSeverity.Warning, candidate,
                    $"Số chương {number} xuất hiện nhiều lần trong cùng quyển.", index));
            if (previous is int prior)
            {
                if (number > prior + 1)
                {
                    string missing = number == prior + 2 ? (prior + 1).ToString() : $"{prior + 1}–{number - 1}";
                    issues.Add(Issue(DetectionIssueCode.MissingChapterNumbers, DetectionIssueSeverity.Warning, candidate,
                        $"Thiếu số chương {missing} trước tiêu đề này.", index));
                    issues.Add(Issue(DetectionIssueCode.NumberJump, DetectionIssueSeverity.Information, candidate,
                        $"Trình tự nhảy từ chương {prior} lên {number}.", index));
                }
                else if (number < prior)
                    issues.Add(Issue(DetectionIssueCode.ReversedChapterNumber, DetectionIssueSeverity.Warning, candidate,
                        $"Số chương giảm từ {prior} xuống {number} trong cùng quyển.", index));
            }
            seen.Add(number);
            previous = number;
        }

        if (!hasChapter && sourceLength > 0)
            issues.Add(new(DetectionIssueCode.NoChapterHeadings, DetectionIssueSeverity.Warning,
                new(0, sourceLength), "Không nhận diện được tiêu đề chương nào; toàn bộ nguồn được giữ dưới dạng chưa phân loại.", []));
        return issues.ToImmutable();
    }

    private static DetectionIssue Issue(DetectionIssueCode code, DetectionIssueSeverity severity,
        HeadingCandidate candidate, string message, int index) =>
        new(code, severity, candidate.HeadingSpan, message, [index]);
}
