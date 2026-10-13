using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SourceEditingTests
{
    private static readonly SegmenterOptions Options = new()
    {
        MinimumHanCharacters = 1, TargetHanCharacters = 3,
        MaximumHanCharacters = 4, MaximumTextElements = 2000
    };

    [Fact]
    public async Task Editing_untranslated_source_preserves_exact_text_offsets_and_revision()
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Sửa nguồn", "甲乙丙。丁戊己。庚辛壬。", Options);
        var store = new MemoryStore();
        var service = new TranslationService(chapter, new FakeClient(), store);

        SourceEditPreview preview = service.PreviewSourceEdit("甲乙新增丙。丁戊己。庚辛壬。", Options);
        await service.ApplySourceEditAsync(preview, confirmed: false);

        ChapterDocument edited = service.GetSnapshot();
        Assert.Equal(2, edited.SourceRevision);
        Assert.Equal("甲乙新增丙。丁戊己。庚辛壬。", edited.OriginalText);
        Assert.Equal(edited.OriginalText, TextSegmenter.Reconstruct(edited.Segments));
        Assert.Single(edited.SourceEditHistory);
        Assert.Equal(chapter.OriginalText, edited.SourceEditHistory[0].PreviousOriginalText);
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(edited.SourceEditHistory[0].PreviousSegments));
        Assert.Equal(edited.OriginalText, store.LastSaved!.OriginalText);
    }

    [Fact]
    public async Task Editing_one_translated_region_requires_confirmation_and_archives_all_protected_data()
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Có bản dịch", "甲乙丙。丁戊己。庚辛壬。", Options);
        TranslationSegment protectedSegment = chapter.Segments[1];
        var attempt = new TranslationAttempt { StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
            Outcome = AttemptOutcome.Completed, AiText = "Bản AI cũ", Prompt = "synthetic", PromptVersion = "test" };
        protectedSegment.Attempts.Add(attempt);
        protectedSegment.UserDraft = "Bản người dùng cũ";
        protectedSegment.DraftRevision = 2;
        protectedSegment.Approvals.Add(new ApprovalRecord { ApprovedAt = DateTimeOffset.UtcNow,
            DraftRevision = 2, Text = "Bản người dùng cũ", SourceAttemptId = attempt.Id });
        protectedSegment.DraftHistory.Add(new DraftSnapshot { RecordedAt = DateTimeOffset.UtcNow,
            Revision = 1, Text = "Nháp trước", Reason = "synthetic" });
        protectedSegment.Status = SegmentStatus.Approved;
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        string changed = chapter.OriginalText.Replace(protectedSegment.SourceText,
            protectedSegment.SourceText.Replace('戊', '午'), StringComparison.Ordinal);

        SourceEditPreview preview = service.PreviewSourceEdit(changed, Options);
        Assert.True(preview.ProtectedAffectedSegmentCount > 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplySourceEditAsync(preview, confirmed: false));
        await service.ApplySourceEditAsync(preview, confirmed: true);

        ChapterDocument edited = service.GetSnapshot();
        TranslationSegment archived = Assert.Single(edited.SourceEditHistory).PreviousSegments
            .Single(segment => segment.Id == protectedSegment.Id);
        Assert.Equal("Bản người dùng cũ", archived.UserDraft);
        Assert.Single(archived.Approvals);
        Assert.Single(archived.Attempts);
        Assert.Single(archived.DraftHistory);
        Assert.Contains(edited.Segments, segment => segment.Status == SegmentStatus.InspectionRequired && segment.RequiresRetranslation);
    }

    [Fact]
    public async Task Insertion_before_translated_segment_maps_by_content_and_preserves_translation()
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Chèn đầu", "甲乙丙。丁戊己。庚辛壬。", Options);
        TranslationSegment protectedSegment = chapter.Segments[^1];
        protectedSegment.UserDraft = "Bản Việt được giữ";
        protectedSegment.DraftRevision = 1;
        protectedSegment.Status = SegmentStatus.AwaitingReview;
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());

        SourceEditPreview preview = service.PreviewSourceEdit("新增。" + chapter.OriginalText, Options);
        await service.ApplySourceEditAsync(preview, confirmed: false);

        TranslationSegment mapped = service.GetSnapshot().Segments.Single(segment =>
            segment.SourceText == protectedSegment.SourceText);
        Assert.Equal("Bản Việt được giữ", mapped.UserDraft);
        Assert.Equal(SegmentStatus.AwaitingReview, mapped.Status);
    }

    [Fact]
    public async Task Full_replacement_requires_confirmation_and_failed_save_keeps_live_document_unchanged()
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Thay toàn bộ", "甲乙丙。丁戊己。", Options);
        var store = new MemoryStore { BeforeSave = (_, _) => throw new IOException("synthetic disk full") };
        var service = new TranslationService(chapter, new FakeClient(), store);
        SourceEditPreview preview = service.PreviewSourceEdit("完全不同的新正文。", Options);
        Assert.True(preview.ReplacesMostOfSource);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApplySourceEditAsync(preview, confirmed: false));
        await Assert.ThrowsAsync<IOException>(() => service.ApplySourceEditAsync(preview, confirmed: true));

        Assert.Equal(chapter.OriginalText, service.GetSnapshot().OriginalText);
        Assert.Empty(service.GetSnapshot().SourceEditHistory);
    }
}
