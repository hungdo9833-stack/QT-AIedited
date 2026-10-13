using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ResegmentationTests
{
    private static readonly SegmenterOptions OriginalOptions = new()
    {
        MinimumHanCharacters = 1, TargetHanCharacters = 3,
        MaximumHanCharacters = 4, MaximumTextElements = 2000
    };
    private static readonly SegmenterOptions SmallerOptions = new()
    {
        MinimumHanCharacters = 1, TargetHanCharacters = 2,
        MaximumHanCharacters = 2, MaximumTextElements = 2000
    };

    [Fact]
    public async Task Untranslated_chapter_resegments_exact_source_and_archives_previous_boundaries()
    {
        ChapterDocument chapter = CreateChapter();
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());

        ResegmentationPreview preview = service.PreviewResegmentation(SmallerOptions);
        Assert.True(preview.BoundariesChanged);
        await service.ApplyResegmentationAsync(preview, confirmed: false);

        ChapterDocument changed = service.GetSnapshot();
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(changed.Segments));
        Assert.Equal(SmallerOptions, changed.SegmentationOptions);
        SegmentationChange history = Assert.Single(changed.SegmentationHistory);
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(history.PreviousSegments));
        Assert.Equal(changed.Segments.Select(segment => segment.Id), history.ReplacementIds);
    }

    [Fact]
    public async Task Partial_translation_keeps_protected_segment_as_immutable_anchor()
    {
        ChapterDocument chapter = CreateChapter();
        TranslationSegment anchor = chapter.Segments[1];
        anchor.UserDraft = "Bản dịch người dùng";
        anchor.DraftRevision = 2;
        anchor.Status = SegmentStatus.Approved;
        anchor.Approvals.Add(new ApprovalRecord { ApprovedAt = DateTimeOffset.UtcNow,
            DraftRevision = 2, Text = anchor.UserDraft });
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());

        ResegmentationPreview preview = service.PreviewResegmentation(SmallerOptions);
        Assert.Equal(1, preview.ProtectedSegmentCount);
        Assert.NotEmpty(preview.Warnings);
        await Assert.ThrowsAsync<SegmentationConfirmationRequiredException>(() =>
            service.ApplyResegmentationAsync(preview, confirmed: false));
        await service.ApplyResegmentationAsync(preview, confirmed: true);

        ChapterDocument changed = service.GetSnapshot();
        TranslationSegment preserved = changed.Segments.Single(segment => segment.Id == anchor.Id);
        Assert.Equal(anchor.StartUtf16, preserved.StartUtf16);
        Assert.Equal(anchor.SourceText, preserved.SourceText);
        Assert.Equal("Bản dịch người dùng", preserved.UserDraft);
        Assert.Single(preserved.Approvals);
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(changed.Segments));
    }

    [Fact]
    public async Task Failed_persistence_does_not_publish_resegmented_document()
    {
        ChapterDocument chapter = CreateChapter();
        var store = new MemoryStore { BeforeSave = (_, _) => throw new IOException("synthetic disk full") };
        var service = new TranslationService(chapter, new FakeClient(), store);
        ResegmentationPreview preview = service.PreviewResegmentation(SmallerOptions);

        await Assert.ThrowsAsync<IOException>(() => service.ApplyResegmentationAsync(preview, confirmed: false));

        ChapterDocument unchanged = service.GetSnapshot();
        Assert.Equal(chapter.Segments.Select(segment => segment.Id), unchanged.Segments.Select(segment => segment.Id));
        Assert.Empty(unchanged.SegmentationHistory);
        Assert.Equal(OriginalOptions, unchanged.SegmentationOptions);
    }

    [Theory]
    [InlineData(QueueStatus.Paused)]
    [InlineData(QueueStatus.Interrupted)]
    [InlineData(QueueStatus.Running)]
    public void Non_idle_queue_blocks_resegmentation(QueueStatus status)
    {
        ChapterDocument chapter = CreateChapter();
        chapter.Queue.Status = status;
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());

        Assert.Throws<InvalidOperationException>(() => service.PreviewResegmentation(SmallerOptions));
    }

    private static ChapterDocument CreateChapter() => TranslationService.CreateChapter("Chia lại",
        "甲乙丙。丁戊己。庚辛壬。癸子丑。", OriginalOptions);
}
