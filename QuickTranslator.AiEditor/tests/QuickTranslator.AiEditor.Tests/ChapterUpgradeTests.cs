using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChapterUpgradeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "qtaie-upgrade-" + Guid.NewGuid().ToString("N"));
    [Fact]
    public async Task CurrentSchema_RoundTripsDraftApprovalAndQueueState()
    {
        var store = new ChapterFileStore(_directory);
        var chapter = TranslationService.CreateChapter("Hiện tại", "你好。");
        chapter.Segments[0].UserDraft = "Xin chào.";
        chapter.Segments[0].DraftRevision = 1;
        chapter.Segments[0].Status = SegmentStatus.Approved;
        chapter.Segments[0].Approvals.Add(new() { ApprovedAt = DateTimeOffset.UtcNow, DraftRevision = 1, Text = "Xin chào." });
        chapter.Queue.Status = QueueStatus.Paused;
        await store.SaveAsync(chapter);

        ChapterDocument reopened = (await store.LoadAsync(store.GetPath(chapter.Id))).Chapter;
        Assert.Equal(3, reopened.SchemaVersion);
        Assert.Equal("Xin chào.", reopened.Segments[0].UserDraft);
        Assert.Single(reopened.Segments[0].Approvals);
        Assert.Equal(QueueStatus.Paused, reopened.Queue.Status);
    }

    [Fact]
    public async Task EnvelopeSchema_IsRejectedInsteadOfKeepingACompatibilityReader()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "synthetic.qtaie.json");
        var chapter = TranslationService.CreateChapter("Snapshot cũ", "你好。");
        string envelope = System.Text.Json.JsonSerializer.Serialize(new { chapter }, DocumentJson.Options);
        await File.WriteAllTextAsync(path, envelope);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ChapterFileStore(_directory).LoadAsync(path));
    }

    [Fact]
    public async Task FutureSchema_IsRejectedInsteadOfSilentlyDowngraded()
    {
        var chapter = TranslationService.CreateChapter("Tương lai", "你好。");
        chapter.SchemaVersion = 99;
        await Assert.ThrowsAsync<InvalidDataException>(() => new ChapterFileStore(_directory).SaveAsync(chapter));
    }

    [Fact]
    public async Task QueueCheckpointOnRealDisk_CanResumeWithoutRepeatingSavedSuccess()
    {
        var chapter = TranslationService.CreateChapter("Khôi phục queue", "甲乙。丙丁。\r\n戊己。庚辛。\r\n壬癸。子丑。",
            new() { MinimumHanCharacters = 3, TargetHanCharacters = 4, MaximumHanCharacters = 5 });
        var store = new ChapterFileStore(_directory);
        var reachedSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldClient = new FakeClient { Respond = async (call, ct) =>
        {
            if (call == 2) { reachedSecond.SetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return FakeClient.Completion("Đã lưu trên đĩa.");
        }};
        var service = new TranslationService(chapter, oldClient, store);
        var queue = new TranslationQueue(service);
        var running = queue.StartAsync();
        await reachedSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loaded = await store.LoadAsync(store.GetPath(chapter.Id));
        Assert.Equal(1, loaded.Chapter.Queue.NextIndex);
        Assert.Null(loaded.Chapter.Queue.CurrentSegmentId); // In-flight request is intentionally not checkpointed.
        var newClient = new FakeClient();
        var reopened = new TranslationService(loaded.Chapter, newClient, new MemoryStore());
        await new TranslationQueue(reopened).ResumeAsync();
        Assert.Equal(2, newClient.Calls);
        Assert.Single(reopened.GetSnapshot().Segments[0].Attempts);
        await queue.CancelAsync(); await running;
    }

    [Fact]
    public async Task Composer_KeepsOrderAndSegmentOffsets_ForManualAndAiText()
    {
        var chapter = TranslationService.CreateChapter("Ghép", "甲乙。\n丙丁。\n戊己。", new() { MinimumHanCharacters = 1, TargetHanCharacters = 2, MaximumHanCharacters = 3 });
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        await new TranslationQueue(service).StartAsync();
        service.UpdateDraft(chapter.Segments[1].Id, "  Bản sửa\r\nUnicode 👩🏽‍💻.  ");
        var snapshot = service.GetSnapshot();
        var composed = ChapterTranslationComposer.Compose(snapshot);
        Assert.True(composed.IsComplete);
        Assert.Equal(snapshot.Segments.Select(s => s.Id), composed.Spans.Select(s => s.SegmentId));
        foreach (var span in composed.Spans)
            Assert.Equal(snapshot.Segments.Single(s => s.Id == span.SegmentId).EditableText, composed.Text.Substring(span.Start, span.Length));
        string path = Path.Combine(_directory, "bản dịch.txt");
        await ChapterTranslationComposer.ExportAsync(snapshot, path);
        Assert.Equal(Encoding.UTF8.GetBytes(composed.Text), await File.ReadAllBytesAsync(path));
        Assert.Equal(composed.Text, await File.ReadAllTextAsync(path, Encoding.UTF8));
    }

    [Fact]
    public async Task ExportIncompleteChapter_RequiresExplicitDraftPermission()
    {
        var chapter = TranslationService.CreateChapter("Nháp", "你好。");
        string path = Path.Combine(_directory, "incomplete.txt");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ChapterTranslationComposer.ExportAsync(chapter, path));
        Assert.False(File.Exists(path));
        await ChapterTranslationComposer.ExportAsync(chapter, path, true);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RestoringOldAi_DoesNotDiscardTheManualVersionItReplaces()
    {
        var chapter = TranslationService.CreateChapter("Lịch sử", "你好。");
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        var attempt = await service.TranslateAsync(chapter.Segments[0].Id);
        service.UpdateDraft(chapter.Segments[0].Id, "Bản sửa quan trọng.");
        await service.UseAiAsDraftAsync(chapter.Segments[0].Id, attempt.Id);
        var saved = Assert.Single(service.GetSnapshot().Segments[0].DraftHistory);
        Assert.Equal("Bản sửa quan trọng.", saved.Text);
        await service.RestoreDraftAsync(chapter.Segments[0].Id, saved.Id, false);
        Assert.Equal("Bản sửa quan trọng.", service.GetSnapshot().Segments[0].UserDraft);
    }

    [Fact]
    public void FinalSentenceWarning_IsIndependentOfLengthAndDoesNotApproveAnything()
    {
        var issues = new TranslationQualityChecker().Check("他站起来，缓缓说道：「我们走吧。」", FakeClient.Completion("Anh đứng dậy rồi chậm rãi nói: chúng ta hãy"), new());
        Assert.Contains(issues, i => i.Code == IssueCode.IncompleteFinalSentence && i.Severity == IssueSeverity.Warning);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
