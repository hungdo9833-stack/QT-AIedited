using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ImportPreviewTests
{
    [Fact]
    public void Preview_exposes_detection_issues_unclassified_regions_and_stable_ids()
    {
        const string source = "Lời nguồn giữ nguyên\r\n第1章 Mở đầu\r\n甲。\r\n第3章 Nhảy số\r\n乙。";
        ImmutableImportPlan detected = new ChapterDetector().Detect(source);

        ImportPreviewPlan first = ImportPreviewEditor.Create(detected).Current;
        ImportPreviewPlan second = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        Assert.Equal(source, first.ReconstructSource());
        first.ValidateExactCoverage();
        Assert.Contains(first.Items, item => item.Kind == PlannedRegionKind.Unclassified);
        Assert.Contains(first.Issues, issue => issue.Code == ImportPreviewIssueCode.NumberJump);
        Assert.Equal(first.Chapters.Select(x => x.ChapterId), second.Chapters.Select(x => x.ChapterId));
        Assert.All(first.Chapters, item => Assert.NotEqual(Guid.Empty, item.ChapterId));
    }

    [Fact]
    public void Edit_split_merge_include_boundary_and_undo_never_change_source_coverage()
    {
        const string source = "版权信息\n第1章 起点\n甲。\n乙。\n第2章 继续\n丙。\n丁。";
        var editor = ImportPreviewEditor.Create(new ChapterDetector().Detect(source));
        ImportPreviewItem first = editor.Current.Chapters.First(x => x.Number == 1);

        editor.UpdateChapter(first.Id, 10, "Tên đã sửa");
        Assert.Equal("Tên đã sửa", editor.Current.Chapters.First().Title);
        Assert.Equal(10, editor.Current.Chapters.First().Number);
        AssertExact(source, editor.Current);

        editor.SetIncluded(first.Id, false);
        Assert.False(editor.Current.Chapters.First().Included);
        editor.Undo();
        Assert.True(editor.Current.Chapters.First().Included);

        int splitAt = source.IndexOf("乙。", StringComparison.Ordinal);
        editor.Split(first.Id, splitAt, 11, "Nửa sau");
        Assert.Equal(3, editor.Current.Chapters.Count());
        AssertExact(source, editor.Current);

        editor.MergeWithNext(first.Id);
        Assert.Equal(2, editor.Current.Chapters.Count());
        AssertExact(source, editor.Current);

        int movedBoundary = source.IndexOf("丁。", StringComparison.Ordinal);
        editor.MoveBoundary(editor.Current.Chapters.First().Id, movedBoundary);
        AssertExact(source, editor.Current);
        Assert.Equal("丁。", editor.Current.Slice(editor.Current.Chapters.Last().ContentSpan));

        editor.Undo();
        AssertExact(source, editor.Current);
    }

    [Fact]
    public void Missing_duplicate_and_reversed_numbers_are_blocking_until_user_edits_them()
    {
        const string source = "序章\n序。\n第1章 一\n甲。\n第1章 重复\n乙。\n第0章 倒序\n丙。";
        var editor = ImportPreviewEditor.Create(new ChapterDetector().Detect(source));
        Assert.Contains(editor.Current.Issues, x => x.Code == ImportPreviewIssueCode.MissingNumber);
        Assert.Contains(editor.Current.Issues, x => x.Code == ImportPreviewIssueCode.DuplicateNumber);

        ImportPreviewItem special = editor.Current.Chapters.First(x => x.Kind == PlannedRegionKind.SpecialChapter);
        Assert.False(special.Included);
        editor.UpdateChapter(special.Id, 10, special.Title);
        editor.SetIncluded(special.Id, true);
        Assert.DoesNotContain(editor.Current.Issues.Where(x => x.ItemIds.Contains(special.Id)),
            x => x.Code == ImportPreviewIssueCode.MissingNumber);
        AssertExact(source, editor.Current);
    }

    [Fact]
    public void Split_requires_a_line_boundary_inside_content()
    {
        const string source = "第1章 一\n甲。\n乙。";
        var editor = ImportPreviewEditor.Create(new ChapterDetector().Detect(source));
        ImportPreviewItem chapter = Assert.Single(editor.Current.Chapters);
        Assert.Throws<InvalidOperationException>(() => editor.Split(chapter.Id, chapter.ContentSpan.StartUtf16 + 1, 2, "Sai"));
        Assert.Throws<InvalidOperationException>(() => editor.Split(chapter.Id, chapter.ContentSpan.StartUtf16, 2, "Sai"));
        AssertExact(source, editor.Current);
    }

    private static void AssertExact(string source, ImportPreviewPlan plan)
    {
        plan.ValidateExactCoverage();
        Assert.Equal(source, plan.ReconstructSource());
        Assert.Equal(0, plan.Items[0].Span.StartUtf16);
        Assert.Equal(source.Length, plan.Items[^1].Span.EndUtf16);
    }
}
