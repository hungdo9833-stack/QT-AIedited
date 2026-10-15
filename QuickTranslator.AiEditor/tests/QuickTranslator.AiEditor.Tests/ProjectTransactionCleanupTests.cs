using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectTransactionCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-storage-cleanup-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Successful_checkpoints_do_not_accumulate_completed_transaction_directories()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Storage synthetic");
        var chapter = TranslationService.CreateChapter("Chương thử", string.Concat(Enumerable.Repeat("天地玄黄。", 100)));
        await repository.AddChapterAsync(session, chapter, 1);
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;

        for (int index = 0; index < 10; index++)
        {
            chapter.ConfirmedContextNotes = "checkpoint " + index;
            await repository.UpdateChapterAsync(session, chapter);
        }

        string transactions = session.Resolve("History/Transactions");
        Assert.Empty(Directory.EnumerateDirectories(transactions));
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.False(reopened.RecoveryReport.RequiresDecision);
    }

    [Fact]
    public async Task Interrupted_transaction_keeps_recovery_copies()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Crash synthetic");
        var chapter = TranslationService.CreateChapter("Chương thử", "天地玄黄。宇宙洪荒。");
        repository.FaultPoint = point =>
        {
            if (point == "AfterJournal") throw new ProjectOperationInterruptedException(point);
        };

        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => repository.AddChapterAsync(session, chapter, 1));

        string directory = Assert.Single(Directory.EnumerateDirectories(session.Resolve("History/Transactions")));
        Assert.True(File.Exists(Path.Combine(directory, "journal.json")));
        Assert.NotEmpty(Directory.EnumerateFiles(directory, "before-*.bin"));
        Assert.NotEmpty(Directory.EnumerateFiles(directory, "after-*.bin"));
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.True(reopened.RecoveryReport.RequiresDecision);
        Assert.Contains(reopened.RecoveryReport.Transactions, item => item.State == ProjectTransactionState.Pending);
    }

    [Fact]
    public async Task Cleanup_deletes_only_verified_terminal_transactions()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Cleanup mixture synthetic");
        var chapter = TranslationService.CreateChapter("Chương thử", "天地玄黄。宇宙洪荒。");
        string committed = await KeepCommittedDirectoryAsync(repository, session, chapter);
        string rolledBack = await KeepRolledBackDirectoryAsync(repository, session, chapter.Id);

        repository.FaultPoint = point =>
        {
            if (point == "AfterJournal") throw new ProjectOperationInterruptedException(point);
        };
        ChapterDocument pendingChapter = TranslationService.CreateChapter("Chương pending", "天地玄黄。");
        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => repository.AddChapterAsync(session, pendingChapter, 2));
        repository.FaultPoint = null;

        string pending = Directory.EnumerateDirectories(session.Resolve("History/Transactions"))
            .Single(path => path != committed && path != rolledBack);
        File.WriteAllText(Path.Combine(rolledBack, "retained-user-data.bin"), "must keep");
        string unreadable = Path.Combine(session.Resolve("History/Transactions"), Guid.NewGuid().ToString("D"));
        Directory.CreateDirectory(unreadable);
        File.WriteAllText(Path.Combine(unreadable, "journal.json"), "not-json");

        ProjectTransactionCleanupPreview preview = await repository.PreviewTransactionCleanupAsync(session);
        Assert.Equal(4, preview.TotalCount);
        Assert.Equal(1, preview.DeletableCount);
        Assert.Contains(preview.Items, item => item.Id == Guid.Parse(Path.GetFileName(committed)) && item.CanDelete);
        Assert.Contains(preview.Items, item => item.Disposition == ProjectTransactionCleanupDisposition.KeptRetainedData);
        Assert.Contains(preview.Items, item => item.Disposition == ProjectTransactionCleanupDisposition.KeptPending);
        Assert.Contains(preview.Items, item => item.Disposition == ProjectTransactionCleanupDisposition.KeptUnreadable);

        ProjectTransactionCleanupResult result = await repository.CleanupTransactionsAsync(session);
        Assert.Equal(1, result.DeletedCount);
        Assert.True(result.FreedBytes > 0);
        Assert.False(Directory.Exists(committed));
        Assert.True(Directory.Exists(rolledBack));
        Assert.True(Directory.Exists(pending));
        Assert.True(Directory.Exists(unreadable));
    }

    [Fact]
    public async Task Cleanup_can_retry_after_locked_file_without_changing_project_data()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Locked cleanup synthetic");
        var chapter = TranslationService.CreateChapter("Chương thử", "天地玄黄。宇宙洪荒。");
        chapter.Segments[0].UserDraft = "Bản nháp đã duyệt";
        chapter.Segments[0].DraftRevision = 2;
        chapter.Segments[0].Status = SegmentStatus.Approved;
        chapter.Segments[0].Approvals.Add(new() { Text = "Bản nháp đã duyệt", DraftRevision = 2 });
        chapter.ConfirmedTerms = [new("天地", "Thiên địa")];
        string committed = await KeepCommittedDirectoryAsync(repository, session, chapter);
        var resources = await new ProjectResourceStore(session).LoadAsync();
        resources.Terms.Add(new("宇宙", "Vũ trụ"));
        resources.Context = "Ngữ cảnh synthetic phải giữ nguyên";
        await new ProjectResourceStore(session).SaveAsync(resources);
        Dictionary<string, byte[]> projectBefore = CaptureProjectData(session);
        string copy = Directory.EnumerateFiles(committed, "before-*.bin").First();

        ProjectTransactionCleanupResult blocked;
        using (new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read))
            blocked = await repository.CleanupTransactionsAsync(session);

        Assert.Equal(0, blocked.DeletedCount);
        Assert.NotEmpty(blocked.Failures);
        Assert.True(Directory.Exists(committed));
        AssertProjectData(projectBefore, CaptureProjectData(session));

        ProjectTransactionCleanupResult retried = await repository.CleanupTransactionsAsync(session);
        Assert.Equal(1, retried.DeletedCount);
        Assert.False(Directory.Exists(committed));
        AssertProjectData(projectBefore, CaptureProjectData(session));
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        ChapterDocument reopenedChapter = (await new ProjectRepository().LoadChapterAsync(reopened, chapter.Id)).Chapter;
        Assert.Equal(chapter.OriginalText, reopenedChapter.OriginalText);
        Assert.Equal("Bản nháp đã duyệt", reopenedChapter.Segments[0].UserDraft);
        Assert.Equal(SegmentStatus.Approved, reopenedChapter.Segments[0].Status);
        Assert.Single(reopenedChapter.Segments[0].Approvals);
        Assert.Equal("Vũ trụ", (await new ProjectResourceStore(reopened).LoadAsync()).Terms.Single().Vietnamese);
    }

    [Fact]
    public async Task Completed_journal_does_not_hash_obsolete_copies_when_opening_project()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Open optimized synthetic");
        var chapter = TranslationService.CreateChapter("Chương thử", "天地玄黄。宇宙洪荒。");
        string committed = await KeepCommittedDirectoryAsync(repository, session, chapter);
        File.WriteAllText(Directory.EnumerateFiles(committed, "before-*.bin").First(), "obsolete-copy-corrupted");

        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;

        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Empty(reopened.RecoveryReport.Transactions);
        ProjectTransactionCleanupPreview preview = await repository.PreviewTransactionCleanupAsync(reopened);
        Assert.Single(preview.Items, item => item.CanDelete);
    }

    private static async Task<string> KeepCommittedDirectoryAsync(ProjectRepository repository, ProjectSession session,
        ChapterDocument chapter)
    {
        FileStream? locked = null;
        repository.FaultPoint = point =>
        {
            if (point != "BeforeCommit") return;
            string directory = Directory.EnumerateDirectories(session.Resolve("History/Transactions"))
                .OrderByDescending(Directory.GetCreationTimeUtc).First();
            locked = new FileStream(Directory.EnumerateFiles(directory, "before-*.bin").First(),
                FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        await repository.AddChapterAsync(session, chapter, 1);
        locked!.Dispose();
        repository.FaultPoint = null;
        return Assert.Single(Directory.EnumerateDirectories(session.Resolve("History/Transactions")));
    }

    private static async Task<string> KeepRolledBackDirectoryAsync(ProjectRepository repository, ProjectSession session,
        Guid chapterId)
    {
        FileStream? locked = null;
        repository.FaultPoint = point =>
        {
            if (point != "AfterJournal") return;
            string directory = Directory.EnumerateDirectories(session.Resolve("History/Transactions"))
                .OrderByDescending(Directory.GetCreationTimeUtc).First();
            locked = new FileStream(Directory.EnumerateFiles(directory, "before-*.bin").First(),
                FileMode.Open, FileAccess.Read, FileShare.Read);
            throw new IOException("synthetic rollback");
        };
        await Assert.ThrowsAsync<IOException>(() => repository.SoftDeleteChapterAsync(session, chapterId));
        locked!.Dispose();
        repository.FaultPoint = null;
        return Directory.EnumerateDirectories(session.Resolve("History/Transactions"))
            .OrderByDescending(Directory.GetCreationTimeUtc).First();
    }

    private static Dictionary<string, byte[]> CaptureProjectData(ProjectSession session) =>
        Directory.EnumerateFiles(session.RootDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.Combine("History", "Transactions"), StringComparison.OrdinalIgnoreCase))
            .ToDictionary(path => Path.GetRelativePath(session.RootDirectory, path), File.ReadAllBytes,
                StringComparer.OrdinalIgnoreCase);

    private static void AssertProjectData(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), actual.Keys.Order(StringComparer.OrdinalIgnoreCase));
        foreach ((string path, byte[] bytes) in expected) Assert.Equal(bytes, actual[path]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
