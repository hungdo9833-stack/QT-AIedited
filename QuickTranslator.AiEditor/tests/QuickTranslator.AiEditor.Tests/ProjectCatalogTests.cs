using System.Diagnostics;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-m3-catalog-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    [InlineData(10000)]
    public async Task Builds_large_catalog_from_manifest_without_loading_chapter_bodies(int count)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "large-" + count), "Synthetic " + count);
        AddManifestEntries(session, count);
        await repository.SaveManifestAsync(session);
        repository.FaultPoint = point =>
        {
            if (point == "BeforeChapterRead") throw new InvalidOperationException("Catalog attempted to load a chapter body.");
        };
        using var catalog = new ProjectCatalog(repository);

        var watch = Stopwatch.StartNew();
        ProjectCatalogSnapshot snapshot = catalog.GetSnapshot(session);
        watch.Stop();

        Assert.Equal(count, snapshot.Chapters.Length);
        Assert.Equal(count, snapshot.ActiveCount);
        Assert.Equal(0, snapshot.DeletedCount);
        Assert.All(snapshot.Chapters, chapter => Assert.True(chapter.DisplayOrder > 0));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Catalog build took {watch.Elapsed}.");
    }

    [Fact]
    public async Task Detects_manifest_revision_changes_and_rebuilds_on_next_read()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "revision"), "Synthetic revision");
        AddManifestEntries(session, 2);
        await repository.SaveManifestAsync(session);
        using var catalog = new ProjectCatalog(repository);
        ProjectCatalogSnapshot before = catalog.GetSnapshot(session);

        session.Manifest.Chapters[0].VietnameseTitle = "Tên đã đổi ngoài cache";
        session.Manifest.UpdatedAt = session.Manifest.UpdatedAt.AddTicks(1);
        ProjectCatalogSnapshot after = catalog.GetSnapshot(session);

        Assert.NotEqual(before.Revision, after.Revision);
        Assert.Equal("Tên đã đổi ngoài cache", after.Chapters.Single(x => x.Number == 1).VietnameseTitle);
    }

    [Fact]
    public async Task Successful_commits_invalidate_catalog_but_rolled_back_transactions_do_not_publish_changes()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "commit-events"), "Synthetic commits");
        using var catalog = new ProjectCatalog(repository);
        _ = catalog.GetSnapshot(session);
        var changes = new List<ProjectCatalogChangedEventArgs>();
        catalog.Changed += (_, change) => changes.Add(change);
        ChapterDocument chapter = TranslationService.CreateChapter("Một", "原文。");
        repository.FaultPoint = point =>
        {
            if (point == "AfterFile:0") throw new IOException("Injected write failure.");
        };

        await Assert.ThrowsAsync<IOException>(() => repository.AddChapterAsync(session, chapter, 1));
        Assert.Empty(changes);
        Assert.Empty(catalog.GetSnapshot(session).Chapters);

        repository.FaultPoint = null;
        await repository.AddChapterAsync(session, chapter, 1);
        ProjectCatalogChangedEventArgs committed = Assert.Single(changes);
        Assert.Equal("AddChapter", committed.Operation);
        Assert.Equal(chapter.Id, Assert.Single(catalog.GetSnapshot(session).Chapters).ChapterId);
    }

    [Fact]
    public async Task Rename_delete_restore_translation_and_approval_update_catalog_from_committed_manifest()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "lifecycle"), "Synthetic lifecycle");
        ChapterDocument chapter = TranslationService.CreateChapter("Tên cũ", "原文。");
        await repository.AddChapterAsync(session, chapter, 1, "旧名");
        chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        using var catalog = new ProjectCatalog(repository);

        await repository.RenameChapterAsync(session, chapter.Id, "Tên mới", "新名");
        Assert.Equal("Tên mới", Assert.Single(catalog.GetSnapshot(session).Chapters).VietnameseTitle);

        var service = new TranslationService(chapter, new FakeClient(), new RegisteredChapterStore(session, repository));
        Guid segmentId = chapter.Segments[0].Id;
        service.UpdateDraft(segmentId, "Bản dịch tổng hợp");
        await service.SaveAsync();
        Assert.Equal(ProjectChapterState.AwaitingReview, Assert.Single(catalog.GetSnapshot(session).Chapters).TranslationState);
        await service.ApproveAsync(segmentId);
        ProjectCatalogChapter approved = Assert.Single(catalog.GetSnapshot(session).Chapters);
        Assert.Equal(ProjectChapterState.Approved, approved.TranslationState);
        Assert.Equal(ProjectCatalogApprovalState.Approved, approved.ApprovalState);

        await repository.SoftDeleteChapterAsync(session, chapter.Id);
        Assert.Empty(catalog.Query(session, new(Deletion: ProjectCatalogDeletionFilter.Active)));
        Assert.True(Assert.Single(catalog.Query(session, new(Deletion: ProjectCatalogDeletionFilter.Deleted))).IsDeleted);
        await repository.RestoreDeletedChapterAsync(session, chapter.Id);
        Assert.False(Assert.Single(catalog.Query(session, new())).IsDeleted);
    }

    [Fact]
    public async Task Search_filter_and_sort_use_manifest_metadata_only()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "query"), "Synthetic query");
        AddManifestEntries(session, 1000);
        session.Manifest.Chapters[949].VietnameseTitle = "Đích cần tìm";
        session.Manifest.Chapters[949].ChineseTitle = "目标";
        session.Manifest.Chapters[949].State = ProjectChapterState.Approved;
        session.Manifest.Chapters[949].CompletedSegmentCount = session.Manifest.Chapters[949].SegmentCount;
        await repository.SaveManifestAsync(session);
        repository.FaultPoint = point =>
        {
            if (point == "BeforeChapterRead") throw new InvalidOperationException("Body read is forbidden.");
        };
        using var catalog = new ProjectCatalog(repository);

        ProjectCatalogChapter result = Assert.Single(catalog.Query(session, new(
            Search: "目标", State: ProjectChapterState.Approved, Approval: ProjectCatalogApprovalFilter.Approved,
            Sort: ProjectCatalogSort.Title)));

        Assert.Equal(950, result.Number);
        Assert.Equal("Đích cần tìm", result.VietnameseTitle);
    }

    [Fact]
    public async Task Recovery_commit_notification_rebuilds_catalog_after_crash_rollback()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(_root, "recovery"), "Synthetic recovery");
        ChapterDocument chapter = TranslationService.CreateChapter("Gián đoạn", "原文。");
        repository.FaultPoint = point =>
        {
            if (point == "BeforeCommit") throw new ProjectOperationInterruptedException(point);
        };
        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => repository.AddChapterAsync(session, chapter, 1));

        var recoveryRepository = new ProjectRepository();
        ProjectSession reopened = (await recoveryRepository.OpenAsync(session.ManifestPath)).Session;
        using var catalog = new ProjectCatalog(recoveryRepository);
        Assert.Single(catalog.GetSnapshot(reopened).Chapters);
        ProjectTransactionPreview pending = Assert.Single(reopened.RecoveryReport.Transactions,
            transaction => transaction.State == ProjectTransactionState.Pending);

        await recoveryRepository.RecoverTransactionAsync(reopened, pending.Id, ProjectRecoveryDecision.RollBack);

        Assert.Empty(catalog.GetSnapshot(reopened).Chapters);
        Assert.False(reopened.RecoveryReport.RequiresDecision);
    }

    private static void AddManifestEntries(ProjectSession session, int count)
    {
        DateTimeOffset updated = DateTimeOffset.UtcNow;
        for (int index = 0; index < count; index++)
        {
            int number = index + 1;
            Guid id = Guid.NewGuid();
            session.Manifest.Chapters.Add(new()
            {
                ChapterId = id,
                Number = number,
                VietnameseTitle = "Chương " + number,
                ChineseTitle = "第" + number + "章",
                RelativePath = $"Chapters/{id:D}.qtaie.json",
                State = index % 5 == 0 ? ProjectChapterState.AwaitingReview : ProjectChapterState.Untranslated,
                SegmentCount = 10,
                CompletedSegmentCount = index % 5 == 0 ? 5 : 0,
                UpdatedAt = updated.AddSeconds(index)
            });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
