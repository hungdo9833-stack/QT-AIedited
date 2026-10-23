using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Tests.WebImport;

public sealed class WebImportQualityReportTests
{
    private static WebChapterContent Chapter(int index, string? title = null, string? text = null) =>
        new(new(index, title ?? $"第{index}章", new Uri($"https://quality.invalid/{index}?secret=private")),
            title ?? $"第{index}章", text ?? new string((char)('春' + index), 1200), 1);

    [Fact]
    public void Clean_book_and_empty_list_have_no_issues()
    {
        Assert.Empty(WebImportQualityReport.Analyze([]));
        Assert.Empty(WebImportQualityReport.Analyze(Enumerable.Range(1, 5).Select(i => Chapter(i)).ToArray()));
    }

    [Fact]
    public void Empty_and_short_outlier_are_separate_advisory_issues()
    {
        var chapters = Enumerable.Range(1, 7).Select(i => Chapter(i)).ToArray();
        chapters[1] = Chapter(2, text: " \r\n");
        chapters[2] = Chapter(3, text: "需要订阅");
        chapters[3] = Chapter(4, text: "????");
        var issues = WebImportQualityReport.Analyze(chapters);
        Assert.Equal(new[] { 2 }, Assert.Single(issues, i => i.Kind == WebImportQualityIssueKind.EmptyChapter).ChapterIndexes);
        Assert.Equal(new[] { 3, 4 }, Assert.Single(issues, i => i.Kind == WebImportQualityIssueKind.ShortChapter).ChapterIndexes);
    }

    [Theory]
    [InlineData(299, true)]
    [InlineData(300, false)]
    public void Short_chapter_requires_both_thresholds(int length, bool flagged)
    {
        var chapters = new[] { Chapter(1), Chapter(2), Chapter(3), Chapter(4, text: new string('字', length)) };
        Assert.Equal(flagged, WebImportQualityReport.Analyze(chapters).Any(i => i.Kind == WebImportQualityIssueKind.ShortChapter));
        Assert.DoesNotContain(WebImportQualityReport.Analyze([Chapter(1, text: "字"), Chapter(2, text: "文")]),
            i => i.Kind == WebImportQualityIssueKind.ShortChapter);
    }

    [Fact]
    public void Duplicate_content_hash_ignores_whitespace_unicode_width_and_chapter_headings()
    {
        var chapters = new[] { Chapter(1, text: "第1章 开始\n春天，风来了。１２３"),
            Chapter(2, text: "第2章 后来\r\n 春天， 风来了。123\n"), Chapter(3) };
        var issue = Assert.Single(WebImportQualityReport.Analyze(chapters), i => i.Kind == WebImportQualityIssueKind.DuplicateContent);
        Assert.Equal(new[] { 1, 2 }, issue.ChapterIndexes);
        Assert.DoesNotContain("春天", issue.Message);
        Assert.DoesNotContain("secret", issue.Message);
    }

    [Fact]
    public void Duplicate_titles_and_numbers_list_all_indexes()
    {
        var chapters = new[] { Chapter(4, "第七章 秘密标题"), Chapter(8, "第七章  秘密标题"), Chapter(9, "第八章") };
        var issues = WebImportQualityReport.Analyze(chapters);
        Assert.Equal(new[] { 4, 8 }, Assert.Single(issues, i => i.Kind == WebImportQualityIssueKind.DuplicateTitle).ChapterIndexes);
        Assert.Equal(new[] { 4, 8 }, Assert.Single(issues, i => i.Kind == WebImportQualityIssueKind.NumberingRepeat).ChapterIndexes);
        Assert.All(issues, i => Assert.DoesNotContain("秘密", i.Message));
    }

    [Theory]
    [InlineData("第十一章", "第十三章")]
    [InlineData("第２１章", "第廿三章")]
    [InlineData("第一百章", "第一百零二章")]
    public void Numbering_gaps_reuse_chinese_number_parser(string first, string second)
    {
        var issue = Assert.Single(WebImportQualityReport.Analyze([Chapter(10, first), Chapter(11, second)]));
        Assert.Equal(WebImportQualityIssueKind.NumberingGap, issue.Kind);
        Assert.Equal(new[] { 10, 11 }, issue.ChapterIndexes);
    }

    [Fact]
    public void Range_start_extras_and_consecutive_numbers_do_not_create_gaps()
    {
        Assert.Empty(WebImportQualityReport.Analyze([Chapter(10, "第十章"), Chapter(11, "番外"), Chapter(12, "第十一章")]));
    }

    [Fact]
    public void Block_mismatch_reports_corrected_and_uncorrected_chapters_together()
    {
        var corrected = Chapter(1) with { BlockSignatureDiffered = true, BlockSignatureCorrected = true };
        var uncorrected = Chapter(2) with { Link = Chapter(2).Link with { ExpectedBlockSignature = "expected" }, BlockSignature = "other" };
        var issue = Assert.Single(WebImportQualityReport.Analyze([corrected, uncorrected, Chapter(3)]));
        Assert.Equal(WebImportQualityIssueKind.BlockSignatureMismatch, issue.Kind);
        Assert.Equal(new[] { 1, 2 }, issue.ChapterIndexes);
        Assert.Equal(1, issue.CorrectedChapterCount);
        Assert.Contains("1/2", issue.Message);
    }
}
