using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class LightweightEditorUiTests
{
    [Fact]
    public Task TypingInBothEditorsAndIdleTicksNeverCloneTheChapter() => StaHost.Run(() =>
    {
        using var f = new WorkspaceFixture();
        var chapter = TranslationService.CreateChapter("Typing", "你好。再见。");
        f.Attach(chapter);
        f.Call("Refresh");
        long clones = Counter(f.Service, "FullSnapshotCount");
        long aggregates = Counter(f.Service, "AttemptAggregateBuildCount");
        for (int i = 0; i < 20; i++)
        {
            f.Text("DraftText").Text = "Draft " + i;
            f.Call("OnChapterDraftChanged", chapter.Segments[0].Id, "Block " + i);
        }
        Assert.Equal(clones, Counter(f.Service, "FullSnapshotCount"));
        Assert.Equal(aggregates, Counter(f.Service, "AttemptAggregateBuildCount"));
        Assert.True(f.Service.GetEditorState(chapter.Segments[0].Id).SelectedSegment!.HasUserDraft);
        Assert.Equal("Block 19", f.Text("DraftText").Text);
        long reads = Counter(f.Service, "LightweightSnapshotCount");
        for (int i = 0; i < 20; i++) f.Call("OnProgressTimerTick", null, EventArgs.Empty);
        Assert.Equal(reads, Counter(f.Service, "LightweightSnapshotCount"));
        Assert.Equal(clones, Counter(f.Service, "FullSnapshotCount"));
        Assert.Equal(100d, ((ProgressBar)f.Window.FindName("ChapterProgress")).Value);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task HistoryButtonsAndSummaryKeepOriginalPredicates(bool normalization, bool manualReplacement) => StaHost.Run(() =>
    {
        using var f = new WorkspaceFixture();
        var chapter = TranslationService.CreateChapter("History", "你好。");
        var segment = chapter.Segments[0];
        segment.UserDraft = "Bản nháp";
        segment.Status = SegmentStatus.InspectionRequired;
        segment.RequiresRetranslation = true;
        segment.NormalizationHistory.Add(new()
        {
            BatchId = Guid.NewGuid(), Reverted = !normalization,
            Changes = [new() { Length = 1, Original = "ta", Replacement = "tôi", CharacterId = Guid.NewGuid() }]
        });
        foreach (var (automatic, reverted) in new[] { (true, false), (false, !manualReplacement) })
            chapter.BatchReplacementHistory.Add(new()
            {
                Automatic = automatic, Reverted = reverted,
                Segments = [new() { SegmentId = segment.Id, SegmentOrder = 1,
                    Changes = [new() { LengthUtf16 = 1, Original = "x", Replacement = "y" }] }]
            });
        chapter.SegmentationHistory.Add(new() { PreviousSegments = [segment], ReplacementIds = [segment.Id] });
        f.Attach(chapter);
        CheckButtons(f);
        Assert.Equal($"1 đoạn · đã duyệt 0 · đã chuẩn hóa {(normalization ? 1 : 0)} vị trí",
            ((TextBlock)f.Window.FindName("ChapterSummary")).Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ButtonsMatchDocumentThroughEditingApprovalTranslationCancellationAndQueueCompletion() => StaHost.Run(async () =>
    {
        using var f = new WorkspaceFixture();
        var chapter = TranslationService.CreateChapter("States", "你好。再见。");
        f.Attach(chapter);
        CheckButtons(f);
        f.Text("DraftText").Text = "Bản nháp";
        CheckButtons(f);
        await f.Service.ApproveAsync(chapter.Segments[0].Id);
        CheckButtons(f);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Client.Respond = async (_, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        };
        using var cancellation = new CancellationTokenSource();
        f.Set("_busy", true);
        var request = f.Service.TranslateAsync(chapter.Segments[0].Id, cancellationToken: cancellation.Token);
        await started.Task;
        CheckButtons(f);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        f.Set("_busy", false);
        CheckButtons(f);
        f.Attach(chapter);
        started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = f.Get<TranslationQueue>("_queue");
        var run = queue.StartAsync();
        await started.Task;
        Assert.Equal(QueueStatus.Running, queue.GetProgress().Status);
        CheckButtons(f);
        long clones = Counter(f.Service, "FullSnapshotCount");
        f.Call("OnProgressTimerTick", null, EventArgs.Empty);
        Assert.Equal(clones, Counter(f.Service, "FullSnapshotCount"));
        await queue.CancelAsync();
        await run;
        Assert.Equal(QueueStatus.Cancelled, queue.GetProgress().Status);
        CheckButtons(f);
        f.Client.Respond = (_, _) => Task.FromResult(QuickTranslator.AiEditor.Tests.FakeClient.Completion("Xin chào. Tạm biệt."));
        await queue.ResumeAsync();
        Assert.Equal(QueueStatus.Completed, queue.GetProgress().Status);
        CheckButtons(f);
    });

    [Theory]
    [InlineData(QueueStatus.Idle)]
    [InlineData(QueueStatus.Paused)]
    [InlineData(QueueStatus.Cancelled)]
    [InlineData(QueueStatus.Interrupted)]
    [InlineData(QueueStatus.Faulted)]
    [InlineData(QueueStatus.Completed)]
    [InlineData(QueueStatus.CompletedWithWarnings)]
    [InlineData(QueueStatus.CompletedWithErrors)]
    public Task IdleTicksPreserveButtonsAndPersistedProgressWithoutReadingTheDocument(QueueStatus status) => StaHost.Run(() =>
    {
        using var f = new WorkspaceFixture();
        var chapter = TranslationService.CreateChapter("Checkpoint", "你好。");
        chapter.Queue.Status = status;
        chapter.Queue.Elapsed = TimeSpan.FromSeconds(3723);
        chapter.Queue.LastError = status == QueueStatus.Faulted ? "Saved failure" : null;
        f.Attach(chapter);
        CheckButtons(f);
        long clones = Counter(f.Service, "FullSnapshotCount");
        long reads = Counter(f.Service, "LightweightSnapshotCount");
        string progress = ((TextBlock)f.Window.FindName("QueueInfo")).Text;
        for (int i = 0; i < 20; i++) f.Call("OnProgressTimerTick", null, EventArgs.Empty);
        Assert.Equal(reads, Counter(f.Service, "LightweightSnapshotCount"));
        Assert.Equal(clones, Counter(f.Service, "FullSnapshotCount"));
        Assert.Equal(progress, ((TextBlock)f.Window.FindName("QueueInfo")).Text);
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task SaveWritesOneChapterAndAtMostOneManifest(bool project, bool autosave) => StaHost.Run(async () =>
    {
        using var f = new WorkspaceFixture();
        ChapterDocument chapter;
        if (project)
        {
            await f.CreateProjectAsync();
            chapter = await f.AddChapterAsync("One write", 1);
        }
        else chapter = TranslationService.CreateChapter("Legacy", "你好。");
        f.Attach(chapter);
        await f.TaskCall("SaveCurrentAsync");
        f.Service.UpdateDraft(chapter.Segments[0].Id, "Saved draft");
        var commits = new List<string>();
        f.Get<ProjectRepository>("_projectRepository").ManifestCommitted += (_, change) => commits.Add(change.Operation);
        using (var capture = StorageIoMetricsCapture.Begin())
        {
            await f.TaskCall(autosave ? "AutosaveAsync" : "SaveCurrentAsync");
            var metrics = capture.Snapshot();
            // Catalog revision hashing also serializes the manifest without writing it.
            // Count durable writes/replacements, so that read-only UI work is excluded.
            Assert.Equal(project ? 2 : 1, metrics.FileWrites);
            Assert.Equal(project ? 2 : 1, metrics.FileReplacements);
        }
        if (project) Assert.Equal("UpdateChapter", Assert.Single(commits));
        else Assert.Empty(commits);
        var loaded = await f.Get<ChapterFileStore>("_store").LoadAsync(f.Service.SavePath);
        Assert.Equal("Saved draft", loaded.Chapter.Segments[0].UserDraft);
        if (project)
        {
            var persisted = JsonSerializer.Deserialize<ProjectManifest>(await File.ReadAllBytesAsync(f.Project.ManifestPath), DocumentJson.Options)!;
            Assert.Equal(ProjectChapterState.AwaitingReview, persisted.Chapters.Single().State);
            Assert.Equal(chapter.Id, persisted.CurrentChapterId);
            Assert.Equal(f.Project.Manifest.Chapters.Single().UpdatedAt, persisted.Chapters.Single().UpdatedAt);
        }
        Assert.False(f.Service.HasUnsavedChanges);
    });

    [Fact]
    public Task RenameOfAttachedProjectChapterDoesNotWriteTheSummaryTwice() => StaHost.Run(async () =>
    {
        using var f = new WorkspaceFixture();
        await f.CreateProjectAsync();
        var chapter = await f.AddChapterAsync("Before", 1);
        f.Attach(chapter);
        using var capture = StorageIoMetricsCapture.Begin();
        await f.Window.RenameProjectChapterAsync(chapter.Id, "After", "新名");
        Assert.Equal(2, capture.Snapshot().FileWrites);
        Assert.Equal("After", f.Project.Manifest.Chapters.Single().VietnameseTitle);
    });

    private static long Counter(TranslationService service, string name) =>
        (long)typeof(TranslationService).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

    private static void CheckButtons(WorkspaceFixture f)
    {
        // Freeze only the test clock so the rendered elapsed label can be compared exactly.
        var queue = f.Get<TranslationQueue>("_queue");
        var watch = (Stopwatch?)typeof(TranslationQueue).GetField("_watch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(queue);
        bool ticking = watch?.IsRunning == true;
        watch?.Stop();
        try { CheckButtonsAndProgress(f); }
        finally { if (ticking) watch!.Start(); }
    }

    private static void CheckButtonsAndProgress(WorkspaceFixture f)
    {
        f.Call("Refresh");
        var chapter = f.Service.GetSnapshot();
        var selected = chapter.Segments.Single(s => s.Id == f.Get<Guid?>("_selectedId"));
        bool busy = f.Get<bool>("_busy"), running = f.Get<TranslationQueue>("_queue").IsRunning;
        var context = new EditorActionContext
        {
            ChapterSegmented = true, RawSourceTextPresent = true, PresetsReady = true, UiAvailable = true,
            SingleRequestBusy = busy, LocalQueueStatus = chapter.Queue.Status,
            LocalQueueRunning = running && chapter.Queue.Status == QueueStatus.Running,
            SegmentCounts = Enum.GetValues<SegmentStatus>().ToDictionary(s => s, s => chapter.Segments.Count(x => x.Status == s)),
            RequiresRetranslationCount = chapter.Segments.Count(s => s.RequiresRetranslation),
            SelectedSegmentStatus = selected.Status,
            SelectedSegmentHasEditableDraft = !string.IsNullOrWhiteSpace(selected.EditableText)
        };
        var expected = EditorActionPolicy.Decide(context);
        var actual = f.Get<EditorActionPlan>("_currentEditorActionPlan");
        Assert.Equal(expected.Primary, actual.Primary);
        Assert.Equal(expected.Secondary, actual.Secondary);
        Assert.Equal(expected.Overflow, actual.Overflow);
        // Queue pause/resume live in a menu (MenuItem) since the simplified shell; IsEnabled is all that is checked.
        Control Button(string name) => (Control)f.Window.FindName(name);
        bool active = busy || running;
        Assert.Equal(!active, Button("TranslateButton").IsEnabled);
        Assert.Equal(selected.Status != SegmentStatus.Translating && !string.IsNullOrWhiteSpace(selected.EditableText), Button("ApproveButton").IsEnabled);
        Assert.Equal(busy, Button("CancelButton").IsEnabled);
        Assert.Equal(running && chapter.Queue.Status == QueueStatus.Running, Button("PauseQueueButton").IsEnabled);
        Assert.Equal(!active && chapter.Queue.Status is QueueStatus.Paused or QueueStatus.Interrupted or QueueStatus.Cancelled or QueueStatus.Faulted,
            Button("ResumeQueueButton").IsEnabled);
        Assert.Equal(!active && chapter.Segments.SelectMany(s => s.NormalizationHistory).Any(r => !r.Reverted), Button("UndoPronounsButton").IsEnabled);
        Assert.Equal(!active && chapter.BatchReplacementHistory.Any(r => !r.Reverted && !r.Automatic), Button("UndoBatchReplaceButton").IsEnabled);
        Assert.Equal(!active && chapter.SegmentationHistory.Count > 0, Button("RestoreStructureButton").IsEnabled);
        Assert.False(f.Text("DraftText").IsReadOnly);
        Assert.Equal(selected.Attempts.Count == 0 ? "Dịch đoạn đang chọn" : "Dịch lại đoạn đang chọn", ((Button)Button("TranslateButton")).Content);
        Assert.Equal($"{chapter.Segments.Count(s => s.Status == SegmentStatus.Approved)}/{chapter.Segments.Count} đã duyệt  ·  " +
            $"{chapter.Segments.Count(s => s.Status == SegmentStatus.AwaitingReview)} chờ duyệt  ·  " +
            $"{chapter.Segments.Count(s => s.Status == SegmentStatus.InspectionRequired || s.RequiresRetranslation)} cần kiểm tra  ·  " +
            $"{chapter.Segments.Count(s => s.Status == SegmentStatus.Error)} lỗi  ·  " +
            $"{chapter.Segments.Count(s => s.Status == SegmentStatus.Untranslated)} chưa dịch",
            ((TextBlock)f.Window.FindName("EditorContentStateText")).Text);
        var progress = f.Get<TranslationQueue>("_queue").GetProgress();
        Assert.Equal(100d * progress.Translated / progress.Total, ((ProgressBar)f.Window.FindName("ChapterProgress")).Value);
        var label = (string)typeof(MainWindow).GetMethod("QueueLabel", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [progress.Status])!;
        Assert.Equal($"{label} · đã có bản dịch {progress.Translated}/{progress.Total} · lỗi {progress.Errors} · cần kiểm tra {progress.NeedsInspection}" +
            (progress.CurrentOrder is int order ? $" · đang dịch đoạn {order}" : "") +
            $"\nĐã chạy {progress.Elapsed:hh\\:mm\\:ss} · còn khoảng " +
            (progress.EstimatedRemaining is TimeSpan eta ? eta.ToString(@"hh\:mm\:ss") : "chưa đủ số liệu") +
            (progress.Error is null ? "" : "\n" + progress.Error), ((TextBlock)f.Window.FindName("QueueInfo")).Text);
    }
}
