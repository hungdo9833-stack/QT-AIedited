using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class TranslationQueueTests
{
    [Fact]
    public async Task WholeChapter_RunsInOrder_CheckpointsEverySegment_WithoutApproval()
    {
        var (service, client, store, chapter) = Create();
        var checkpoints = new List<int>();
        store.BeforeSave = (doc, _) => { checkpoints.Add(doc.Segments.Count(s => s.HasCompletedTranslation)); return Task.CompletedTask; };
        var queue = new TranslationQueue(service);
        await queue.StartAsync();
        Assert.Equal(chapter.Segments.Count, client.Calls);
        Assert.All(chapter.Segments.Select((s, i) => (s, i)), item => Assert.Contains(item.s.SourceText, client.Prompts[item.i]));
        Assert.All(service.GetSnapshot().Segments, s => { Assert.Equal(SegmentStatus.AwaitingReview, s.Status); Assert.Empty(s.Approvals); });
        Assert.Equal(QueueStatus.Completed, service.GetSnapshot().Queue.Status);
        Assert.All(Enumerable.Range(1, chapter.Segments.Count), count => Assert.Contains(count, checkpoints));
        Assert.Equal(chapter.Segments.Count, queue.GetProgress().Translated);
        await queue.StartAsync();
        Assert.Equal(chapter.Segments.Count, client.Calls);
    }

    [Fact]
    public async Task FiveThousandHan_AreAutomaticallyTranslated_AndComposedInOrder()
    {
        var chapter = TranslationService.CreateChapter("5.000 chữ", string.Concat(Enumerable.Repeat(new string('中', 100) + "。\n", 50)));
        var client = new FakeClient { Respond = (call, _) => Task.FromResult(FakeClient.Completion($"Bản dịch đoạn {call}.")) };
        var service = new TranslationService(chapter, client, new MemoryStore());
        await new TranslationQueue(service).StartAsync();
        var result = ChapterTranslationComposer.Compose(service.GetSnapshot());
        Assert.True(result.IsComplete);
        Assert.Equal(chapter.Segments.Count, client.Calls);
        Assert.Equal(ChapterTranslationComposer.Join(Enumerable.Range(1, client.Calls).Select(i => $"Bản dịch đoạn {i}.")), result.Text);
        Assert.Equal(QueueStatus.CompletedWithWarnings, service.GetSnapshot().Queue.Status); // Length is a warning, never an approval.
    }

    [Fact]
    public async Task Pause_FinishesCurrentRequest_ThenResumeSkipsItsCompletedResult()
    {
        var (service, client, store, chapter) = Create();
        var started = Signal();
        var release = new TaskCompletionSource<OllamaCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Respond = (call, _) => { if (call != 1) return Task.FromResult(FakeClient.Completion("Đoạn tiếp theo.")); started.SetResult(); return release.Task; };
        var queue = new TranslationQueue(service);
        var run = queue.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var pause = queue.PauseAsync();
        Assert.Equal(QueueStatus.PauseRequested, service.GetSnapshot().Queue.Status);
        Assert.Equal(1, client.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ResumeAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeWithNextAsync(chapter.Segments[0].Id, true));
        release.SetResult(FakeClient.Completion("Đoạn đầu đã lưu."));
        await Task.WhenAll(run, pause);
        Assert.Equal(1, client.Calls);
        Assert.Equal(QueueStatus.Paused, store.LastSaved!.Queue.Status);
        await queue.ResumeAsync();
        Assert.Equal(chapter.Segments.Count, client.Calls);
        Assert.Single(service.GetSnapshot().Segments[0].Attempts);
    }

    [Fact]
    public async Task Cancel_CheckpointsCancelledAttempt_AndResumeDoesNotRepeatEarlierSuccess()
    {
        var (service, client, store, chapter) = Create();
        var second = Signal();
        client.Respond = async (call, ct) =>
        {
            if (call != 2) return FakeClient.Completion("Bản đã nhận.");
            second.SetResult(); await Task.Delay(Timeout.Infinite, ct); return FakeClient.Completion("Không tới đây.");
        };
        var queue = new TranslationQueue(service);
        var run = queue.StartAsync();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await queue.CancelAsync();
        await run;
        Assert.Equal(QueueStatus.Cancelled, store.LastSaved!.Queue.Status);
        Assert.Equal(AttemptOutcome.Cancelled, store.LastSaved.Segments[1].Attempts[0].Outcome);
        await queue.ResumeAsync();
        Assert.Single(service.GetSnapshot().Segments[0].Attempts);
        Assert.Equal(chapter.Segments.Count + 1, client.Calls);
    }

    [Fact]
    public async Task CrashRecovery_SkipsCheckpointedSuccess_ButRetriesInterruptedRequestOnExplicitResume()
    {
        var (service, client, store, chapter) = Create();
        var second = Signal();
        client.Respond = async (call, ct) =>
        {
            if (call != 2) return FakeClient.Completion("Đã hoàn tất.");
            second.SetResult(); await Task.Delay(Timeout.Infinite, ct); return FakeClient.Completion("Không tới đây.");
        };
        var oldQueue = new TranslationQueue(service);
        var run = oldQueue.StartAsync();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacementClient = new FakeClient();
        var reopened = new TranslationService(store.LastSaved!, replacementClient, new MemoryStore());
        var recovered = new TranslationQueue(reopened);
        Assert.Equal(QueueStatus.Interrupted, reopened.GetSnapshot().Queue.Status);
        Assert.Equal(0, replacementClient.Calls);
        await recovered.ResumeAsync();
        Assert.Equal(chapter.Segments.Count - 1, replacementClient.Calls);
        Assert.Single(reopened.GetSnapshot().Segments[0].Attempts);
        await oldQueue.CancelAsync(); await run;
    }

    [Fact]
    public async Task Error_IsNotReportedAsChapterSuccess_AndOnlyErrorsAreRetriedOnRequest()
    {
        var (service, client, _, chapter) = Create();
        client.Respond = (call, _) => call == 2
            ? throw new OllamaRequestException(OllamaErrorKind.Http, "Synthetic error.")
            : Task.FromResult(FakeClient.Completion("Bản dịch đủ dấu câu."));
        var queue = new TranslationQueue(service);
        await queue.StartAsync();
        Assert.Equal(chapter.Segments.Count, client.Calls);
        Assert.Equal(QueueStatus.CompletedWithErrors, service.GetSnapshot().Queue.Status);
        Assert.Equal(1, queue.GetProgress().Errors);
        Assert.False(ChapterTranslationComposer.Compose(service.GetSnapshot()).IsComplete);
        await queue.StartAsync(QueueMode.FailedSegments);
        Assert.Equal(chapter.Segments.Count + 1, client.Calls);
        Assert.Equal(QueueStatus.Completed, service.GetSnapshot().Queue.Status);
        Assert.Equal(2, service.GetSnapshot().Segments[1].Attempts.Count);
    }

    [Fact]
    public async Task EmptyOutput_IsAnError_ButDoesNotStopOtherSegments()
    {
        var (service, client, _, chapter) = Create();
        client.Respond = (call, _) => Task.FromResult(FakeClient.Completion(call == 1 ? "" : "Bản dịch."));
        await new TranslationQueue(service).StartAsync();
        Assert.Equal(chapter.Segments.Count, client.Calls);
        Assert.Equal(SegmentStatus.Error, service.GetSnapshot().Segments[0].Status);
        Assert.Equal(QueueStatus.CompletedWithErrors, service.GetSnapshot().Queue.Status);
    }

    [Fact]
    public async Task EditedOrApprovedSegments_AreNeverAutomaticallyTranslated_EvenInErrorRetry()
    {
        var (service, client, _, chapter) = Create();
        service.UpdateDraft(chapter.Segments[0].Id, "Nháp cần giữ.");
        service.UpdateDraft(chapter.Segments[1].Id, "Bản đã duyệt.");
        await service.ApproveAsync(chapter.Segments[1].Id);
        var queue = new TranslationQueue(service);
        await queue.StartAsync();
        Assert.Equal(1, client.Calls);
        client.Respond = (_, _) => throw new OllamaRequestException(OllamaErrorKind.Http, "Synthetic error.");
        await Assert.ThrowsAsync<OllamaRequestException>(() => service.TranslateAsync(chapter.Segments[0].Id));
        int calls = client.Calls;
        await queue.StartAsync(QueueMode.FailedSegments);
        Assert.Equal(calls, client.Calls);
        Assert.Equal("Nháp cần giữ.", service.GetSnapshot().Segments[0].UserDraft);
        Assert.Equal("Bản đã duyệt.", service.GetSnapshot().Segments[1].EditableText);
    }

    [Fact]
    public async Task EditNextSegmentWhileRequestRuns_ProtectsItBeforeQueueDispatch()
    {
        var (service, client, _, chapter) = Create();
        var first = Signal();
        var release = Signal();
        client.Respond = async (call, ct) =>
        { if (call == 1) { first.SetResult(); await release.Task.WaitAsync(ct); } return FakeClient.Completion("Bản AI."); };
        var queue = new TranslationQueue(service);
        var run = queue.StartAsync();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.UpdateDraft(chapter.Segments[1].Id, "Nháp mới của người dùng.");
        release.SetResult(); await run;
        Assert.Equal(chapter.Segments.Count - 1, client.Calls);
        Assert.Empty(service.GetSnapshot().Segments[1].Attempts);
        Assert.Equal("Nháp mới của người dùng.", service.GetSnapshot().Segments[1].EditableText);
    }

    [Fact]
    public async Task SettingsAndPreset_AreFrozenForRun_AndStoredOnEveryAttempt()
    {
        var (service, _, _, _) = Create();
        var preset = new PromptPreset { Name = "Thử", Template = "Translate to Vietnamese:\n{{source_text}}\n{{context}}" };
        var options = new ModelOptions { Temperature = 0.2, ContextSize = 8192 };
        await new TranslationQueue(service).StartAsync(options: options, preset: preset);
        Assert.All(service.GetSnapshot().Segments, s =>
        {
            Assert.Equal(preset, s.Attempts[0].PresetSnapshot);
            Assert.Equal(options, s.Attempts[0].ModelOptions);
            Assert.StartsWith("Translate to Vietnamese:", s.Attempts[0].Prompt);
            Assert.NotNull(s.Attempts[0].SourceSha256);
        });
    }

    [Fact]
    public async Task SaveFailure_StopsQueueBeforeAnyNextRequest()
    {
        var (service, client, store, _) = Create();
        store.BeforeSave = (doc, _) => doc.Segments.Any(s => s.HasCompletedTranslation)
            ? throw new IOException("Synthetic disk full.") : Task.CompletedTask;
        var queue = new TranslationQueue(service);
        await Assert.ThrowsAsync<IOException>(() => queue.StartAsync());
        Assert.Equal(1, client.Calls);
        Assert.True(service.HasUnsavedChanges);
        Assert.Equal(QueueStatus.Faulted, service.GetSnapshot().Queue.Status);
        Assert.NotEmpty(service.GetSnapshot().Segments[0].EditableText);
    }

    [Fact]
    public async Task NoParallelQueueOrManualRequest_IsAllowedDuringRun()
    {
        var (service, client, _, chapter) = Create();
        var started = Signal();
        client.Respond = async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return FakeClient.Completion(""); };
        var queue = new TranslationQueue(service);
        var run = queue.StartAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.StartAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(chapter.Segments[1].Id));
        await queue.CancelAsync(); await run;
        Assert.Equal(1, client.Calls);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static (TranslationService, FakeClient, MemoryStore, ChapterDocument) Create()
    {
        var chapter = TranslationService.CreateChapter("Chương thử", "甲乙丙丁戊。\r\n己庚辛壬癸。\r\n天地玄黄宇。",
            new() { MinimumHanCharacters = 3, TargetHanCharacters = 5, MaximumHanCharacters = 6 });
        var client = new FakeClient(); var store = new MemoryStore();
        return (new(chapter, client, store), client, store, chapter);
    }
}
