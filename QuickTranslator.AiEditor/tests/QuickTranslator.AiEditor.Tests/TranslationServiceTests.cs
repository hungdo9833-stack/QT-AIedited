using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed partial class TranslationServiceTests
{
    [Fact]
    public async Task OneClick_TranslatesOnlyRequestedSegment_AndNeverApprovesOrAdvances()
    {
        var (service, client, store, chapter) = Create();
        var selected = chapter.Segments[1];
        service.SelectSegment(selected.Id);
        var attempt = await service.TranslateAsync(selected.Id);
        var snapshot = service.GetSnapshot();
        Assert.Equal(1, client.Calls);
        Assert.Contains(selected.SourceText, client.Prompts[0]);
        Assert.Contains("Preceding source (reference only, do not translate)", client.Prompts[0]);
        Assert.DoesNotContain(chapter.Segments[2].SourceText, client.Prompts[0]);
        Assert.Equal(selected.Id, snapshot.SelectedSegmentId);
        Assert.Equal(SegmentStatus.AwaitingReview, snapshot.Segments[1].Status);
        Assert.Empty(snapshot.Segments[1].Approvals);
        Assert.All(snapshot.Segments.Where(s => s.Id != selected.Id), s =>
        {
            Assert.Empty(s.Attempts);
            Assert.Equal(SegmentStatus.Untranslated, s.Status);
        });
        Assert.Equal(TranslationPromptBuilder.Version, attempt.PromptVersion);
        Assert.Equal(new ModelOptions(), attempt.ModelOptions);
        Assert.Equal(attempt.Prompt, store.LastSaved!.Segments[1].Attempts[0].Prompt);
        Assert.Equal(1, store.Saves);
        Assert.False(service.HasUnsavedChanges);
    }

    [Fact]
    public async Task Retranslation_KeepsUserDraft_PreviousAi_AndApprovalHistory()
    {
        var (service, client, _, chapter) = Create();
        var id = chapter.Segments[0].Id;
        client.Respond = (call, _) => Task.FromResult(FakeClient.Completion($"Bản AI lần {call}."));
        await service.TranslateAsync(id);
        service.UpdateDraft(id, "Bản người dùng sửa.");
        await service.ApproveAsync(id);
        Assert.Equal(SegmentStatus.Approved, service.GetSnapshot().Segments[0].Status);
        await service.TranslateAsync(id);
        var segment = service.GetSnapshot().Segments[0];
        Assert.Equal("Bản người dùng sửa.", segment.EditableText);
        Assert.Equal("Bản người dùng sửa.", Assert.Single(segment.Approvals).Text);
        Assert.Equal(new[] { "Bản AI lần 1.", "Bản AI lần 2." }, segment.Attempts.Select(a => a.AiText));
        Assert.Equal(SegmentStatus.AwaitingReview, segment.Status);
        service.UpdateDraft(id, "Bản sửa tiếp.");
        Assert.Equal(SegmentStatus.AwaitingReview, service.GetSnapshot().Segments[0].Status);
    }

    [Fact]
    public async Task EditAndChangeSelection_DuringAi_KeepsDraftAndDoesNotChangeRequest()
    {
        var (service, client, store, chapter) = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<LlmCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Respond = (_, _) => { started.SetResult(); return release.Task; };
        var request = service.TranslateAsync(chapter.Segments[0].Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SegmentStatus.Translating, service.GetSnapshot().Segments[0].Status);
        service.UpdateDraft(chapter.Segments[0].Id, "Nháp được sửa khi AI đang chạy.");
        service.SelectSegment(chapter.Segments[1].Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.TranslateAsync(chapter.Segments[1].Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(chapter.Segments[0].Id));
        release.SetResult(FakeClient.Completion("Bản AI về sau."));
        await request;
        Assert.Equal(1, client.Calls);
        Assert.Equal(chapter.Segments[1].Id, store.LastSaved!.SelectedSegmentId);
        Assert.Equal("Nháp được sửa khi AI đang chạy.", store.LastSaved.Segments[0].UserDraft);
        Assert.Empty(store.LastSaved.Segments[1].Attempts);
    }

    [Fact]
    public async Task EditAfterApproval_RequiresAnotherHumanApproval()
    {
        var (service, _, _, chapter) = Create();
        var id = chapter.Segments[0].Id;
        service.UpdateDraft(id, "Bản sửa đầu.");
        await service.ApproveAsync(id);
        service.UpdateDraft(id, "Bản sửa sau.");
        var segment = service.GetSnapshot().Segments[0];
        Assert.Equal(SegmentStatus.AwaitingReview, segment.Status);
        Assert.Equal("Bản sửa đầu.", Assert.Single(segment.Approvals).Text);
        Assert.True(service.HasUnsavedChanges);
        await service.ApproveAsync(id);
        Assert.Equal(2, service.GetSnapshot().Segments[0].Approvals.Count);
    }

    [Fact]
    public async Task EmptyAiAndEmptyDraft_CannotBecomeAutomaticallyApproved()
    {
        var (service, client, _, chapter) = Create();
        client.Respond = (_, _) => Task.FromResult(FakeClient.Completion("  \r\n"));
        var id = chapter.Segments[0].Id;
        var attempt = await service.TranslateAsync(id);
        Assert.Equal(AttemptOutcome.InvalidOutput, attempt.Outcome);
        Assert.Equal(SegmentStatus.Error, service.GetSnapshot().Segments[0].Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ApproveAsync(id));
    }

    [Fact]
    public async Task Cancellation_RecordsErrorAndHistory_ButKeepsDraft()
    {
        var (service, client, store, chapter) = Create();
        var id = chapter.Segments[0].Id;
        service.UpdateDraft(id, "Nháp cần giữ.");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Respond = async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeClient.Completion("Không tới đây.");
        };
        using var cancellation = new CancellationTokenSource();
        var request = service.TranslateAsync(id, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        var saved = store.LastSaved!.Segments[0];
        Assert.Equal(AttemptOutcome.Cancelled, Assert.Single(saved.Attempts).Outcome);
        Assert.Equal(SegmentStatus.Error, saved.Status);
        Assert.Equal("Nháp cần giữ.", saved.UserDraft);
        Assert.Empty(saved.Approvals);
    }

    [Fact]
    public async Task Queue_checkpoints_each_completed_segment_once_instead_of_before_and_after_request()
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Chương thử", "甲乙丙丁戊。\r\n己庚辛壬癸。",
            new() { MinimumHanCharacters = 3, TargetHanCharacters = 5, MaximumHanCharacters = 6 });
        var store = new MemoryStore();
        var client = new FakeClient();
        var service = new TranslationService(chapter, client, store);
        var queue = new TranslationQueue(service);

        await queue.StartAsync();

        Assert.Equal(2, client.Calls);
        Assert.Equal(4, store.Saves); // queue start + one per completed segment + terminal status
        Assert.All(store.LastSaved!.Segments, segment => Assert.True(segment.HasCompletedTranslation));
        Assert.Equal(QueueStatus.Completed, store.LastSaved.Queue.Status);
    }

    [Fact]
    public async Task PreviousUnapprovedDraftIsContextOnlyAndIsNeverOverwritten()
    {
        var (service, client, _, chapter) = Create();
        string draft = "Bản nháp trước chưa duyệt cần dùng làm mạch văn.";
        service.UpdateDraft(chapter.Segments[0].Id, draft);

        await service.TranslateAsync(chapter.Segments[1].Id);

        Assert.Contains(draft, client.Prompts.Single(), StringComparison.Ordinal);
        Assert.Equal(draft, service.GetSnapshot().Segments[0].UserDraft);
    }

    [Fact]
    public async Task PromptOptionalContextIsTrimmedToModelInputBudget()
    {
        var (service, client, _, chapter) = Create();
        service = new TranslationService(chapter, client, new MemoryStore(), memory: new LongContextMemory());
        var options = new ModelOptions { ContextSize = 1_024, MaximumOutputTokens = 256 };

        await service.TranslateAsync(chapter.Segments[0].Id, options);

        string prompt = client.Prompts.Single();
        Assert.Contains(chapter.Segments[0].SourceText, prompt, StringComparison.Ordinal);
        Assert.True(prompt.Length < 3_072, $"Prompt length {prompt.Length:N0} exceeded the bounded input budget.");
    }

    private sealed class LongContextMemory : IContextMemory
    {
        public ValueTask<string> GetNotesAsync(ChapterDocument chapter, Guid segmentId,
            CancellationToken cancellationToken) => ValueTask.FromResult(new string('ữ', 20_000));
    }

    [Fact]
    public async Task DiskFailureAfterResponse_DoesNotLoseCompletedAiResultInMemory()
    {
        var (service, client, store, chapter) = Create();
        store.BeforeSave = (_, _) => throw new IOException("Synthetic disk full.");
        await Assert.ThrowsAsync<IOException>(() => service.TranslateAsync(chapter.Segments[0].Id));
        Assert.Equal(1, client.Calls);
        Assert.True(service.HasUnsavedChanges);
        Assert.Equal(AttemptOutcome.Completed, service.GetSnapshot().Segments[0].Attempts[0].Outcome);
    }

    [Fact]
    public async Task DiskFailureAfterResponse_PreservesAiAndUserDraftInMemory_ForExplicitSave()
    {
        var (service, _, store, chapter) = Create();
        var id = chapter.Segments[0].Id;
        service.UpdateDraft(id, "Nháp riêng.");
        store.BeforeSave = (_, _) => throw new IOException("Synthetic disk full.");
        await Assert.ThrowsAsync<IOException>(() => service.TranslateAsync(id));
        var segment = service.GetSnapshot().Segments[0];
        Assert.Equal(AttemptOutcome.Completed, segment.Attempts[0].Outcome);
        Assert.Equal("Một bản dịch thử nghiệm.", segment.Attempts[0].AiText);
        Assert.Equal("Nháp riêng.", segment.UserDraft);
        Assert.True(service.HasUnsavedChanges);
        store.BeforeSave = null;
        await service.SaveAsync();
        Assert.False(service.HasUnsavedChanges);
        Assert.Equal(segment.Attempts[0].AiText, store.LastSaved!.Segments[0].Attempts[0].AiText);
    }

    [Fact]
    public async Task ConcurrentSaves_CannotOverwriteNewerDraftWithAnOldSnapshot()
    {
        var (service, _, store, chapter) = Create();
        var id = chapter.Segments[0].Id;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        store.BeforeSave = async (_, ct) =>
        {
            if (++calls != 1) return;
            started.SetResult();
            await release.Task.WaitAsync(ct);
        };
        service.UpdateDraft(id, "Nháp cũ.");
        var oldSave = service.SaveAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.UpdateDraft(id, "Nháp mới.");
        var newSave = service.SaveAsync();
        release.SetResult();
        await Task.WhenAll(oldSave, newSave);
        Assert.Equal("Nháp mới.", store.LastSaved!.Segments[0].UserDraft);
        Assert.False(service.HasUnsavedChanges);
    }

    [Fact]
    public async Task Restart_RecoversInterruptedAttempt_WithoutCallingAi()
    {
        var (_, client, store, chapter) = Create();
        chapter.Segments[0].Status = SegmentStatus.Translating;
        chapter.Segments[0].UserDraft = "Nháp đã lưu.";
        chapter.Segments[0].Attempts.Add(new() { Outcome = AttemptOutcome.InProgress, Prompt = "Synthetic prompt." });
        var restarted = new TranslationService(chapter, client, store);
        await restarted.SaveAsync();
        Assert.Equal(0, client.Calls);
        Assert.Equal(AttemptOutcome.Interrupted, store.LastSaved!.Segments[0].Attempts[0].Outcome);
        Assert.Equal(SegmentStatus.Error, store.LastSaved.Segments[0].Status);
        Assert.Equal("Nháp đã lưu.", store.LastSaved.Segments[0].UserDraft);
    }

    [Fact]
    public async Task ChoosingOldAiDraft_IsExplicit_AndRequiresReviewAgain()
    {
        var (service, client, _, chapter) = Create();
        var id = chapter.Segments[0].Id;
        client.Respond = (call, _) => Task.FromResult(FakeClient.Completion($"AI {call}."));
        var old = await service.TranslateAsync(id);
        await service.TranslateAsync(id);
        service.UpdateDraft(id, "Bản sửa riêng.");
        await service.UseAiAsDraftAsync(id, old.Id);
        var segment = service.GetSnapshot().Segments[0];
        Assert.Equal("AI 1.", segment.UserDraft);
        Assert.Equal(old.Id, segment.DraftSourceAttemptId);
        Assert.Equal(SegmentStatus.AwaitingReview, segment.Status);
    }

    private static (TranslationService Service, FakeClient Client, MemoryStore Store, ChapterDocument Chapter) Create()
    {
        var chapter = TranslationService.CreateChapter("Chương thử", "甲乙丙丁戊。\r\n己庚辛壬癸。\r\n天地玄黄宇。",
            new() { MinimumHanCharacters = 3, TargetHanCharacters = 5, MaximumHanCharacters = 6 });
        var client = new FakeClient();
        var store = new MemoryStore();
        return (new(chapter, client, store), client, store, chapter);
    }
}
