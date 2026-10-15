using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class FastCheckpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-fast-checkpoint-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Ordinary_chapter_checkpoint_does_not_enter_multi_file_transaction_journal()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Fast checkpoint synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương thử", "天地玄黄。宇宙洪荒。");
        await repository.AddChapterAsync(session, chapter, 1);
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        var faultPoints = new List<string>();
        repository.FaultPoint = faultPoints.Add;

        chapter.Segments[0].UserDraft = "Bản sửa tay phải được giữ";
        chapter.Segments[0].DraftRevision++;
        chapter.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await repository.UpdateChapterAsync(session, chapter);

        Assert.DoesNotContain("BeforeJournal", faultPoints);
        Assert.Empty(Directory.EnumerateDirectories(session.Resolve("History/Transactions")));
        ChapterDocument reopened = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        Assert.Equal("Bản sửa tay phải được giữ", reopened.Segments[0].UserDraft);
    }

    [Fact]
    public async Task Crash_after_chapter_before_manifest_reconciles_summary_on_reopen()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Manifest reconcile synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Tên cũ", "天地玄黄。宇宙洪荒。");
        await repository.AddChapterAsync(session, chapter, 1);
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        chapter.Title = "Tên mới đã checkpoint";
        chapter.Segments[0].UserDraft = "Kết quả AI đã hoàn thành";
        chapter.Segments[0].DraftRevision++;
        chapter.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(2);
        repository.FaultPoint = point =>
        {
            if (point == "AfterFastChapterWrite") throw new ProjectOperationInterruptedException(point);
        };

        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => repository.UpdateChapterAsync(session, chapter));

        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        ProjectChapterEntry entry = Assert.Single(reopened.Manifest.Chapters);
        Assert.Equal("Tên mới đã checkpoint", entry.VietnameseTitle);
        ChapterDocument saved = (await new ProjectRepository().LoadChapterAsync(reopened, chapter.Id)).Chapter;
        Assert.Equal("Kết quả AI đã hoàn thành", saved.Segments[0].UserDraft);
    }

    [Fact]
    public async Task Crash_before_chapter_write_keeps_previous_chapter_and_manifest()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Pre-write crash synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Tên ổn định", "天地玄黄。宇宙洪荒。");
        await repository.AddChapterAsync(session, chapter, 1);
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        chapter.Title = "Tên chưa commit";
        repository.FaultPoint = point =>
        {
            if (point == "BeforeFastChapterWrite") throw new ProjectOperationInterruptedException(point);
        };

        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => repository.UpdateChapterAsync(session, chapter));

        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.Equal("Tên ổn định", Assert.Single(reopened.Manifest.Chapters).VietnameseTitle);
        Assert.Equal("Tên ổn định", (await new ProjectRepository().LoadChapterAsync(reopened, chapter.Id)).Chapter.Title);
    }

    [Fact]
    public async Task Crash_after_manifest_write_reopens_committed_checkpoint()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Post-manifest crash synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Tên cũ", "天地玄黄。宇宙洪荒。");
        await repository.AddChapterAsync(session, chapter, 1);
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        chapter.Title = "Tên đã ghi manifest";
        chapter.Segments[0].UserDraft = "Kết quả đã commit";
        chapter.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        repository.FaultPoint = point =>
        {
            if (point == "AfterFastManifestWrite") throw new ProjectOperationInterruptedException(point);
        };

        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => repository.UpdateChapterAsync(session, chapter));

        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.Equal("Tên đã ghi manifest", Assert.Single(reopened.Manifest.Chapters).VietnameseTitle);
        Assert.Equal("Kết quả đã commit",
            (await new ProjectRepository().LoadChapterAsync(reopened, chapter.Id)).Chapter.Segments[0].UserDraft);
    }

    [Fact]
    public async Task Failure_after_temporary_flush_leaves_previous_primary_and_cleans_temp_file()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Temporary write synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Tên ổn định", "天地玄黄。宇宙洪荒。");
        await repository.AddChapterAsync(session, chapter, 1);
        string path = session.ChapterStore.GetPath(chapter.Id);
        byte[] before = File.ReadAllBytes(path);
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        chapter.Title = "Tên chưa thay primary";
        AtomicLocalFile.FaultPoint.Value = point =>
        {
            if (point == "AfterTemporaryFlush") throw new IOException("Synthetic process loss while temp is durable");
        };
        try
        {
            await Assert.ThrowsAsync<IOException>(() => repository.UpdateChapterAsync(session, chapter));
        }
        finally { AtomicLocalFile.FaultPoint.Value = null; }

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".tmp-*"));
        Assert.Equal("Tên ổn định", (await new ProjectRepository().LoadChapterAsync(session, chapter.Id)).Chapter.Title);
    }

    [Fact]
    public async Task Valid_backup_allows_recovery_when_primary_is_corrupt_before_fast_checkpoint()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Backup recovery synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương thử", "天地玄黄。宇宙洪荒。");
        await repository.AddChapterAsync(session, chapter, 1);
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        chapter.Segments[0].UserDraft = "Checkpoint thứ nhất";
        chapter.Segments[0].DraftRevision++;
        await repository.UpdateChapterAsync(session, chapter);
        string path = session.ChapterStore.GetPath(chapter.Id);
        File.WriteAllText(path, "primary-corrupt");
        chapter.Segments[0].UserDraft = "Checkpoint sau phục hồi";
        chapter.Segments[0].DraftRevision++;

        await repository.UpdateChapterAsync(session, chapter);

        ChapterDocument saved = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        Assert.Equal("Checkpoint sau phục hồi", saved.Segments[0].UserDraft);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".rescue-*.zip"));
    }

    [Fact]
    public async Task Revision_guarded_fast_checkpoint_rejects_stale_worker_without_transaction()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Revision synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương thử", "天地玄黄。宇宙洪荒。");
        await repository.AddChapterAsync(session, chapter, 1);
        string staleRevision = await repository.GetChapterStorageRevisionAsync(session, chapter.Id);
        ChapterDocument userEdit = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        ChapterDocument staleWorker = DocumentJson.Clone(userEdit);
        userEdit.Segments[0].UserDraft = "Bản người dùng sửa";
        userEdit.Segments[0].DraftRevision++;
        userEdit.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);
        await repository.UpdateChapterAsync(session, userEdit);
        staleWorker.Segments[0].Attempts.Add(new() { AiText = "Kết quả worker cũ", Outcome = AttemptOutcome.Completed });

        await Assert.ThrowsAsync<ProjectTranslationConflictException>(() =>
            repository.UpdateChapterIfStorageRevisionAsync(session, staleWorker, staleRevision));

        ChapterDocument current = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        Assert.Equal("Bản người dùng sửa", current.Segments[0].UserDraft);
        Assert.Empty(current.Segments[0].Attempts);
        Assert.Empty(Directory.EnumerateDirectories(session.Resolve("History/Transactions")));
    }

    [Fact]
    public async Task Fast_checkpoint_uses_two_atomic_writes_and_no_transaction_files()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "I/O metrics synthetic");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương thử", "天地玄黄。宇宙洪荒。");
        await repository.AddChapterAsync(session, chapter, 1);
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        chapter.ConfirmedContextNotes = "Checkpoint nhẹ";
        chapter.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1);

        using StorageIoMetricsCapture capture = StorageIoMetricsCapture.Begin();
        await repository.UpdateChapterAsync(session, chapter);
        StorageIoMetricsSnapshot metrics = capture.Snapshot();

        Assert.Equal(2, metrics.Serializations);
        Assert.Equal(2, metrics.FileReads);
        Assert.Equal(2, metrics.FileWrites);
        Assert.Equal(2, metrics.TemporaryFilesCreated);
        Assert.Equal(2, metrics.BackupReplacements);
        Assert.Equal(0, metrics.FileDeletes);
        Assert.Empty(Directory.EnumerateDirectories(session.Resolve("History/Transactions")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
