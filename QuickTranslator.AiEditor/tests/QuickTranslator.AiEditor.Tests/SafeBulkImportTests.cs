using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SafeBulkImportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-simple-bulk-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1)]
    [InlineData(50)]
    [InlineData(500)]
    [InlineData(5000)]
    public async Task Imports_large_corpora_reopens_and_creates_no_legacy_storage(int count)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "p-" + count), "Synthetic " + count);
        ImportPreviewPlan plan = Plan(count);
        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults(),
            new BulkImportOptions(500, 4 * 1024 * 1024));

        Assert.True(report.Completed);
        Assert.True(report.VerifiedComplete);
        Assert.Equal(count, report.ImportedCount);
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.Equal(count, reopened.Manifest.ChapterCount);
        Assert.Equal(count, Directory.GetFiles(reopened.Resolve("Chapters"), "*.qtaie.json").Length);
        Assert.False(Directory.Exists(reopened.Resolve("History/Transactions")));
        Assert.False(Directory.Exists(Path.Combine(reopened.RootDirectory, "Backups")));
        Assert.Empty(Directory.GetFiles(reopened.RootDirectory, "*.bak", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Collision_stops_before_writing_and_never_overwrites_existing_chapter()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "collision"), "Collision");
        ChapterDocument existing = TranslationService.CreateChapter("Có sẵn", "原文。");
        await repository.AddChapterAsync(session, existing, 1);
        byte[] before = await File.ReadAllBytesAsync(session.ChapterStore.GetPath(existing.Id));

        BulkImportValidationException error = await Assert.ThrowsAsync<BulkImportValidationException>(() =>
            new SafeBulkImportService(repository).ImportAsync(session, Plan(2), Defaults()));

        Assert.Contains(error.Issues, issue => issue.Contains("số chương 1", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, await File.ReadAllBytesAsync(session.ChapterStore.GetPath(existing.Id)));
        Assert.Single(session.Manifest.Chapters);
    }

    [Fact]
    public async Task Cancel_between_chunks_reports_partial_success_and_retry_is_idempotent()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "cancel"), "Cancel");
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(value => { if (value.CompletedChunks == 1) cancellation.Cancel(); });
        var service = new SafeBulkImportService(repository);
        ImportPreviewPlan plan = Plan(7);

        BulkImportReport first = await service.ImportAsync(session, plan, Defaults(),
            new BulkImportOptions(3, 1024 * 1024), progress, cancellation.Token);
        Assert.False(first.Completed);
        Assert.Equal(3, first.ImportedCount);
        Assert.Equal(4, first.Chapters.Count(item => item.State == BulkImportChapterState.NotStarted));

        BulkImportReport retry = await service.ImportAsync(session, plan, Defaults(), new BulkImportOptions(3, 1024 * 1024));
        Assert.True(retry.Completed);
        Assert.Equal(3, retry.AlreadyImportedCount);
        Assert.Equal(4, retry.ImportedCount);
        Assert.Equal(7, session.Manifest.ChapterCount);
    }

    [Fact]
    public async Task Existing_draft_approval_history_and_glossary_remain_byte_exact()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "protected"), "Protected");
        ChapterDocument chapter = TranslationService.CreateChapter("Đã biên tập", "原文。第二句。");
        await repository.AddChapterAsync(session, chapter, 1);
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        TranslationSegment segment = chapter.Segments[0];
        segment.UserDraft = "Bản nháp";
        segment.DraftRevision = 1;
        segment.DraftHistory.Add(new() { RecordedAt = DateTimeOffset.UtcNow, Revision = 1, Text = "Lịch sử", Reason = "test" });
        segment.Approvals.Add(new() { ApprovedAt = DateTimeOffset.UtcNow, DraftRevision = 1, Text = "Bản nháp" });
        segment.Status = SegmentStatus.Approved;
        await repository.UpdateChapterAsync(session, chapter);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        resources.Terms.Add(new("王", "Vương"));
        await new ProjectResourceStore(session).SaveAsync(resources);
        string chapterPath = session.ChapterStore.GetPath(chapter.Id);
        string resourcesPath = session.Resolve(session.Manifest.Files.Resources);
        byte[] chapterBefore = await File.ReadAllBytesAsync(chapterPath);
        byte[] resourcesBefore = await File.ReadAllBytesAsync(resourcesPath);

        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, Plan(3, 10), Defaults());

        Assert.True(report.Completed);
        Assert.Equal(chapterBefore, await File.ReadAllBytesAsync(chapterPath));
        Assert.Equal(resourcesBefore, await File.ReadAllBytesAsync(resourcesPath));
    }

    [Fact]
    public async Task Same_plan_after_success_is_recognized_without_duplicate_chapters()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "resume"), "Resume");
        ImportPreviewPlan plan = Plan(4);
        var service = new SafeBulkImportService(repository);
        Assert.True((await service.ImportAsync(session, plan, Defaults())).Completed);

        BulkImportReport retry = await service.ImportAsync(session, plan, Defaults());

        Assert.True(retry.Completed);
        Assert.Equal(4, retry.AlreadyImportedCount);
        Assert.Equal(0, retry.ImportedCount);
        Assert.Equal(4, session.Manifest.Chapters.Select(entry => entry.ChapterId).Distinct().Count());
    }

    [Fact]
    public async Task Explicit_soft_deleted_replacement_imports_same_stable_id()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "replace"), "Replace");
        ImportPreviewPlan plan = Plan(1);
        ImportPreviewItem item = Assert.Single(plan.ImportableChapters);
        ChapterDocument old = TranslationService.CreateChapter("Bản cũ", "旧内容。");
        old.Id = item.ChapterId;
        old.Segments = new TextSegmenter().Segment(old.OriginalText, old.Id, new()).ToList();
        old.SelectedSegmentId = old.Segments[0].Id;
        await repository.AddChapterAsync(session, old, item.Number!.Value);
        await repository.SoftDeleteChapterAsync(session, old.Id);

        await Assert.ThrowsAsync<BulkImportValidationException>(() =>
            new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults()));
        BulkImportReport result = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults(),
            new BulkImportOptions(ReplaceSoftDeletedHistory: true));

        Assert.True(result.Completed);
        Assert.Equal(1, session.Manifest.ChapterCount);
        Assert.Equal(0, session.Manifest.DeletedChapterCount);
        Assert.Equal(plan.Slice(item.ContentSpan),
            (await repository.LoadChapterAsync(session, item.ChapterId)).Chapter.OriginalText);
    }

    private static ImportPreviewPlan Plan(int count, int firstNumber = 1)
    {
        string source = string.Concat(Enumerable.Range(firstNumber, count)
            .Select(number => $"第{number}章 标题{number}\n原文{number}。\n"));
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
