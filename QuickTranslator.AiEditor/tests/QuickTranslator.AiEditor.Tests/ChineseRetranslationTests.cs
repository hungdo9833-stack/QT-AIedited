using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChineseRetranslationTests
{
    [Fact]
    public void Residual_mode_previews_and_replaces_only_the_selected_han_cluster()
    {
        ChapterDocument chapter = Chapter("Anh ấy nói 老公, 上次在酒店 rồi rời đi.");
        ChineseTextOccurrence occurrence = Scan(chapter).Single(item => item.Text == "老公");

        BatchReplacementPreviewItem preview = ChineseRetranslationPreviewBuilder.Build(chapter, occurrence,
            ChineseRetranslationMode.ResidualHanOnly, ChineseRetranslationMethod.VietPhrase, "chồng");

        Assert.Equal("老公", preview.Original);
        Assert.Equal("chồng", preview.Replacement);
        Assert.Equal("Anh ấy nói chồng, 上次在酒店 rồi rời đi.",
            BatchReplacementEngine.Apply(chapter.Segments[0].EditableText, [preview]));
        Assert.Equal("Anh ấy nói 老公, 上次在酒店 rồi rời đi.", chapter.Segments[0].EditableText);
    }

    [Fact]
    public void Entire_segment_mode_uses_the_whole_editable_text_and_preserves_history_on_apply()
    {
        ChapterDocument chapter = Chapter("Bản cũ còn 老公.");
        TranslationSegment segment = chapter.Segments[0];
        segment.UserDraft = "Bản cũ còn 老公.";
        segment.DraftRevision = 2;
        segment.Approvals.Add(new() { Text = "Bản đã duyệt cũ.", DraftRevision = 1,
            ApprovedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        ChineseTextOccurrence occurrence = Assert.Single(Scan(chapter));

        BatchReplacementPreviewItem preview = ChineseRetranslationPreviewBuilder.Build(chapter, occurrence,
            ChineseRetranslationMode.EntireSegment, ChineseRetranslationMethod.Ollama,
            "Bản dịch lại hoàn chỉnh.");
        BatchReplacementEngine.ApplyToChapter(chapter, [preview], true,
            BatchReplacementScope.CurrentChapter, Guid.NewGuid(), DateTimeOffset.UtcNow);

        Assert.Equal("Bản dịch lại hoàn chỉnh.", segment.UserDraft);
        Assert.Contains(segment.DraftHistory, item => item.Text == "Bản cũ còn 老公.");
        Assert.Single(segment.Approvals);
        Assert.Equal("Bản đã duyệt cũ.", segment.Approvals[0].Text);
        Assert.Single(chapter.BatchReplacementHistory);
    }

    [Fact]
    public void Stale_scan_location_is_rejected_before_preview_is_created()
    {
        ChapterDocument chapter = Chapter("Còn chữ 老公.");
        ChineseTextOccurrence occurrence = Assert.Single(Scan(chapter));
        chapter.Segments[0].UserDraft = "Nội dung đã đổi.";
        chapter.Segments[0].DraftRevision++;

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            ChineseRetranslationPreviewBuilder.Build(chapter, occurrence,
                ChineseRetranslationMode.ResidualHanOnly, ChineseRetranslationMethod.HanViet, "lão công"));

        Assert.Contains("quét lại", error.Message, StringComparison.CurrentCultureIgnoreCase);
        Assert.Empty(chapter.BatchReplacementHistory);
    }

    [Fact]
    public void Empty_or_unchanged_translation_is_not_accepted()
    {
        ChapterDocument chapter = Chapter("Còn chữ 老公.");
        ChineseTextOccurrence occurrence = Assert.Single(Scan(chapter));

        Assert.Throws<InvalidDataException>(() => ChineseRetranslationPreviewBuilder.Build(chapter, occurrence,
            ChineseRetranslationMode.ResidualHanOnly, ChineseRetranslationMethod.Ollama, ""));
        Assert.Throws<InvalidDataException>(() => ChineseRetranslationPreviewBuilder.Build(chapter, occurrence,
            ChineseRetranslationMode.ResidualHanOnly, ChineseRetranslationMethod.Ollama, "老公"));
    }

    private static ChapterDocument Chapter(string draft)
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Test", "老公说了一句话。",
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 100, MaximumHanCharacters = 200 });
        chapter.Segments[0].UserDraft = draft;
        chapter.Segments[0].DraftRevision = 1;
        return chapter;
    }

    private static IReadOnlyList<ChineseTextOccurrence> Scan(ChapterDocument chapter) =>
        new ChineseTextScanner().Scan(chapter).Occurrences;
}
