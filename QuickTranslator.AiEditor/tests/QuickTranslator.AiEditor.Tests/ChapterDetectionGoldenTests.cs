using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChapterDetectionGoldenTests
{
    public static IEnumerable<object?[]> GoldenHeadings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "ChapterDetection", "golden-headings.tsv");
        foreach (string line in File.ReadLines(path))
        {
            string[] fields = line.Split('\t');
            yield return [Enum.Parse<ChapterHeadingKind>(fields[0]), string.IsNullOrEmpty(fields[1]) ? null : int.Parse(fields[1]), fields[2]];
        }
    }

    [Theory]
    [MemberData(nameof(GoldenHeadings))]
    public void Recognizes_real_world_heading_forms(ChapterHeadingKind kind, int? number, string heading)
    {
        ImmutableImportPlan plan = new ChapterDetector().Detect(heading + "\r\n正文。😀\r\n");
        HeadingCandidate candidate = Assert.Single(plan.Candidates.Where(x => x.Decision != CandidateDecision.Rejected));
        Assert.Equal(kind, candidate.Kind);
        Assert.Equal(number, candidate.Number);
        Assert.Equal(heading, candidate.RawHeading);
        Assert.NotEmpty(candidate.Recognizer);
        Assert.NotEmpty(candidate.Evidence);
        Assert.InRange(candidate.Confidence, 0, 1);
    }

    [Fact]
    public void Supports_volume_resets_without_cross_volume_sequence_errors()
    {
        const string source = "第一卷 山门\n第一章 初见\n甲。\n第二章 入门\n乙。\n第二卷 中州\n第一章 重逢\n丙。\n第二章 风云\n丁。";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(new int?[] { 1, 2, 1, 2 }, plan.Candidates.Where(x => x.Kind == ChapterHeadingKind.Chapter && x.Decision != CandidateDecision.Rejected).Select(x => x.Number));
        Assert.DoesNotContain(plan.Issues, x => x.Code is DetectionIssueCode.DuplicateChapterNumber or DetectionIssueCode.ReversedChapterNumber);
        Assert.Equal(source, plan.ReconstructSource());
        Assert.Equal("山门", plan.Chapters.ElementAt(0).VolumeTitle);
        Assert.Equal("中州", plan.Chapters.ElementAt(2).VolumeTitle);
    }

    [Fact]
    public void Reports_gap_duplicate_and_reversed_numbers_without_renumbering()
    {
        const string source = "第1章 A\n正文\n第3章 B\n正文\n第3章 C\n正文\n第2章 D\n正文";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(new int?[] { 1, 3, 3, 2 }, plan.Candidates.Where(x => x.Decision != CandidateDecision.Rejected).Select(x => x.Number));
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.MissingChapterNumbers);
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.NumberJump);
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.DuplicateChapterNumber);
        Assert.Contains(plan.Issues, x => x.Code == DetectionIssueCode.ReversedChapterNumber);
    }

    [Fact]
    public void Rejects_body_lines_that_only_resemble_headings()
    {
        const string source = "第一章 起点\n他说：“第二章就要开始了。”\n第三章的内容让他吃惊。\n她在纸上写着第九章 风雪。\n第二章 再见\n正文。";
        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        Assert.Equal(new int?[] { 1, 2 }, plan.Candidates.Where(x => x.Kind == ChapterHeadingKind.Chapter && x.Decision != CandidateDecision.Rejected).Select(x => x.Number));
        Assert.Contains(plan.Candidates, x => x.RawHeading.StartsWith("第三章的内容", StringComparison.Ordinal) && x.Decision == CandidateDecision.Rejected);
        Assert.Equal(2, plan.Chapters.Count());
    }

    [Fact]
    public void Rejects_lesson_round_and_inline_prose_that_begin_with_ordinals()
    {
        const string source = "第一章 真标题\n正文。\n第二节课开始了。\n第一回合我们输了。\n第三章他回到了家，发现门已经开了。\n第二章 结束\n正文。";

        ImmutableImportPlan plan = new ChapterDetector().Detect(source);

        Assert.Equal(new int?[] { 1, 2 }, plan.Chapters.Select(x => x.ChapterNumber));
        Assert.All(plan.Candidates.Where(x => x.RawHeading is "第二节课开始了。" or "第一回合我们输了。" or
            "第三章他回到了家，发现门已经开了。"), candidate =>
            Assert.Equal(CandidateDecision.Rejected, candidate.Decision));
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public void Keeps_special_unnumbered_chapters_and_allows_number_reset_per_volume()
    {
        const string source = "第一卷 山门\n序章\n序幕正文。\n第一章 初见\n甲。\n第二卷 中州\n楔子\n过渡正文。\n第一章 重逢\n乙。\n番外 婚礼\n丙。";

        ImmutableImportPlan plan = new ChapterDetector().Detect(source);
        PlannedSourceRegion[] chapters = plan.Chapters.ToArray();

        Assert.Equal(5, chapters.Length);
        Assert.Equal(new int?[] { null, 1, null, 1, null }, chapters.Select(x => x.ChapterNumber));
        Assert.Equal(new[] { "山门", "山门", "中州", "中州", "中州" }, chapters.Select(x => x.VolumeTitle));
        Assert.DoesNotContain(plan.Issues, x => x.Code is DetectionIssueCode.DuplicateChapterNumber or
            DetectionIssueCode.ReversedChapterNumber);
        Assert.Equal(source, plan.ReconstructSource());
    }
}
