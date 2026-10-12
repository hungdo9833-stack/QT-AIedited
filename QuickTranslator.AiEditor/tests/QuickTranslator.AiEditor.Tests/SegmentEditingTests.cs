using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SegmentEditingTests
{
    [Fact]
    public async Task Split_PreservesAllSourceAndUnaffectedIdsAndOrder()
    {
        var (service, _, chapter) = Create();
        await service.SplitSegmentAsync(chapter.Segments[0].Id, 3);
        var updated = service.GetSnapshot();
        Assert.Equal(4, updated.Segments.Count);
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(updated.Segments));
        Assert.Equal(chapter.Segments.Skip(1).Select(s => s.Id), updated.Segments.Skip(2).Select(s => s.Id));
        Assert.Equal(Enumerable.Range(1, 4), updated.Segments.Select(s => s.Order));
        DocumentJson.Validate(updated);
    }

    [Fact]
    public async Task Merge_KeepsSourceExact_AndOnlyReplacesAdjacentPair()
    {
        var (service, _, chapter) = Create();
        await service.MergeWithNextAsync(chapter.Segments[0].Id);
        var updated = service.GetSnapshot();
        Assert.Equal(2, updated.Segments.Count);
        Assert.Equal(chapter.Segments[0].SourceText + chapter.Segments[1].SourceText, updated.Segments[0].SourceText);
        Assert.Equal(chapter.Segments[2].Id, updated.Segments[1].Id);
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(updated.Segments));
        DocumentJson.Validate(updated);
    }

    [Fact]
    public async Task MoveBoundary_AffectsOnlyTwoSegments_AndNoOpKeepsHistoryUnchanged()
    {
        var (service, _, chapter) = Create();
        await service.MoveBoundaryAsync(chapter.Segments[0].Id, chapter.Segments[0].SourceText.Length);
        Assert.Empty(service.GetSnapshot().SegmentationHistory);
        await service.MoveBoundaryAsync(chapter.Segments[0].Id, 3);
        var updated = service.GetSnapshot();
        Assert.Equal(chapter.Segments[2].Id, updated.Segments[2].Id);
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(updated.Segments));
        Assert.Single(updated.SegmentationHistory);
    }

    [Fact]
    public async Task TranslatedSplit_RequiresConfirmation_ArchivesAndProtectsEditedText()
    {
        var (service, client, chapter) = Create();
        var id = chapter.Segments[0].Id;
        await service.TranslateAsync(id);
        service.UpdateDraft(id, "Bản sửa cần bảo vệ.");
        await service.ApproveAsync(id);
        await Assert.ThrowsAsync<SegmentationConfirmationRequiredException>(() => service.SplitSegmentAsync(id, 3));
        Assert.Equal(3, service.GetSnapshot().Segments.Count);
        await service.SplitSegmentAsync(id, 3, confirmed: true);
        var updated = service.GetSnapshot();
        var archived = Assert.Single(Assert.Single(updated.SegmentationHistory).PreviousSegments);
        Assert.Equal("Bản sửa cần bảo vệ.", archived.UserDraft);
        Assert.Single(archived.Approvals);
        Assert.Single(archived.Attempts);
        Assert.All(updated.Segments.Take(2), s => { Assert.True(s.ProtectedByPreviousEdit); Assert.True(s.RequiresRetranslation); });
        await new TranslationQueue(service).StartAsync();
        Assert.Equal(3, client.Calls); // Original request plus the two unaffected segments, never the protected split.
        Assert.Equal(chapter.OriginalText, service.GetSnapshot().OriginalText);
    }

    [Fact]
    public async Task ChangedAiOnlyPair_IsRetranslated_WhileUntouchedCompletedSegmentIsSkipped()
    {
        var (service, client, chapter) = Create();
        await new TranslationQueue(service).StartAsync();
        int before = client.Calls;
        await service.MoveBoundaryAsync(chapter.Segments[0].Id, 3, true);
        await new TranslationQueue(service).StartAsync();
        Assert.Equal(before + 2, client.Calls);
        Assert.Single(service.GetSnapshot().Segments[2].Attempts);
        Assert.Equal(chapter.Segments[2].Id, service.GetSnapshot().Segments[2].Id);
    }

    [Fact]
    public async Task MergeManualDrafts_KeepsBothTexts_AndRequiresHumanCheck()
    {
        var (service, _, chapter) = Create();
        service.UpdateDraft(chapter.Segments[0].Id, "Bản một.");
        service.UpdateDraft(chapter.Segments[1].Id, "Bản hai.");
        await service.MergeWithNextAsync(chapter.Segments[0].Id, true);
        var merged = service.GetSnapshot().Segments[0];
        Assert.Equal("Bản một.\n\nBản hai.", merged.UserDraft);
        Assert.True(merged.IsProtectedFromAutomaticTranslation);
        Assert.True(merged.RequiresRetranslation);
        Assert.Equal(SegmentStatus.InspectionRequired, merged.Status);
    }

    [Fact]
    public async Task RestoreBoundary_RestoresOldEditedVersion_AndArchivesNewWorkToo()
    {
        var (service, _, chapter) = Create();
        var id = chapter.Segments[0].Id;
        service.UpdateDraft(id, "Nháp trước tách.");
        await service.SplitSegmentAsync(id, 3, true);
        var newId = service.GetSnapshot().Segments[0].Id;
        service.UpdateDraft(newId, "Nháp sau tách.");
        await service.RestoreLastSegmentationAsync(true);
        var restored = service.GetSnapshot();
        Assert.Equal(id, restored.Segments[0].Id);
        Assert.Equal("Nháp trước tách.", restored.Segments[0].UserDraft);
        Assert.Equal("Nháp sau tách.", restored.SegmentationHistory.Last().PreviousSegments[0].UserDraft);
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(restored.Segments));
    }

    [Fact]
    public async Task InvalidBoundaryOrNonAdjacentMerge_CannotLoseAnyData()
    {
        var (service, _, chapter) = Create();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SplitSegmentAsync(chapter.Segments[0].Id, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => service.MergeWithNextAsync(chapter.Segments.Last().Id));
        Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(service.GetSnapshot().Segments));
        Assert.Empty(service.GetSnapshot().SegmentationHistory);
    }

    [Fact]
    public async Task SegmentationArchive_SurvivesJsonRoundTrip()
    {
        var (service, _, chapter) = Create();
        service.UpdateDraft(chapter.Segments[0].Id, "Bản sửa cũ.");
        await service.SplitSegmentAsync(chapter.Segments[0].Id, 3, true);
        var cloned = DocumentJson.Clone(service.GetSnapshot());
        DocumentJson.Validate(cloned);
        Assert.Equal("Bản sửa cũ.", cloned.SegmentationHistory[0].PreviousSegments[0].UserDraft);
    }

    private static (TranslationService, FakeClient, ChapterDocument) Create()
    {
        var chapter = TranslationService.CreateChapter("Chương thử", "甲乙。丙丁。\r\n戊己。庚辛。\r\n壬癸。子丑。",
            new() { MinimumHanCharacters = 3, TargetHanCharacters = 4, MaximumHanCharacters = 5 });
        var client = new FakeClient();
        return (new(chapter, client, new MemoryStore()), client, chapter);
    }
}
