using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class AtomicBatchTests
{
    public static IEnumerable<object[]> InvalidGroups => from index in new[] { 0, 1, 2 }
        from error in new[] { "revision", "protected", "conflict", "bounds", "approval", "status" }
        select new object[] { index, error };

    [Theory]
    [MemberData(nameof(InvalidGroups))]
    public void F05_FailureAtFirstMiddleOrLastSegmentLeavesWholeDocumentUnchanged(int index, string error)
    {
        var chapter = CreateChapter();
        var preview = Preview(chapter).ToArray();
        var segment = chapter.Segments[index];
        bool allowProtected = true;
        switch (error)
        {
            case "revision": preview[index] = preview[index] with { DraftRevision = 999 }; break;
            case "protected": segment.UserDraft = segment.EditableText; allowProtected = false; break;
            case "conflict": preview[index] = preview[index] with { Conflict = true }; break;
            case "bounds": preview[index] = preview[index] with { StartUtf16 = segment.EditableText.Length + 1 }; break;
            case "approval": segment.Approvals.Add(new() { Text = segment.EditableText, ApprovedAt = DateTimeOffset.UtcNow }); break;
            case "status": segment.Status = SegmentStatus.Translating; break;
        }
        string before = Serialize(chapter);
        Assert.ThrowsAny<InvalidOperationException>(() => BatchReplacementEngine.ApplyToChapter(chapter, preview,
            allowProtected, BatchReplacementScope.CurrentChapter, Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Equal(before, Serialize(chapter));
    }

    [Fact]
    public async Task F05_FailedBatchPersistenceLeavesLiveDocumentAndSavedRevisionUnchanged()
    {
        var chapter = CreateChapter();
        var store = new MemoryStore();
        var service = new TranslationService(chapter, new FakeClient(), store);
        await service.SaveAsync();
        var preview = service.PreviewBatchReplacement([Rule()], BatchReplacementScope.CurrentChapter);
        string before = Serialize(service.GetSnapshot());
        store.BeforeSave = (_, _) => throw new IOException("Injected batch write failure");
        await Assert.ThrowsAsync<IOException>(() => service.ApplyBatchReplacementAsync(preview, true, BatchReplacementScope.CurrentChapter));
        Assert.Equal(before, Serialize(service.GetSnapshot()));
        Assert.Equal(before, Serialize(store.LastSaved!));
        Assert.False(service.HasUnsavedChanges);
    }

    [Fact]
    public async Task F05_ConcurrentEditCannotEnterWhileBatchCandidateIsBeingWritten()
    {
        var chapter = CreateChapter();
        var store = new MemoryStore();
        var service = new TranslationService(chapter, new FakeClient(), store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.BeforeSave = async (_, _) => { entered.SetResult(); await release.Task; };
        var preview = service.PreviewBatchReplacement([Rule()], BatchReplacementScope.CurrentChapter);
        var batch = service.ApplyBatchReplacementAsync(preview, true, BatchReplacementScope.CurrentChapter);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Assert.Throws<InvalidOperationException>(() => service.UpdateDraft(chapter.Segments[0].Id, "Sửa trong lúc ghi batch"));
            Assert.Empty(service.GetSnapshot().BatchReplacementHistory); // Candidate is not committed yet.
        }
        finally { release.TrySetResult(); await batch; }
        Assert.Single(service.GetSnapshot().BatchReplacementHistory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F05_WriteFailureOrCancellationPreservesPendingDraftAndAllowsExplicitRetry(bool cancel)
    {
        var chapter = CreateChapter();
        var store = new MemoryStore();
        var service = new TranslationService(chapter, new FakeClient(), store);
        await service.SaveAsync();
        service.UpdateDraft(chapter.Segments[1].Id, "Bản chưa lưu 王秀兰.");
        string before = Serialize(service.GetSnapshot());
        var preview = service.PreviewBatchReplacement([Rule()], BatchReplacementScope.CurrentChapter);
        store.BeforeSave = (_, _) => cancel
            ? throw new OperationCanceledException("Injected cancellation during write")
            : throw new IOException("Injected write failure with pending draft");
        await Assert.ThrowsAnyAsync<Exception>(() => service.ApplyBatchReplacementAsync(preview, true, BatchReplacementScope.CurrentChapter));
        Assert.Equal(before, Serialize(service.GetSnapshot()));
        Assert.True(service.HasUnsavedChanges);
        Assert.False(service.IsProcessing);
        store.BeforeSave = null;
        await service.ApplyBatchReplacementAsync(preview, true, BatchReplacementScope.CurrentChapter);
        var saved = service.GetSnapshot();
        Assert.Equal("Bản chưa lưu Vương Tú Lan.", saved.Segments[1].UserDraft);
        Assert.Contains(saved.Segments[1].DraftHistory, x => x.Text == "Bản chưa lưu 王秀兰.");
        Assert.Single(saved.BatchReplacementHistory);
        Assert.False(service.HasUnsavedChanges);
    }

    [Fact]
    public async Task F05_ProjectPartialSuccessIsExplicitAndFailedChapterIsEntirelyUnchanged()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-phase2-batch-" + Guid.NewGuid());
        try
        {
            var store = new ChapterFileStore(root);
            var first = CreateChapter(); var second = CreateChapter();
            second.CharacterProject.Id = first.CharacterProject.Id;
            await store.SaveAsync(first); await store.SaveAsync(second);
            string before = File.ReadAllText(store.GetPath(second.Id));
            var chapters = new[] { first, second };
            var preview = new BatchReplacementEngine().Preview(chapters, [Rule()], BatchReplacementScope.CurrentProject)
                .Select(x => x.ChapterId == second.Id && x.SegmentOrder == 3 ? x with { DraftRevision = 999 } : x).ToArray();
            var report = await new BatchProjectReplacementService(store).ApplyAsync(chapters, preview, true);
            Assert.Equal(1, report.AppliedChapters); Assert.Equal(1, report.FailedChapters);
            Assert.False(string.IsNullOrWhiteSpace(report.Chapters.Single(x => !x.Applied).Error));
            Assert.Equal(before, File.ReadAllText(store.GetPath(second.Id)));
            Assert.Single((await store.LoadAsync(store.GetPath(first.Id))).Chapter.BatchReplacementHistory);
            Assert.Empty(second.BatchReplacementHistory);
            Assert.False(Directory.Exists(Path.Combine(root, "Backups")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task F05_LockedRealChapterFileLeavesDiskAndLiveDocumentUnchanged()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-phase2-locked-" + Guid.NewGuid());
        try
        {
            var store = new ChapterFileStore(root);
            var service = new TranslationService(CreateChapter(), new FakeClient(), store);
            await service.SaveAsync();
            string path = service.SavePath;
            byte[] beforeDisk = File.ReadAllBytes(path);
            string beforeLive = Serialize(service.GetSnapshot());
            var preview = service.PreviewBatchReplacement([Rule()], BatchReplacementScope.CurrentChapter);
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Assert.ThrowsAnyAsync<IOException>(() => service.ApplyBatchReplacementAsync(preview, true, BatchReplacementScope.CurrentChapter));
            Assert.Equal(beforeDisk, File.ReadAllBytes(path));
            Assert.Equal(beforeLive, Serialize(service.GetSnapshot()));
            Assert.False(service.HasUnsavedChanges);
            Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
            await service.ApplyBatchReplacementAsync(preview, true, BatchReplacementScope.CurrentChapter);
            Assert.Single((await store.LoadAsync(path)).Chapter.BatchReplacementHistory);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("SetQueueCurrent")]
    [InlineData("AdvanceQueue")]
    [InlineData("CheckpointElapsed")]
    [InlineData("SetQueueStatus")]
    public async Task F05_LateQueueCheckpointCannotMutateDocumentDuringBatchPersistence(string method)
    {
        var chapter = CreateChapter();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryStore { BeforeSave = async (_, _) => { entered.TrySetResult(); await release.Task; } };
        var service = new TranslationService(chapter, new FakeClient(), store);
        var batch = service.ApplyBatchReplacementAsync(service.PreviewBatchReplacement([Rule()], BatchReplacementScope.CurrentChapter),
            true, BatchReplacementScope.CurrentChapter);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            string before = Serialize(service.GetSnapshot());
            object?[] args = method switch
            {
                "SetQueueCurrent" => [chapter.Segments[0].Id],
                "AdvanceQueue" => [],
                "CheckpointElapsed" => [TimeSpan.FromSeconds(7)],
                _ => [QueueStatus.Faulted, null, "Late queue write failure"]
            };
            var error = Assert.Throws<System.Reflection.TargetInvocationException>(() => typeof(TranslationService)
                .GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(service, args));
            Assert.IsType<InvalidOperationException>(error.InnerException);
            Assert.Equal(before, Serialize(service.GetSnapshot()));
        }
        finally { release.TrySetResult(); await batch; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F05_ProjectCancellationReportsCompletedAndUnwrittenChaptersWithoutPartialDocument(bool undo)
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-phase2-cancel-" + Guid.NewGuid());
        try
        {
            using var cancellation = new CancellationTokenSource();
            var store = new ChapterFileStore(root);
            var chapters = new[] { CreateChapter(), CreateChapter(), CreateChapter() };
            foreach (var chapter in chapters)
            {
                chapter.CharacterProject.Id = chapters[0].CharacterProject.Id;
                await store.SaveAsync(chapter);
            }
            var preview = new BatchReplacementEngine().Preview(chapters, [Rule()], BatchReplacementScope.CurrentProject);
            Guid? batchId = null;
            if (undo)
            {
                batchId = (await new BatchProjectReplacementService(store).ApplyAsync(chapters, preview, true)).BatchId;
                chapters = (await new BatchProjectReplacementService(store).LoadProjectChaptersAsync(chapters[0].CharacterProject.Id)).ToArray();
            }
            var unchanged = chapters.Skip(1).ToDictionary(x => x.Id, x => File.ReadAllBytes(store.GetPath(x.Id)));
            var coordinator = new BatchProjectReplacementService(store, new CancelSecondChapterClock(cancellation));
            var report = undo ? await coordinator.UndoAsync(chapters, batchId!.Value, cancellation.Token)
                : await coordinator.ApplyAsync(chapters, preview, true, cancellation.Token);
            Assert.Equal(1, report.AppliedChapters);
            Assert.Equal(2, report.FailedChapters);
            Assert.All(report.Chapters.Where(x => !x.Applied), x => Assert.Contains("hủy", x.Error));
            foreach (var chapter in chapters.Skip(1)) Assert.Equal(unchanged[chapter.Id], File.ReadAllBytes(store.GetPath(chapter.Id)));
            Assert.Equal(undo, (await store.LoadAsync(store.GetPath(chapters[0].Id))).Chapter.BatchReplacementHistory.Single().Reverted);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class CancelSecondChapterClock(CancellationTokenSource cancellation) : TimeProvider
    {
        private int _calls;
        public override DateTimeOffset GetUtcNow()
        {
            if (++_calls == 2) cancellation.Cancel();
            return DateTimeOffset.UtcNow;
        }
    }

    internal static ChapterDocument CreateChapter()
    {
        var chapter = TranslationService.CreateChapter("Dữ liệu batch tổng hợp", "你好。\n你好。\n你好。",
            new() { MinimumHanCharacters = 1, TargetHanCharacters = 2, MaximumHanCharacters = 2 });
        Assert.Equal(3, chapter.Segments.Count);
        foreach (var segment in chapter.Segments)
        {
            segment.Attempts.Add(new() { Outcome = AttemptOutcome.Completed, AiText = $"Tên 王秀兰 ở đoạn {segment.Order}.",
                Prompt = "Synthetic prompt", PromptVersion = "test" });
            segment.Status = SegmentStatus.AwaitingReview;
            segment.DraftHistory.Add(new() { Text = "Nháp lịch sử giữ nguyên", Revision = 0 });
        }
        chapter.Queue.CandidateIds = chapter.Segments.Select(s => s.Id).ToList();
        return chapter;
    }
    internal static BatchReplacementRule Rule() => new() { Find = "王秀兰", Replacement = "Vương Tú Lan", Confirmed = true, SourceLabel = "Synthetic" };
    internal static IReadOnlyList<BatchReplacementPreviewItem> Preview(ChapterDocument chapter) =>
        new BatchReplacementEngine().Preview([chapter], [Rule()], BatchReplacementScope.CurrentChapter);
    internal static string Serialize(ChapterDocument chapter) => JsonSerializer.Serialize(chapter, DocumentJson.Options);
}
