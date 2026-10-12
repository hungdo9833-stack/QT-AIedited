using System.Text;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SafeBulkImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-m2-bulk-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(500)]
    [InlineData(5000)]
    public async Task Imports_1_50_500_or_5000_chapters_and_reopens_without_orphans(int count)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "p-" + count), "Synthetic " + count);
        ImportPreviewPlan plan = Plan(count);
        var service = new SafeBulkImportService(repository);

        BulkImportReport report = await service.ImportAsync(session, plan, Defaults(),
            new BulkImportOptions(MaxChaptersPerChunk: 500, MaxUtf8BytesPerChunk: 4 * 1024 * 1024));

        Assert.True(report.Completed);
        Assert.Equal(count, report.ImportedCount);
        Assert.True(File.Exists(report.SourceArchivePath));
        Assert.Equal(plan.Source.RawSha256, report.SourceRawSha256);
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.Equal(count, reopened.Manifest.ChapterCount);
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Empty(reopened.RecoveryReport.Orphans);
        Assert.Equal(count, Directory.GetFiles(reopened.Resolve("Chapters"), "*.qtaie.json").Length);
    }

    [Fact]
    public async Task Preflight_collision_rejects_everything_before_source_rescue_or_chapter_write()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "collision"), "Synthetic collision");
        ChapterDocument existing = TranslationService.CreateChapter("Có sẵn", "原文。");
        await repository.AddChapterAsync(session, existing, 1);
        byte[] existingBytes = File.ReadAllBytes(session.ChapterStore.GetPath(existing.Id));

        BulkImportValidationException error = await Assert.ThrowsAsync<BulkImportValidationException>(() =>
            new SafeBulkImportService(repository).ImportAsync(session, Plan(2), Defaults()));

        Assert.Contains(error.Issues, x => x.Contains("số chương 1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(existingBytes, File.ReadAllBytes(session.ChapterStore.GetPath(existing.Id)));
        Assert.Single(session.Manifest.Chapters);
        Assert.Empty(Directory.GetFiles(session.Resolve("Backups"), "detected-source-*.zip"));
    }

    [Fact]
    public async Task Existing_approved_draft_history_and_project_glossary_are_byte_identical_after_import()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "protected"), "Synthetic protected");
        ChapterDocument protectedChapter = TranslationService.CreateChapter("Đã biên tập", "原文。第二句。");
        await repository.AddChapterAsync(session, protectedChapter, 1);
        protectedChapter = (await repository.LoadChapterAsync(session, protectedChapter.Id)).Chapter;
        TranslationSegment segment = protectedChapter.Segments[0];
        segment.UserDraft = "Bản nháp người dùng"; segment.DraftRevision = 3;
        segment.DraftHistory.Add(new() { RecordedAt = DateTimeOffset.UtcNow, Revision = 2, Text = "Lịch sử", Reason = "test" });
        segment.Approvals.Add(new() { ApprovedAt = DateTimeOffset.UtcNow, DraftRevision = 3, Text = "Bản đã duyệt" });
        segment.Status = SegmentStatus.Approved;
        await session.ChapterStore.SaveAsync(protectedChapter);
        var resources = await new ProjectResourceStore(session).LoadAsync();
        resources.Terms.Add(new("王", "Vương"));
        await new ProjectResourceStore(session).SaveAsync(resources);
        string chapterPath = session.ChapterStore.GetPath(protectedChapter.Id);
        string resourcesPath = session.Resolve("Settings/resources.json");
        byte[] chapterBefore = File.ReadAllBytes(chapterPath), resourcesBefore = File.ReadAllBytes(resourcesPath);

        ImportPreviewPlan plan = Plan(3, firstNumber: 10);
        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults());

        Assert.True(report.Completed);
        Assert.Equal(chapterBefore, File.ReadAllBytes(chapterPath));
        Assert.Equal(resourcesBefore, File.ReadAllBytes(resourcesPath));
        ChapterDocument reloaded = (await repository.LoadChapterAsync(session, protectedChapter.Id)).Chapter;
        Assert.Equal("Bản nháp người dùng", reloaded.Segments[0].UserDraft);
        Assert.Single(reloaded.Segments[0].Approvals);
        Assert.Single(reloaded.Segments[0].DraftHistory);
    }

    [Theory]
    [InlineData("AfterJournal")]
    [InlineData("AfterFile:1")]
    [InlineData("BeforeCommit")]
    public async Task Fault_at_start_middle_or_end_rolls_back_the_entire_chunk(string point)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "fault-" + point.Replace(':', '-')), "Synthetic fault");
        repository.FaultPoint = value => { if (value == point) throw new IOException("There is not enough space on the disk. Injected."); };

        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, Plan(5), Defaults(),
            new BulkImportOptions(5, 1024 * 1024));

        Assert.False(report.Completed);
        Assert.Equal(0, report.ImportedCount);
        Assert.All(report.Chapters, x => Assert.Contains(x.State, new[] { BulkImportChapterState.RolledBack, BulkImportChapterState.NotStarted }));
        Assert.Empty(session.Manifest.Chapters);
        Assert.Empty(Directory.GetFiles(session.Resolve("Chapters"), "*.qtaie.json"));
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Empty(reopened.RecoveryReport.Orphans);
    }

    [Fact]
    public async Task Cancel_between_chunks_reports_partial_success_and_retry_is_idempotent()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "cancel"), "Synthetic cancel");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.CompletedChunks == 1) cancellation.Cancel(); });
        var service = new SafeBulkImportService(repository);
        ImportPreviewPlan plan = Plan(7);

        BulkImportReport first = await service.ImportAsync(session, plan, Defaults(), new BulkImportOptions(3, 1024 * 1024), progress, cancellation.Token);
        Assert.False(first.Completed);
        Assert.Equal(3, first.ImportedCount);
        Assert.Equal(4, first.Chapters.Count(x => x.State == BulkImportChapterState.NotStarted));

        BulkImportReport retry = await service.ImportAsync(session, plan, Defaults(), new BulkImportOptions(3, 1024 * 1024));
        Assert.True(retry.Completed);
        Assert.Equal(3, retry.AlreadyImportedCount);
        Assert.Equal(4, retry.ImportedCount);
        Assert.Equal(7, session.Manifest.ChapterCount);
        Assert.Equal(7, session.Manifest.Chapters.Select(x => x.ChapterId).Distinct().Count());
    }

    [Fact]
    public async Task Simulated_crash_reopens_with_recovery_then_same_plan_resumes_without_duplicates()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "crash"), "Synthetic crash");
        repository.FaultPoint = point => { if (point == "BeforeCommit") throw new ProjectOperationInterruptedException(point); };
        var service = new SafeBulkImportService(repository);
        ImportPreviewPlan plan = Plan(4);

        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => service.ImportAsync(session, plan, Defaults(), new BulkImportOptions(4, 1024 * 1024)));
        var afterCrashRepository = new ProjectRepository();
        ProjectSession reopened = (await afterCrashRepository.OpenAsync(session.ManifestPath)).Session;
        ProjectTransactionPreview pending = Assert.Single(reopened.RecoveryReport.Transactions,
            x => x.State == ProjectTransactionState.Pending);
        await afterCrashRepository.RecoverTransactionAsync(reopened, pending.Id, ProjectRecoveryDecision.RollBack);

        BulkImportReport retry = await new SafeBulkImportService(afterCrashRepository).ImportAsync(reopened, plan, Defaults(), new BulkImportOptions(4, 1024 * 1024));
        Assert.True(retry.Completed);
        Assert.Equal(4, reopened.Manifest.ChapterCount);
        Assert.Equal(4, reopened.Manifest.Chapters.Select(x => x.ChapterId).Distinct().Count());
    }

    private static ImportPreviewPlan Plan(int count, int firstNumber = 1)
    {
        string source = string.Concat(Enumerable.Range(firstNumber, count).Select(number => $"第{number}章 标题{number}\n原文{number}。\n"));
        return ImportPreviewEditor.Create(new ChapterDetector().Detect(source, $"synthetic-{count}.txt")).Current;
    }

    private static BulkImportChapterDefaults Defaults() => new(new(), new(), PromptPreset.Default);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class InlineProgress(Action<BulkImportProgress> action) : IProgress<BulkImportProgress>
    {
        public void Report(BulkImportProgress value) => action(value);
    }
}
