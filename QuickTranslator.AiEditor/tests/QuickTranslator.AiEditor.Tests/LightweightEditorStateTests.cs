using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class LightweightEditorStateTests
{
    [Fact]
    public async Task StateMatchesDocumentThroughEditingApprovalTranslationCancellationAndQueueCompletion()
    {
        var chapter = TranslationService.CreateChapter("State", "你好。再见。");
        var client = new FakeClient();
        var service = new TranslationService(chapter, client, new MemoryStore());
        Guid id = chapter.Segments[0].Id;
        Check(service, id);
        service.UpdateDraft(id, "Bản nháp");
        Check(service, id);
        await service.ApproveAsync(id);
        Check(service, id);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Respond = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return FakeClient.Completion("unused");
        };
        using var cancellation = new CancellationTokenSource();
        var request = service.TranslateAsync(id, cancellationToken: cancellation.Token);
        await started.Task;
        Check(service, id);
        service.UpdateDraft(id, "Sửa trong lúc dịch");
        Check(service, id);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Check(service, id);
        client.Respond = (_, _) => Task.FromResult(FakeClient.Completion("Bản dịch hoàn tất."));
        await service.TranslateAsync(id);
        Check(service, id);

        // An unprotected chapter exercises an actual running queue and its terminal state.
        service = new TranslationService(chapter, client, new MemoryStore());
        var queue = new TranslationQueue(service);
        queue.ProgressChanged += (_, _) => Check(service, id);
        await queue.StartAsync();
        Check(service, id);
        Assert.Equal(service.GetSnapshot().Queue.Elapsed, queue.GetProgress().Elapsed);
    }

    [Theory]
    [InlineData(QueueMode.MissingSegments)]
    [InlineData(QueueMode.FailedSegments)]
    [InlineData(QueueMode.AllSegments)]
    public void CountsAndTimingMatchOriginalPredicatesForMixedHistory(QueueMode mode)
    {
        var chapter = TranslationService.CreateChapter("Mixed", string.Concat(Enumerable.Repeat("你好。", 12)),
            new() { MinimumHanCharacters = 1, TargetHanCharacters = 2, MaximumHanCharacters = 4 });
        chapter.Queue.Mode = mode;
        chapter.Queue.Elapsed = TimeSpan.FromSeconds(42);
        // Active translation is exercised above; persisted input cannot contain multiple live requests.
        var statuses = Enum.GetValues<SegmentStatus>().Where(s => s != SegmentStatus.Translating).ToArray();
        for (int i = 0; i < chapter.Segments.Count; i++)
        {
            var segment = chapter.Segments[i];
            segment.Status = statuses[i % statuses.Length];
            segment.RequiresRetranslation = i % 3 == 0;
            segment.UserDraft = i % 4 == 0 ? "nháp" : null;
            segment.Attempts.AddRange(new[]
            {
                Attempt(AttemptOutcome.Completed, 2), Attempt(AttemptOutcome.Completed, 4),
                Attempt(AttemptOutcome.Failed, 100), Attempt(AttemptOutcome.Cancelled, 200),
                Attempt(AttemptOutcome.Completed, 0),
                Attempt(AttemptOutcome.Completed, 500, new ModelOptions { Temperature = 0.5 })
            });
        }
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        foreach (var segment in chapter.Segments) Check(service, segment.Id);
        Check(service, Guid.NewGuid());
        var before = service.FullSnapshotCount;
        long builds = service.AttemptAggregateBuildCount;
        for (int i = 0; i < 30; i++)
        {
            service.UpdateDraft(chapter.Segments[0].Id, "edit " + i);
            service.GetEditorState(chapter.Segments[0].Id);
            new TranslationQueue(service).GetProgress();
        }
        Assert.Equal(before, service.FullSnapshotCount);
        Assert.Equal(builds, service.AttemptAggregateBuildCount);
    }

    [Fact]
    public async Task TimingCacheTracksCompletionModelChangesAndStructureRestore()
    {
        var chapter = TranslationService.CreateChapter("Cache", "你好。再见。");
        chapter.Segments[0].Attempts.AddRange([Attempt(AttemptOutcome.Completed, 2), Attempt(AttemptOutcome.Completed, 4)]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new FakeClient
        {
            Respond = async (_, ct) =>
            {
                started.SetResult();
                await finish.Task.WaitAsync(ct);
                return new LlmCompletion("Xin chào. Tạm biệt.", true, "stop",
                    new() { ClientElapsed = TimeSpan.FromSeconds(9) });
            }
        };
        var service = new TranslationService(chapter, client, new MemoryStore());
        Guid id = chapter.Segments[0].Id;
        Check(service, id);
        var translating = service.TranslateAsync(id);
        await started.Task;
        Check(service, id); // Cache the appended attempt while it is still in progress.
        long builds = service.AttemptAggregateBuildCount;
        finish.SetResult();
        await translating;
        Check(service, id);
        Assert.Equal(builds + 1, service.AttemptAggregateBuildCount);
        service.BeginQueue(QueueMode.AllSegments, chapter.DefaultModelOptions with { Temperature = 0.5 }, chapter.SelectedPreset, false);
        Check(service, id);
        service.SetQueueStatus(QueueStatus.Cancelled);
        await service.SplitSegmentAsync(id, "你好。".Length, confirmed: true);
        Check(service, id);
        await service.RestoreLastSegmentationAsync(confirmed: true);
        Check(service, id);
    }

    [Theory]
    [InlineData(QueueStatus.Idle)]
    [InlineData(QueueStatus.Paused)]
    [InlineData(QueueStatus.Cancelled)]
    [InlineData(QueueStatus.Interrupted)]
    [InlineData(QueueStatus.Faulted)]
    [InlineData(QueueStatus.Completed)]
    [InlineData(QueueStatus.CompletedWithWarnings)]
    [InlineData(QueueStatus.CompletedWithErrors)]
    public void IdleProgressKeepsPersistedElapsedWithoutCloningOrRebuildingTiming(QueueStatus status)
    {
        var chapter = TranslationService.CreateChapter("Checkpoint", "你好。");
        chapter.Queue.Status = status;
        chapter.Queue.Elapsed = TimeSpan.FromSeconds(3723);
        chapter.Queue.LastError = status == QueueStatus.Faulted ? "Saved failure" : null;
        chapter.Segments[0].Attempts.AddRange([Attempt(AttemptOutcome.Completed, 2), Attempt(AttemptOutcome.Completed, 4)]);
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        var queue = new TranslationQueue(service);
        Check(service, chapter.Segments[0].Id);
        var expected = service.GetQueueProgress();
        long clones = service.FullSnapshotCount, builds = service.AttemptAggregateBuildCount;
        for (int i = 0; i < 20; i++) Assert.Equal(expected, queue.GetProgress());
        Assert.Equal(TimeSpan.FromSeconds(3723), expected.Elapsed);
        Assert.Equal(clones, service.FullSnapshotCount);
        Assert.Equal(builds, service.AttemptAggregateBuildCount);
    }

    private static TranslationAttempt Attempt(AttemptOutcome outcome, int seconds, ModelOptions? model = null) => new()
    {
        Outcome = outcome, AiText = "Bản dịch", ModelOptions = model ?? new(),
        Metrics = new() { ClientElapsed = TimeSpan.FromSeconds(seconds) }
    };

    // Oracle copied from the former full-document UI/progress predicates.
    private static void Check(TranslationService service, Guid id)
    {
        var chapter = service.GetSnapshot();
        var selected = chapter.Segments.SingleOrDefault(s => s.Id == id);
        var state = service.GetEditorState(id);
        Assert.Equal(chapter.Title, state.Title);
        Assert.Equal(chapter.Segments.Count, state.Total);
        foreach (var status in Enum.GetValues<SegmentStatus>())
            Assert.Equal(chapter.Segments.Count(s => s.Status == status), state.Count(status));
        Assert.Equal(selected?.Status, state.SelectedSegment?.Status);
        Assert.Equal(selected?.Attempts.Count, state.SelectedSegment?.AttemptCount);
        Assert.Equal(selected?.Id, state.SelectedSegment?.Id);
        Assert.Equal(selected?.Order, state.SelectedSegment?.Order);
        Assert.Equal(selected?.SourceText, state.SelectedSegment?.SourceText);
        Assert.Equal(selected?.HanCharacterCount, state.SelectedSegment?.HanCharacterCount);
        Assert.Equal(selected?.RequiresRetranslation, state.SelectedSegment?.RequiresRetranslation);
        Assert.Equal(selected?.UserDraft is not null, state.SelectedSegment?.HasUserDraft == true);
        Assert.Equal(!string.IsNullOrWhiteSpace(selected?.EditableText), state.SelectedSegment?.HasEditableText == true);
        Assert.Equal(chapter.Queue.Status, state.QueueStatus);
        Assert.Equal(chapter.Segments.Count(s => s.RequiresRetranslation), state.RequiresRetranslation);
        Assert.Equal(chapter.Segments.Count(s => s.Status == SegmentStatus.InspectionRequired || s.RequiresRetranslation), state.NeedsInspection);
        Assert.Equal(chapter.Segments.SelectMany(s => s.NormalizationHistory).Where(r => !r.Reverted).Sum(r => r.Changes.Count), state.NormalizedChanges);
        Assert.Equal(chapter.Segments.SelectMany(s => s.NormalizationHistory).Any(r => !r.Reverted), state.CanUndoNormalization);
        Assert.Equal(chapter.BatchReplacementHistory.Any(r => !r.Reverted && !r.Automatic), state.CanUndoBatchReplacement);
        Assert.Equal(chapter.SegmentationHistory.Count > 0, state.CanRestoreStructure);
        var durations = chapter.Segments.SelectMany(s => s.Attempts).Where(a => a.Outcome == AttemptOutcome.Completed &&
            a.ModelOptions == chapter.Queue.ModelOptions && a.Metrics?.ClientElapsed.TotalSeconds > 0)
            .Select(a => a.Metrics!.ClientElapsed.TotalSeconds).ToArray();
        int remaining = chapter.Segments.Count(s => TranslationService.IsQueueEligible(s, chapter.Queue.Mode));
        var expected = new QueueProgress(chapter.Queue.Status, chapter.Segments.Count,
            chapter.Segments.Count(s => !s.RequiresRetranslation && !string.IsNullOrWhiteSpace(s.EditableText)),
            chapter.Segments.Count(s => s.Status == SegmentStatus.Error),
            chapter.Segments.Count(s => s.Status == SegmentStatus.InspectionRequired || s.RequiresRetranslation), remaining,
            chapter.Segments.SingleOrDefault(s => s.Id == chapter.Queue.CurrentSegmentId)?.Order, chapter.Queue.Elapsed,
            durations.Length >= 2 ? TimeSpan.FromSeconds(durations.Average() * remaining) : null, chapter.Queue.LastError);
        Assert.Equal(expected, service.GetQueueProgress());
    }
}
