using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class AuditPhase2PerformanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-audit-phase2-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task F03_F04_QueuePersistsEverySegmentWithoutWritingManifestOrChangingEditorChapter()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Synthetic");
        ChapterDocument background = TranslationService.CreateChapter("Nền", "甲乙丙丁戊。\r\n己庚辛壬癸。",
            new SegmenterOptions { MinimumHanCharacters = 3, TargetHanCharacters = 5, MaximumHanCharacters = 6 });
        ProjectChapterEntry first = await repository.AddChapterAsync(session, background, 1, "第一章");
        ChapterDocument editor = TranslationService.CreateChapter("Đang mở", "天地玄黄。",
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 10, MaximumHanCharacters = 20 });
        ProjectChapterEntry second = await repository.AddChapterAsync(session, editor, 2, "第二章");
        background = (await repository.LoadChapterAsync(session, first.ChapterId)).Chapter;
        var service = new TranslationService(background, new FakeClient(),
            new TranslationChapterStore(session, repository, background));

        using StorageIoMetricsCapture capture = StorageIoMetricsCapture.Begin();
        await new TranslationQueue(service).StartAsync();
        StorageIoMetricsSnapshot io = capture.Snapshot();

        Assert.Equal(second.ChapterId, session.Manifest.CurrentChapterId);
        Assert.True(io.FileWrites <= 4, $"Two durable segment checkpoints wrote {io.FileWrites} files.");
        ChapterDocument persisted = (await repository.LoadChapterAsync(session, first.ChapterId)).Chapter;
        Assert.Equal(QueueStatus.Completed, persisted.Queue.Status);
        Assert.All(persisted.Segments, segment => Assert.True(segment.HasCompletedTranslation));
    }

    [Fact]
    public void F10_QueueJobCloneIsIndependentWithoutLargeJsonAllocation()
    {
        ProjectTranslationJob source = SyntheticJob(5_000);
        _ = ProjectTranslationJobStore.Clone(source);
        long before = GC.GetAllocatedBytesForCurrentThread();

        ProjectTranslationJob clone = ProjectTranslationJobStore.Clone(source);

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        clone.Items[0].Title = "Đã đổi";
        clone.Items[0].EligibilityDiagnostics[0].ReasonCounts[QueueEligibilityReason.EligibleNeverAttempted] = 2;
        Assert.NotEqual(clone.Items[0].Title, source.Items[0].Title);
        Assert.Equal(1, source.Items[0].EligibilityDiagnostics[0].ReasonCounts[QueueEligibilityReason.EligibleNeverAttempted]);
        Assert.True(allocated < 4_000_000, $"Clone 5,000 queue items allocated {allocated:N0} bytes.");
    }

    [Fact]
    public async Task F04_DifferentGpuChaptersCanReachDurableWritesConcurrently()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Parallel synthetic");
        ChapterDocument first = TranslationService.CreateChapter("Một", "甲乙丙丁。",
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 10, MaximumHanCharacters = 20 });
        ChapterDocument second = TranslationService.CreateChapter("Hai", "天地玄黄。",
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 10, MaximumHanCharacters = 20 });
        await repository.AddChapterAsync(session, first, 1, "第一章");
        await repository.AddChapterAsync(session, second, 2, "第二章");
        first = (await repository.LoadChapterAsync(session, first.Id)).Chapter;
        second = (await repository.LoadChapterAsync(session, second.Id)).Chapter;
        string firstSource = ProjectTranslationFingerprints.Source(first);
        string firstProtected = ProjectTranslationFingerprints.Protected(first);
        string secondSource = ProjectTranslationFingerprints.Source(second);
        string secondProtected = ProjectTranslationFingerprints.Protected(second);
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        repository.FaultPoint = point =>
        {
            if (point != "BeforeTranslatedChapterWrite") return;
            entered.Signal();
            release.Wait(TimeSpan.FromSeconds(5));
        };

        Task saveFirst = Task.Run(() => repository.UpdateTranslatedChapterAsync(session, first,
            firstSource, firstProtected, CancellationToken.None));
        Task saveSecond = Task.Run(() => repository.UpdateTranslatedChapterAsync(session, second,
            secondSource, secondProtected, CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(2)), "Hai GPU vẫn bị tuần tự hóa bởi khóa toàn Project.");
        release.Set();
        await Task.WhenAll(saveFirst, saveSecond);
    }

    private static ProjectTranslationJob SyntheticJob(int count)
    {
        const string fingerprint = "0000000000000000000000000000000000000000000000000000000000000000";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new ProjectTranslationJob
        {
            ProjectId = Guid.NewGuid(), Status = ProjectTranslationJobStatus.Running,
            CreatedAt = now, UpdatedAt = now,
            Backend = OllamaBackendSnapshot.LocalDefault("translategemma:12b"),
            Items = Enumerable.Range(1, count).Select(number => new ProjectTranslationItem
            {
                ChapterId = Guid.NewGuid(), Number = number, Order = number, Title = "Chương " + number,
                SourceFingerprint = fingerprint, ProtectedFingerprint = fingerprint,
                TotalSegments = 1,
                EligibilityDiagnostics = [new TranslationEligibilityDiagnostic
                {
                    Stage = TranslationEligibilityDiagnosticStage.Plan, ChapterId = Guid.Empty,
                    ChapterNumber = number, QueueMode = QueueMode.MissingSegments, SegmentCount = 1,
                    EligibleSegmentCount = 1,
                    ReasonCounts = new() { [QueueEligibilityReason.EligibleNeverAttempted] = 1 }
                }]
            }).Select(item =>
            {
                item.EligibilityDiagnostics[0] = item.EligibilityDiagnostics[0] with { ChapterId = item.ChapterId };
                return item;
            }).ToList()
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
