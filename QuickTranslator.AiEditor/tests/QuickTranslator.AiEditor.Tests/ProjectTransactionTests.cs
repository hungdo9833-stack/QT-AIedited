using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectTransactionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-phase3-transaction-" + Guid.NewGuid());
    private readonly ProjectRepository _repository = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F07_LockedBackupCannotLeaveMainFileMoved(bool restore)
    {
        var (session, id) = await ChapterWithBackupAsync();
        if (restore) await _repository.SoftDeleteChapterAsync(session, id);
        string source = session.Resolve(session.Manifest.Chapters.Single().RelativePath);
        var before = JsonSerializer.Serialize(session.Manifest, DocumentJson.Options);
        byte[] primary = File.ReadAllBytes(source), backup = File.ReadAllBytes(source + ".bak");
        using (var locked = new FileStream(source + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => restore
                ? _repository.RestoreDeletedChapterAsync(session, id) : _repository.SoftDeleteChapterAsync(session, id));
            Assert.True(File.Exists(source));
            Assert.Equal(primary, File.ReadAllBytes(source));
            Assert.Equal(backup, File.ReadAllBytes(source + ".bak"));
            Assert.Equal(before, JsonSerializer.Serialize(session.Manifest, DocumentJson.Options));
        }
        var reopened = (await _repository.OpenAsync(session.ManifestPath)).Session;
        Assert.Equal(before, JsonSerializer.Serialize(reopened.Manifest, DocumentJson.Options));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F07_FailedManifestWriteRestoresAllManifestFieldsAndFiles(bool restore)
    {
        var (session, id) = await ChapterWithBackupAsync();
        if (restore) await _repository.SoftDeleteChapterAsync(session, id);
        string source = session.Resolve(session.Manifest.Chapters.Single().RelativePath);
        string before = JsonSerializer.Serialize(session.Manifest, DocumentJson.Options);
        byte[] primary = File.ReadAllBytes(source), backup = File.ReadAllBytes(source + ".bak");
        using (var locked = new FileStream(session.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => restore
                ? _repository.RestoreDeletedChapterAsync(session, id) : _repository.SoftDeleteChapterAsync(session, id));
        Assert.Equal(before, JsonSerializer.Serialize(session.Manifest, DocumentJson.Options));
        Assert.Equal(primary, File.ReadAllBytes(source)); Assert.Equal(backup, File.ReadAllBytes(source + ".bak"));
        Assert.Equal(before, JsonSerializer.Serialize((await _repository.OpenAsync(session.ManifestPath)).Session.Manifest, DocumentJson.Options));
    }

    [Fact]
    public async Task F07_RestoreBackupDestinationCollisionMustBeRejectedBeforeMovingPrimary()
    {
        var (session, id) = await ChapterWithBackupAsync();
        await _repository.SoftDeleteChapterAsync(session, id);
        string source = session.Resolve(session.Manifest.Chapters.Single().RelativePath);
        string destination = session.ChapterStore.GetPath(id);
        File.WriteAllText(destination + ".bak", "Unknown orphan backup — keep bytes");
        await Assert.ThrowsAnyAsync<IOException>(() => _repository.RestoreDeletedChapterAsync(session, id));
        Assert.True(File.Exists(source)); Assert.False(File.Exists(destination));
        Assert.Equal("Unknown orphan backup — keep bytes", File.ReadAllText(destination + ".bak"));
    }

    [Fact]
    public async Task F07_AddMustNotWriteOverUnregisteredBackupPath()
    {
        var session = await _repository.CreateAsync(_root, "Project tổng hợp");
        var chapter = TranslationService.CreateChapter("Chương mới", "你好。");
        string path = session.ChapterStore.GetPath(chapter.Id);
        File.WriteAllText(path + ".bak", "Unknown orphan backup — keep bytes");
        await Assert.ThrowsAnyAsync<IOException>(() => _repository.AddChapterAsync(session, chapter, 1));
        Assert.False(File.Exists(path)); Assert.Empty(session.Manifest.Chapters);
        Assert.Equal("Unknown orphan backup — keep bytes", File.ReadAllText(path + ".bak"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task F07_CancelledDeleteOrRestoreKeepsExactBeforeState(bool restore)
    {
        var (session, id) = await ChapterWithBackupAsync();
        if (restore) await _repository.SoftDeleteChapterAsync(session, id);
        string before = JsonSerializer.Serialize(session.Manifest, DocumentJson.Options);
        string source = session.Resolve(session.Manifest.Chapters.Single().RelativePath);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore
            ? _repository.RestoreDeletedChapterAsync(session, id, cancellation.Token)
            : _repository.SoftDeleteChapterAsync(session, id, cancellation.Token));
        Assert.True(File.Exists(source)); Assert.True(File.Exists(source + ".bak"));
        Assert.Equal(before, JsonSerializer.Serialize(session.Manifest, DocumentJson.Options));
    }

    public static IEnumerable<object[]> Faults => from operation in new[] { "add", "delete", "restore" }
        from point in new[] { "AfterJournal", "AfterFile:0", "BeforeCommit" }
        from error in new[] { "io", "permissions", "cancel" }
        select new object[] { operation, point, error };

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task F07_FaultAtStartMiddleOrEndRollsBackDiskAndMemory(string operation, string point, string error)
    {
        var (session, id) = await ChapterWithBackupAsync();
        if (operation == "restore") await _repository.SoftDeleteChapterAsync(session, id);
        var before = Capture(session);
        string manifest = JsonSerializer.Serialize(session.Manifest, DocumentJson.Options);
        _repository.FaultPoint = value =>
        {
            if (value == point) throw error switch { "permissions" => new UnauthorizedAccessException("Injected permissions"),
                "cancel" => new OperationCanceledException("Injected cancellation"), _ => new IOException("Injected IO") };
        };
        await Assert.ThrowsAnyAsync<Exception>(() => ActAsync(operation, session, id));
        _repository.FaultPoint = null;
        Assert.Equal(manifest, JsonSerializer.Serialize(session.Manifest, DocumentJson.Options));
        AssertExact(before, Capture(session));
        var reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.Equal(manifest, JsonSerializer.Serialize(reopened.Manifest, DocumentJson.Options));
        Assert.False(reopened.RecoveryReport.RequiresDecision);
    }

    [Theory]
    [InlineData("add", "AfterFile:0")]
    [InlineData("delete", "AfterFile:0")]
    [InlineData("restore", "AfterFile:0")]
    [InlineData("add", "BeforeCommit")]
    [InlineData("delete", "BeforeCommit")]
    [InlineData("restore", "BeforeCommit")]
    public async Task F07_ReopenPendingJournalOffersExplicitIdempotentRollback(string operation, string point)
    {
        var (session, id) = await ChapterWithBackupAsync();
        if (operation == "restore") await _repository.SoftDeleteChapterAsync(session, id);
        var before = Capture(session);
        _repository.FaultPoint = value => { if (value == point) throw new ProjectOperationInterruptedException(point); };
        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => ActAsync(operation, session, id));
        var repository = new ProjectRepository();
        var reopened = (await repository.OpenAsync(session.ManifestPath)).Session;
        var preview = reopened.RecoveryReport.Transactions.Single(x => x.State == ProjectTransactionState.Pending);
        Assert.True(preview.CanRollBack); Assert.True(reopened.RecoveryReport.RequiresDecision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.SaveManifestAsync(reopened));
        await repository.RecoverTransactionAsync(reopened, preview.Id, ProjectRecoveryDecision.RollBack);
        AssertExact(before, Capture(reopened));
        Assert.False((await repository.OpenAsync(session.ManifestPath)).Session.RecoveryReport.RequiresDecision);
    }

    [Fact]
    public async Task F07_InterruptedCommittedFilesCanBeConfirmedWithoutRewritingChapter()
    {
        var (session, id) = await ChapterWithBackupAsync();
        _repository.FaultPoint = point => { if (point == "BeforeCommit") throw new ProjectOperationInterruptedException(point); };
        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => _repository.SoftDeleteChapterAsync(session, id));
        var reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        var preview = reopened.RecoveryReport.Transactions.Single(x => x.State == ProjectTransactionState.Pending);
        Assert.True(preview.CanConfirmCommitted);
        var after = Capture(reopened);
        await new ProjectRepository().RecoverTransactionAsync(reopened, preview.Id, ProjectRecoveryDecision.ConfirmCommitted);
        AssertExact(after, Capture(reopened));
        Assert.NotNull(reopened.Manifest.Chapters.Single().DeletedAt);
    }

    [Fact]
    public async Task F07_OrphanPreviewNeverDeletesOrChangesUnknownPrimaryOrBackup()
    {
        var session = await _repository.CreateAsync(_root, "Project tổng hợp");
        var chapter = TranslationService.CreateChapter("Orphan cần đối chiếu", "你好。");
        chapter.ProjectId = session.Manifest.ProjectId;
        await session.ChapterStore.SaveAsync(chapter); await session.ChapterStore.SaveAsync(chapter);
        var before = Capture(session);
        var opened = await _repository.OpenAsync(session.ManifestPath);
        Assert.Equal(2, opened.Session.RecoveryReport.Orphans.Count);
        Assert.All(opened.Session.RecoveryReport.Orphans, x => Assert.Equal(chapter.Id, x.ChapterId));
        AssertExact(before, Capture(session));
    }

    private Task ActAsync(string operation, ProjectSession session, Guid id) => operation switch
    {
        "add" => _repository.AddChapterAsync(session, TranslationService.CreateChapter("Thêm tổng hợp", "你好。"), 2),
        "delete" => _repository.SoftDeleteChapterAsync(session, id),
        _ => _repository.RestoreDeletedChapterAsync(session, id)
    };
    private static Dictionary<string, byte[]> Capture(ProjectSession session) => Directory.GetFiles(session.RootDirectory, "*", SearchOption.AllDirectories)
        .Where(x => !x.Contains(Path.Combine("History", "Transactions"), StringComparison.OrdinalIgnoreCase))
        .ToDictionary(x => Path.GetRelativePath(session.RootDirectory, x), File.ReadAllBytes);
    private static void AssertExact(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (string key in before.Keys) Assert.Equal(before[key], after[key]);
    }

    private async Task<(ProjectSession Session, Guid Id)> ChapterWithBackupAsync()
    {
        var session = await _repository.CreateAsync(_root, "Project tổng hợp");
        var chapter = TranslationService.CreateChapter("Chương thử", "你好。");
        await _repository.AddChapterAsync(session, chapter, 1);
        var saved = (await _repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        saved.Segments[0].UserDraft = "Nháp giữ nguyên."; saved.Segments[0].DraftRevision++;
        await session.ChapterStore.SaveAsync(saved);
        return (session, chapter.Id);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
