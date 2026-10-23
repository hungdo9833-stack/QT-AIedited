using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectCommitFailureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-commit-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();

    [Theory]
    [InlineData(false, "BeforeChapterMove")]
    [InlineData(true, "BeforeChapterMove")]
    [InlineData(false, "BeforeMoveManifestWrite")]
    [InlineData(true, "BeforeMoveManifestWrite")]
    [InlineData(false, "Cancel")]
    [InlineData(true, "Cancel")]
    [InlineData(false, "ManifestFlush")]
    [InlineData(true, "ManifestFlush")]
    public async Task MoveFailure_RestoresEveryOriginalByteAndManifest(bool restore, string fault)
    {
        ProjectSession session = await CreatePairAsync(restore);
        var originals = Snapshot(session);
        byte[] manifest = File.ReadAllBytes(session.ManifestPath);
        using var cancellation = new CancellationTokenSource();
        int moves = 0;
        _repository.FaultPoint = point =>
        {
            if (fault == "Cancel" && point == "AfterChapterMove") cancellation.Cancel();
            if (point == fault && (point != "BeforeChapterMove" || ++moves == 2)) throw new IOException("Injected");
        };

        if (fault == "ManifestFlush")
            SimpleFileStorage.FaultPoint.Value = point => { if (point == "AfterTemporaryFlush") throw new IOException("Manifest flush"); };
        try { await Assert.ThrowsAnyAsync<Exception>(() => MoveAsync(session, restore, cancellation.Token)); }
        finally { SimpleFileStorage.FaultPoint.Value = null; }
        _repository.FaultPoint = null;
        Assert.Equal(manifest, File.ReadAllBytes(session.ManifestPath));
        AssertOriginals(session, originals);
        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        AssertOriginals(reopened, originals);
    }

    [Fact]
    public async Task RestorePreflight_RejectsSecondDestinationBeforeMovingFirst()
    {
        ProjectSession session = await CreatePairAsync(true);
        var originals = Snapshot(session);
        string collision = session.ChapterStore.GetPath(session.Manifest.Chapters[1].ChapterId);
        File.WriteAllText(collision, "User file");
        await Assert.ThrowsAsync<IOException>(() => MoveAsync(session, true));
        AssertOriginals(session, originals);
        Assert.Equal("User file", File.ReadAllText(collision));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenAfterInterruptedMove_RecoversManifestLocationByteExactly(bool restore)
    {
        ProjectSession session = await CreatePairAsync(restore);
        var originals = Snapshot(session);
        // Model process termination after a rename, before any catch/finally or manifest commit.
        ProjectChapterEntry entry = session.Manifest.Chapters[0];
        string other = restore ? session.ChapterStore.GetPath(entry.ChapterId)
            : session.Resolve($"Deleted/20260101-000000-{Guid.NewGuid():N}-{entry.ChapterId:D}.qtaie.json");
        File.Move(session.Resolve(entry.RelativePath), other);

        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        AssertOriginals(reopened, originals);
        await MoveAsync(reopened, restore);
        foreach (ProjectChapterEntry moved in reopened.Manifest.Chapters)
            Assert.Equal(originals[moved.ChapterId].Bytes, File.ReadAllBytes(reopened.Resolve(moved.RelativePath)));
    }

    [Theory]
    [InlineData(false, "AfterImportChapterWrite")]
    [InlineData(true, "BeforeImportChapterWrite")]
    [InlineData(true, "AfterImportChapterWrite")]
    [InlineData(false, "ManifestFlush")]
    [InlineData(true, "ManifestFlush")]
    public async Task ImportFailure_PreservesHistoryAndCanResumeAfterReopen(bool replace, string fault)
    {
        ProjectSession session = await _repository.CreateAsync(_root, "Import");
        ImportPreviewPlan plan = Plan();
        ImportPreviewItem item = Assert.Single(plan.ImportableChapters);
        if (replace)
        {
            ChapterDocument old = ProtectedChapter();
            old.Id = item.ChapterId;
            old.Segments = new TextSegmenter().Segment(old.OriginalText, old.Id, new()).ToList();
            old.SelectedSegmentId = old.Segments[0].Id;
            Protect(old);
            await _repository.AddChapterAsync(session, old, 1);
            await _repository.SoftDeleteChapterAsync(session, old.Id);
        }
        var originals = Snapshot(session);
        byte[] manifest = File.ReadAllBytes(session.ManifestPath);
        _repository.FaultPoint = point =>
        {
            if (point == fault) throw new IOException("Injected");
            if (fault == "ManifestFlush" && point == "AfterImportChapterWrite")
                SimpleFileStorage.FaultPoint.Value = _ => throw new IOException("Manifest flush");
        };
        var options = new BulkImportOptions(ReplaceSoftDeletedHistory: replace);
        BulkImportReport failed;
        try { failed = await new SafeBulkImportService(_repository).ImportAsync(session, plan, Defaults(), options); }
        finally { SimpleFileStorage.FaultPoint.Value = null; }
        Assert.False(failed.Completed);
        Assert.True(failed.CanResume);
        Assert.Equal(manifest, File.ReadAllBytes(session.ManifestPath));
        AssertOriginals(session, originals);

        _repository.FaultPoint = null;
        ProjectSession reopened = (await _repository.OpenAsync(session.ManifestPath)).Session;
        AssertOriginals(reopened, originals);
        BulkImportReport retry = await new SafeBulkImportService(_repository).ImportAsync(reopened, plan, Defaults(), options);
        Assert.True(retry.VerifiedComplete, retry.Error);
        Assert.Single(reopened.Manifest.Chapters);
        Assert.Equal(plan.Slice(item.ContentSpan), (await _repository.LoadChapterAsync(reopened, item.ChapterId)).Chapter.OriginalText);
        foreach (var original in originals.Values) Assert.False(File.Exists(session.Resolve(original.Path)));
        Assert.Empty(Directory.GetFiles(session.Resolve("Chapters"), "*.import-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportRetry_RejectsUnownedOrEditedOrphanWithoutChangingBytes(bool failedAttempt)
    {
        ProjectSession session = await _repository.CreateAsync(_root, "Unowned");
        ImportPreviewPlan plan = Plan();
        Guid id = Assert.Single(plan.ImportableChapters).ChapterId;
        string path = session.ChapterStore.GetPath(id);
        if (failedAttempt)
        {
            _repository.FaultPoint = point => { if (point == "AfterImportChapterWrite") throw new IOException("Injected"); };
            Assert.False((await new SafeBulkImportService(_repository).ImportAsync(session, plan, Defaults())).Completed);
            _repository.FaultPoint = null;
            ChapterDocument edited = await ChapterFileStore.ReadVerifiedFileAsync(path);
            Protect(edited);
            await session.ChapterStore.SaveAsync(edited);
        }
        else File.WriteAllText(path, "User-created file");
        byte[] before = File.ReadAllBytes(path);
        ProjectSession reopened = (await _repository.OpenAsync(session.ManifestPath)).Session;
        await Assert.ThrowsAsync<BulkImportValidationException>(() =>
            new SafeBulkImportService(_repository).ImportAsync(reopened, plan, Defaults()));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(reopened.Manifest.Chapters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SecondMoveLocked_RollsBackFirstWithoutChangingBytes(bool restore)
    {
        ProjectSession session = await CreatePairAsync(restore);
        var originals = Snapshot(session);
        string lockedPath = session.Resolve(session.Manifest.Chapters[1].RelativePath);
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => MoveAsync(session, restore));
        AssertOriginals(session, originals);
        AssertOriginals((await _repository.OpenAsync(session.ManifestPath)).Session, originals);
    }

    [Fact]
    public async Task InterruptedMove_AmbiguousRecoveryPreservesEveryCandidate()
    {
        ProjectSession session = await CreatePairAsync(false);
        ProjectChapterEntry entry = session.Manifest.Chapters[0];
        string first = session.Resolve($"Deleted/first-{entry.ChapterId:D}.qtaie.json");
        string second = session.Resolve($"Deleted/second-{entry.ChapterId:D}.qtaie.json");
        byte[] before = File.ReadAllBytes(session.Resolve(entry.RelativePath));
        File.Move(session.Resolve(entry.RelativePath), first);
        File.Copy(first, second);
        // Ambiguity must not block opening the project; both candidates stay untouched for manual resolution.
        ProjectSession reopened = (await _repository.OpenAsync(session.ManifestPath)).Session;
        Assert.Single(reopened.Manifest.Chapters, x => x.ChapterId == entry.ChapterId);
        Assert.Equal(before, File.ReadAllBytes(first));
        Assert.Equal(before, File.ReadAllBytes(second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Insert_RegisteredReadsUseManifestNumberAndRemainEditable(bool adapter)
    {
        ProjectSession session = await CreatePairAsync(false);
        Guid id = session.Manifest.Chapters[1].ChapterId;
        byte[] before = File.ReadAllBytes(session.ChapterStore.GetPath(id));
        await _repository.InsertChapterAsync(session, TranslationService.CreateChapter("Inserted", "新。"), 2);
        ProjectSession reopened = (await _repository.OpenAsync(session.ManifestPath)).Session;
        var store = new RegisteredChapterStore(reopened, _repository);
        ChapterDocument chapter = (adapter ? await store.LoadAsync(store.GetPath(id))
            : await _repository.LoadChapterAsync(reopened, id)).Chapter;
        Assert.Equal(3, chapter.ChapterNumber);
        Assert.Equal(before, File.ReadAllBytes(store.GetPath(id)));
        Assert.Equal("Approved draft", chapter.Segments[0].UserDraft);
        Assert.Single(chapter.Segments[0].Approvals);
        chapter.Title = "Edited";
        await store.SaveAsync(chapter);
        Assert.Equal(3, (await store.LoadAsync(store.GetPath(id))).Chapter.ChapterNumber);
    }

    private async Task<ProjectSession> CreatePairAsync(bool deleted)
    {
        ProjectSession session = await _repository.CreateAsync(_root, "Moves");
        await _repository.AddChapterAsync(session, ProtectedChapter(), 1);
        await _repository.AddChapterAsync(session, ProtectedChapter(), 2);
        if (deleted) await MoveAsync(session, false);
        return session;
    }

    private Task MoveAsync(ProjectSession session, bool restore, CancellationToken token = default)
    {
        Guid[] ids = session.Manifest.Chapters.Select(x => x.ChapterId).ToArray();
        return restore ? _repository.RestoreDeletedChaptersAsync(session, ids, token)
            : _repository.SoftDeleteChaptersAsync(session, ids, token);
    }

    private static Dictionary<Guid, (string Path, byte[] Bytes)> Snapshot(ProjectSession session) =>
        session.Manifest.Chapters.ToDictionary(x => x.ChapterId,
            x => (x.RelativePath, File.ReadAllBytes(session.Resolve(x.RelativePath))));

    private static void AssertOriginals(ProjectSession session, Dictionary<Guid, (string Path, byte[] Bytes)> originals)
    {
        foreach (var (id, original) in originals)
        {
            Assert.Equal(original.Path, session.Manifest.Chapters.Single(x => x.ChapterId == id).RelativePath);
            Assert.True(File.Exists(session.Resolve(original.Path)), "Missing original: " + original.Path);
            Assert.Equal(original.Bytes, File.ReadAllBytes(session.Resolve(original.Path)));
        }
    }

    private static ChapterDocument ProtectedChapter()
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Original", "原文。");
        Protect(chapter);
        return chapter;
    }

    private static void Protect(ChapterDocument chapter)
    {
        chapter.Segments[0].UserDraft = "Approved draft";
        chapter.Segments[0].DraftRevision = 1;
        chapter.Segments[0].Approvals.Add(new() { ApprovedAt = DateTimeOffset.UtcNow, DraftRevision = 1, Text = "Approved draft" });
        chapter.Segments[0].Status = SegmentStatus.Approved;
    }

    private static ImportPreviewPlan Plan() => ImportPreviewEditor.Create(
        new ChapterDetector().Detect("第1章 标题\n原文。\n", "source.txt")).Current;
    private static BulkImportChapterDefaults Defaults() => new(new(), new(), PromptPreset.Default);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
