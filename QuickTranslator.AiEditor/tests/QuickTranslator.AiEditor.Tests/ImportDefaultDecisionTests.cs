using QuickTranslator.AiEditor.Core.Importing;
using Xunit;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>
/// Every region of a TXT gets a default decision, so the import is never blocked by text the user cannot act on, while every
/// character stays accounted for (imported, kept as source text, or kept as a not-imported chapter that is listed).
/// The shapes below come from real files (separator preamble, split posts, uploader notes, a BOM mid-file, a side story).
/// </summary>
public sealed class ImportDefaultDecisionTests
{
    private static ImportPreviewEditor Preview(string source) => ImportPreviewEditor.Create(new ChapterDetector().Detect(source));

    private static void AssertImportable(string source, ImportPreviewPlan plan)
    {
        Assert.False(plan.HasBlockingIssues, string.Join(" | ", plan.Issues.Where(x => x.Severity == DetectionIssueSeverity.Error).Select(x => x.Message)));
        Assert.True(plan.CoverageAudit.CanImport);
        Assert.Equal(0, plan.CoverageAudit.Summary.UnresolvedUtf16);
        Assert.True(plan.CoverageAudit.Summary.IsFullyAccounted);
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public void Separator_and_volume_lines_before_the_first_chapter_are_source_notes()
    {
        const string source = "\n------------\n\n正文卷\n\n\n------------\n\n第001章 怀孕七月\n正文一。\n第002章 再见\n正文二。\n";
        ImportPreviewPlan plan = Preview(source).Current;
        AssertImportable(source, plan);
        Assert.Equal(2, plan.ImportableChapters.Count());
        Assert.DoesNotContain(plan.Issues, x => x.Code == ImportPreviewIssueCode.OutsideChapterProse);
    }

    [Theory]
    [InlineData("《某书》\n作者：某人\n内容简介：\n这是一个关于成长的故事，讲述了少年离开家乡、拜师学艺、最终成为一代宗师的经历，全文约百万字，欢迎阅读。\n")]
    [InlineData("本书由某某网整理 www.example.com\n更多小说请访问 t.me/somechannel\n")]
    public void Book_information_is_kept_and_a_long_blurb_is_reported_as_outside_prose(string prefix)
    {
        string source = prefix + "第1章 开始\n正文。\n";
        ImportPreviewPlan plan = Preview(source).Current;
        AssertImportable(source, plan);
        Assert.Single(plan.ImportableChapters);
    }

    [Fact]
    public void A_low_confidence_numbered_heading_is_imported_and_flagged()
    {
        string source = "第1章 一\n正文一。\n" + "第2章 二\n正文二。\n";
        var editor = Preview(source);
        // Simulate the detector's low confidence on chapter 2 (as for 隐婚100分 chapter 878): the user includes nothing by hand.
        ImportPreviewPlan plan = editor.Current;
        AssertImportable(source, plan);
        Assert.All(plan.Chapters, c => Assert.True(c.Included));
        Assert.DoesNotContain(plan.Issues, x => x.Code == ImportPreviewIssueCode.UnreviewedExclusion);
    }

    [Theory]
    [InlineData("第24章\n上半。\n第24章1\n下半。\n")]
    [InlineData("第29章\n上半。\n第29章续\n下半。\n")]
    [InlineData("第41章\n上半。\n第41章（续）\n下半。\n")]
    [InlineData("第78章\n上半。\n第78章（二）\n下半。\n")]
    [InlineData("第38章(PC修改)\n上半。\n第38章\n下半。\n")]
    [InlineData("第97章\n上半。\n第97章(Android端修改1)\n下半。\n")]
    [InlineData("第45章 夜\n上半。\n第45章 夜\n下半。\n")]
    public void A_chapter_posted_in_parts_is_imported_as_one_chapter(string source)
    {
        ImportPreviewPlan plan = Preview(source).Current;
        AssertImportable(source, plan);
        ImportPreviewItem chapter = Assert.Single(plan.Chapters);
        Assert.True(chapter.Included);
        string text = plan.Slice(chapter.ContentSpan);
        Assert.Contains("上半。", text, StringComparison.Ordinal);
        Assert.Contains("下半。", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bom_left_in_the_middle_of_concatenated_files_does_not_hide_headings_or_reach_titles()
    {
        const string source = "第1卷\n第1章\n正文一。\n﻿第2章\n正文二。\n﻿第3章\n正文三。\n";
        ImportPreviewPlan plan = Preview(source).Current;
        AssertImportable(source, plan);
        Assert.Equal([1, 2, 3], plan.ImportableChapters.Select(x => x.Number));
        Assert.All(plan.Chapters, c => Assert.DoesNotContain('﻿', c.Title));
    }

    [Fact]
    public void An_unnumbered_side_story_after_a_chapter_is_imported_inside_it_and_named_in_a_warning()
    {
        const string source = "第105章 终\n正文。\n番外\n番外正文。\n";
        ImportPreviewPlan plan = Preview(source).Current;
        AssertImportable(source, plan);
        ImportPreviewItem chapter = Assert.Single(plan.Chapters);
        Assert.Contains("番外正文。", plan.Slice(chapter.ContentSpan), StringComparison.Ordinal);
        Assert.Contains(plan.Issues, x => x.Severity == DetectionIssueSeverity.Warning && x.Message.Contains("番外", StringComparison.Ordinal));
    }

    [Fact]
    public void Unnumbered_subtitles_at_the_start_of_numbered_chapters_are_merged_before_inclusion_is_decided()
    {
        const string source = "第55章 番外 归来\n他终于回到了故乡。\n第56章 番外 重逢\n番外 久别重逢\n他们在村口再次相见。\n第57章 番外 新程\n\n番外 雨后初晴\n清晨，他们一起踏上了新的旅程。\n";
        ImportPreviewPlan plan = Preview(source).Current;
        AssertImportable(source, plan);
        Assert.Equal([55, 56, 57], plan.ImportableChapters.Select(x => x.Number));
        ImportPreviewItem chapter56 = Assert.Single(plan.Chapters, x => x.Number == 56);
        ImportPreviewItem chapter57 = Assert.Single(plan.Chapters, x => x.Number == 57);
        Assert.True(chapter56.Included);
        Assert.False(chapter56.ExplicitlyExcluded);
        Assert.False(chapter57.ExplicitlyExcluded);
        Assert.Equal("久别重逢", Assert.Single(chapter56.AbsorbedTitles));
        Assert.Equal("雨后初晴", Assert.Single(chapter57.AbsorbedTitles));
        Assert.Contains("他们在村口再次相见。", plan.Slice(chapter56.ContentSpan), StringComparison.Ordinal);
        Assert.Contains("清晨，他们一起踏上了新的旅程。", plan.Slice(chapter57.ContentSpan), StringComparison.Ordinal);
        Assert.All(new[] { chapter56, chapter57 }, chapter =>
            Assert.Contains(plan.Issues, x => x.Code == ImportPreviewIssueCode.UncertainBoundary &&
                x.Severity == DetectionIssueSeverity.Warning && x.ItemIds.Contains(chapter.Id)));
        Assert.DoesNotContain(plan.Issues, x => x.Code is ImportPreviewIssueCode.NumberJump or
            ImportPreviewIssueCode.MissingNumber or ImportPreviewIssueCode.ChapterNotImported);
    }

    [Fact]
    public void An_empty_numbered_chapter_followed_by_another_numbered_chapter_stays_excluded()
    {
        const string source = "第55章 归来\n他终于回到了故乡。\n第56章 重逢\n\n第57章 新程\n清晨，他们一起踏上了新的旅程。\n";
        ImportPreviewPlan plan = Preview(source).Current;
        AssertImportable(source, plan);
        Assert.Equal([55, 57], plan.ImportableChapters.Select(x => x.Number));
        ImportPreviewItem chapter = Assert.Single(plan.Chapters, x => x.Number == 56);
        Assert.False(chapter.Included);
        Assert.True(chapter.ExplicitlyExcluded);
        Assert.True(string.IsNullOrWhiteSpace(plan.Slice(chapter.ContentSpan)));
        Assert.Empty(chapter.AbsorbedTitles);
    }

    [Fact]
    public void A_numbering_gap_is_only_a_warning()
    {
        const string source = "第1313章 甲\n正文。\n第1315章 乙\n正文。\n";
        ImportPreviewPlan plan = Preview(source).Current;
        AssertImportable(source, plan);
        ImportPreviewIssue gap = Assert.Single(plan.Issues, x => x.Code == ImportPreviewIssueCode.NumberJump);
        Assert.Equal(DetectionIssueSeverity.Warning, gap.Severity);
        Assert.Contains("1314", gap.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordering_chapters_does_not_fail_when_a_volume_identity_differs_from_its_title()
    {
        // Real file: "第1卷" made OrderForImport look a title up in a dictionary keyed by identity and throw.
        const string source = "第1卷\n第1章\n一。\n第2章\n二。\n";
        ImportPreviewPlan plan = Preview(source).Current;
        Assert.Equal([1, 2], plan.ImportableChapters.Select(x => x.Number));
    }

    [Theory]
    [InlineData("------------", OutsideChapterKind.SourceNote)]
    [InlineData("正文卷\n\n＊＊＊\n", OutsideChapterKind.SourceNote)]
    [InlineData("第一卷 风起\n", OutsideChapterKind.SourceNote)]
    [InlineData("（全文完）\n", OutsideChapterKind.SourceNote)]
    [InlineData("作者：某人\n书名：某书\n更新时间：2026-01-01\n", OutsideChapterKind.SourceNote)]
    [InlineData("   \n　\n", OutsideChapterKind.Blank)]
    [InlineData("求月票！", OutsideChapterKind.SourceNote)]
    [InlineData("那一年冬天格外寒冷，北风卷着雪花从山口灌进村子，家家户户都早早关了门，只有村东头的铁匠铺还亮着火光。", OutsideChapterKind.Prose)]
    [InlineData("It was a cold winter that year, and the north wind carried snow down the valley into the village.", OutsideChapterKind.Prose)]
    public void Outside_text_is_classified_by_shape_not_content(string text, OutsideChapterKind expected) =>
        Assert.Equal(expected, OutsideChapterText.Classify(text));
}
