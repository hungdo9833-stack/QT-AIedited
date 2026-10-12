using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SafeBatchUndoTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task F06_ReplaceApproveUndoRejectsWholeChapterAndPreservesApproval(int index)
    {
        var service = await ReplacedAsync(new MemoryStore());
        await service.ApproveAsync(service.GetSnapshot().Segments[index].Id);
        string before = AtomicBatchTests.Serialize(service.GetSnapshot());
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UndoLatestBatchReplacementAsync());
        Assert.Contains("duyệt", error.Message);
        Assert.Equal(before, AtomicBatchTests.Serialize(service.GetSnapshot()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task F06_ReplaceEditUndoRejectsWholeChapterEvenIfUserReturnsToSameText(int index)
    {
        var service = await ReplacedAsync(new MemoryStore());
        var segment = service.GetSnapshot().Segments[index];
        service.UpdateDraft(segment.Id, "Chỉnh thủ công mới");
        service.UpdateDraft(segment.Id, segment.EditableText);
        string before = AtomicBatchTests.Serialize(service.GetSnapshot());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UndoLatestBatchReplacementAsync());
        Assert.Equal(before, AtomicBatchTests.Serialize(service.GetSnapshot()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F06_ReplaceSaveReopenUndoUsesPersistedApprovalVersion(bool approve)
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-phase2-undo-" + Guid.NewGuid());
        try
        {
            var store = new ChapterFileStore(root);
            var service = await ReplacedAsync(store);
            Guid id = service.GetSnapshot().Id;
            if (approve) await service.ApproveAsync(service.GetSnapshot().Segments[1].Id);
            await service.SaveAsync();
            var reopened = new TranslationService((await store.LoadAsync(store.GetPath(id))).Chapter, new FakeClient(), store);
            string before = AtomicBatchTests.Serialize(reopened.GetSnapshot());
            if (approve)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.UndoLatestBatchReplacementAsync());
                Assert.Equal(before, AtomicBatchTests.Serialize(reopened.GetSnapshot()));
            }
            else
            {
                await reopened.UndoLatestBatchReplacementAsync();
                Assert.All(reopened.GetSnapshot().Segments, x => Assert.Contains("王秀兰", x.EditableText));
                Assert.True(reopened.GetSnapshot().BatchReplacementHistory.Single().Reverted);
                Assert.All(reopened.GetSnapshot().Segments, x => Assert.Contains(x.DraftHistory, d => d.Text == "Nháp lịch sử giữ nguyên"));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task F06_FailedUndoSaveKeepsLiveAndPersistedHistoryUnchangedAndCanRetry()
    {
        var store = new MemoryStore();
        var service = await ReplacedAsync(store);
        string before = AtomicBatchTests.Serialize(service.GetSnapshot());
        store.BeforeSave = (_, _) => throw new IOException("Injected undo write failure");
        await Assert.ThrowsAsync<IOException>(() => service.UndoLatestBatchReplacementAsync());
        Assert.Equal(before, AtomicBatchTests.Serialize(service.GetSnapshot()));
        Assert.Equal(before, AtomicBatchTests.Serialize(store.LastSaved!));
        store.BeforeSave = null;
        await service.UndoLatestBatchReplacementAsync();
        Assert.True(service.GetSnapshot().BatchReplacementHistory.Single().Reverted);
    }

    [Fact]
    public async Task F06_LegacyRunRemainsReadableButCannotGuessApprovalStateForUndo()
    {
        var service = await ReplacedAsync(new MemoryStore());
        var chapter = service.GetSnapshot();
        foreach (var revision in chapter.BatchReplacementHistory.Single().Segments)
        {
            revision.AppliedApprovalVersion = null;
            revision.AppliedLastApprovalId = null;
        }
        DocumentJson.Validate(chapter);
        string before = AtomicBatchTests.Serialize(chapter);
        var error = Assert.Throws<InvalidOperationException>(() => BatchReplacementEngine.UndoLatest(chapter, DateTimeOffset.UtcNow));
        Assert.Contains("lịch sử batch cũ", error.Message);
        Assert.Equal(before, AtomicBatchTests.Serialize(chapter));
    }

    [Fact]
    public async Task F06_ApprovalBeforeBatchCanBePreservedAndRestoredWithoutDeletingItsHistory()
    {
        var service = new TranslationService(AtomicBatchTests.CreateChapter(), new FakeClient(), new MemoryStore());
        var id = service.GetSnapshot().Segments[1].Id;
        await service.ApproveAsync(id);
        var before = service.GetSnapshot().Segments[1];
        await service.ApplyBatchReplacementAsync(service.PreviewBatchReplacement([AtomicBatchTests.Rule()],
            BatchReplacementScope.CurrentChapter), true, BatchReplacementScope.CurrentChapter);
        await service.UndoLatestBatchReplacementAsync();
        var after = service.GetSnapshot().Segments[1];
        Assert.Equal(before.UserDraft, after.UserDraft);
        Assert.Equal(SegmentStatus.Approved, after.Status);
        Assert.Equal(before.Approvals.Single().Id, after.Approvals.Single().Id);
        Assert.True(after.DraftHistory.Count >= before.DraftHistory.Count);
    }

    private static async Task<TranslationService> ReplacedAsync(IChapterStore store)
    {
        var service = new TranslationService(AtomicBatchTests.CreateChapter(), new FakeClient(), store);
        await service.ApplyBatchReplacementAsync(service.PreviewBatchReplacement([AtomicBatchTests.Rule()],
            BatchReplacementScope.CurrentChapter), true, BatchReplacementScope.CurrentChapter);
        return service;
    }
}
