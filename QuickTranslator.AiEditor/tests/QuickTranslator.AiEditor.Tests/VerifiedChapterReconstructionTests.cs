using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Tests;

public sealed class VerifiedChapterReconstructionTests
{
    [Fact]
    public void Physical_boundaries_are_cut_by_offsets_before_logical_sort()
    {
        const string source = "第3章 三\n内容C\n第1章 一\n内容A\n第4章 四\n内容D\n第2章 二\n内容B";
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;

        Assert.Equal([3, 1, 4, 2], plan.Chapters.Select(x => x.Number));
        Assert.Equal([1, 2, 3, 4], plan.ChaptersInImportOrder.Select(x => x.Number));
        Assert.Equal("内容C\n", plan.Slice(plan.Chapters.First(x => x.Number == 3).ContentSpan));
        Assert.Equal("内容A\n", plan.Slice(plan.Chapters.First(x => x.Number == 1).ContentSpan));
        Assert.Equal("内容D\n", plan.Slice(plan.Chapters.First(x => x.Number == 4).ContentSpan));
        Assert.Equal("内容B", plan.Slice(plan.Chapters.First(x => x.Number == 2).ContentSpan));
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public void Rejecting_false_heading_removes_boundary_but_preserves_heading_and_body_verbatim()
    {
        const string source = "第1章 开始\n内容A\n第2章 人物提到的故事\n内容B\n第2章 真正章节\n内容C";
        var editor = ImportPreviewEditor.Create(new ChapterDetector().Detect(source));
        ImportPreviewItem falseHeading = editor.Current.Chapters.ElementAt(1);

        editor.RejectFalseBoundary(falseHeading.Id);

        ImportPreviewPlan plan = editor.Current;
        Assert.Equal(2, plan.Chapters.Count());
        Assert.Contains("第2章 人物提到的故事\n内容B\n", plan.Slice(plan.Chapters.First().ContentSpan), StringComparison.Ordinal);
        Assert.Equal(source, plan.ReconstructSource());
    }

    [Fact]
    public void Aggregate_volume_range_is_not_classified_as_a_chapter()
    {
        const string source = "【第二部（11-20）】\n第11章 十一\n正文。\n第12章 十二\n正文。";
        ImmutableImportPlan detected = new ChapterDetector().Detect(source);

        Assert.Equal(2, detected.Chapters.Count());
        Assert.Contains(detected.Structure, x => x.Kind == SourceStructureKind.AggregatePost);
        Assert.Contains(detected.Issues, x => x.Code == DetectionIssueCode.AggregateChapterGroup);
        Assert.Equal(source, detected.ReconstructSource());
    }
}
