using System.Reflection;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class LegacyImportSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-phase3-import-" + Guid.NewGuid());

    [Fact]
    public async Task F09_VerificationReadFailureRollsBackTheChapterCreatedBeforeTracking()
    {
        var repository = new ProjectRepository();
        var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        var source = RichChapter(); var sourceStore = new ChapterFileStore(Path.Combine(_root, "Legacy"));
        await sourceStore.SaveAsync(source); string sourcePath = sourceStore.GetPath(source.Id);
        byte[] original = File.ReadAllBytes(sourcePath);
        var importer = new LegacyProjectImporter(repository);
        var candidate = (await importer.ScanAsync(sourceStore.DirectoryPath)).Single();
        repository.FaultPoint = point => { if (point == "BeforeChapterRead") throw new IOException("Injected verification read failure"); };
        await Assert.ThrowsAnyAsync<IOException>(() => importer.ImportAsync(target, [new(candidate, 1)]));
        Assert.Empty(target.Manifest.Chapters);
        Assert.Empty(target.ChapterStore.EnumerateChapterPaths());
        Assert.Equal(original, File.ReadAllBytes(sourcePath));
        Assert.Empty((await new ProjectRepository().OpenAsync(target.ManifestPath)).Session.Manifest.Chapters);
        repository.FaultPoint = null;
        Assert.Equal(1, (await importer.ImportAsync(target, [new(candidate, 1)])).ImportedCount);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("attempt")]
    [InlineData("approval")]
    [InlineData("history")]
    [InlineData("status")]
    [InlineData("source")]
    public void F09_EqualHistoryCountsDoNotProveEquivalentContents(string field)
    {
        var source = RichChapter(); var changed = DocumentJson.Clone(source);
        switch (field)
        {
            case "draft": changed.Segments[0].UserDraft = "Một nháp khác cùng số revision"; break;
            case "attempt": changed.Segments[0].Attempts[0].AiText = "Một bản AI khác"; break;
            case "approval": changed.Segments[0].Approvals[0] = changed.Segments[0].Approvals[0] with { Text = "Một bản duyệt khác" }; break;
            case "history": changed.Segments[0].DraftHistory[0] = changed.Segments[0].DraftHistory[0] with { Text = "Một lịch sử khác" }; break;
            case "status": changed.Segments[0].Status = SegmentStatus.AwaitingReview; break;
            case "source": changed.Segments[0].SourceText = "您好。"; break;
        }
        var method = typeof(LegacyProjectImporter).GetMethod("VerifyEquivalent", BindingFlags.Static | BindingFlags.NonPublic)!;
        var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, [source, changed]));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    public static IEnumerable<object[]> Faults => from point in new[] { "AfterJournal", "AfterFile:1", "BeforeCommit" }
        from error in new[] { "io", "permissions", "cancel" } select new object[] { point, error };

    [Theory][MemberData(nameof(Faults))]
    public async Task F09_ImportFaultRollsBackWholeBatchAndAllowsRetry(string point, string error)
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        await repository.AddChapterAsync(target, RichChapter(), 10);
        var sourceStore = new ChapterFileStore(Path.Combine(_root, "Legacy"));
        for (int i = 0; i < 3; i++) await sourceStore.SaveAsync(RichChapter());
        var sourceBefore = Capture(sourceStore.DirectoryPath); var before = Capture(target.RootDirectory);
        var importer = new LegacyProjectImporter(repository);
        var selection = (await importer.ScanAsync(sourceStore.DirectoryPath)).Select((x, i) => new LegacyChapterSelection(x, i + 1)).ToArray();
        repository.FaultPoint = value => { if (value == point) throw error switch
            { "cancel" => new OperationCanceledException("Injected import cancellation"), "permissions" => new UnauthorizedAccessException("Injected import permissions"), _ => new IOException("Injected import IO") }; };
        var failure = await Record.ExceptionAsync(() => importer.ImportAsync(target, selection));
        Assert.NotNull(failure);
        if (failure is LegacyImportException import) Assert.True(import.Report.RollbackComplete);
        repository.FaultPoint = null;
        AssertExact(before, Capture(target.RootDirectory)); AssertExact(sourceBefore, Capture(sourceStore.DirectoryPath));
        var reopened = (await new ProjectRepository().OpenAsync(target.ManifestPath)).Session;
        Assert.False(reopened.RecoveryReport.RequiresDecision);
        Assert.Single(reopened.Manifest.Chapters);
        Assert.Equal(3, (await importer.ImportAsync(reopened, selection)).ImportedCount);
        AssertExact(sourceBefore, Capture(sourceStore.DirectoryPath));
    }

    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task F09_VerificationFailureAtAnyChapterRollsBackAllPublishedChapters(int badIndex)
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        var store = new ChapterFileStore(Path.Combine(_root, "Legacy"));
        for (int i = 0; i < 3; i++) await store.SaveAsync(RichChapter());
        var importer = new LegacyProjectImporter(repository);
        var selected = (await importer.ScanAsync(store.DirectoryPath)).Select((x, i) => new LegacyChapterSelection(x, i + 1)).ToArray();
        var before = Capture(target.RootDirectory); int reads = 0;
        repository.FaultPoint = point => { if (point == "BeforeChapterRead" && reads++ == badIndex) throw new IOException("Injected verification failure"); };
        var error = await Assert.ThrowsAsync<LegacyImportException>(() => importer.ImportAsync(target, selected));
        Assert.True(error.Report.RollbackComplete);
        Assert.All(error.Report.Chapters, x => Assert.Equal(LegacyImportChapterState.RolledBack, x.State));
        AssertExact(before, Capture(target.RootDirectory));
        Assert.False((await new ProjectRepository().OpenAsync(target.ManifestPath)).Session.RecoveryReport.RequiresDecision);
    }

    [Fact]
    public async Task F09_FullSnapshotAndSourceBytesSurviveIntentionalTargetIdentityConversion()
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        var resourceStore = new ProjectResourceStore(target); var resources = await resourceStore.LoadAsync(); resources.Context = "Context của target";
        await resourceStore.SaveAsync(resources); byte[] resourceBefore = File.ReadAllBytes(resourceStore.PathName);
        var source = RichChapter(); source.ProjectId = Guid.NewGuid(); source.ChapterNumber = 11; source.ChineseTitle = "  中文标题  ";
        var segment = source.Segments[0];
        segment.Attempts[0].Metrics = new() { InputTokens = 123, OutputTokens = 42, ClientElapsed = TimeSpan.FromSeconds(8) };
        segment.NormalizationHistory.Add(new() { BatchId = Guid.NewGuid(), BeforeText = "hắn", AfterText = "nàng",
            Changes = [new() { CharacterId = Guid.NewGuid(), StartUtf16 = 0, Length = 3, Original = "hắn", Replacement = "nàng", Reason = "Lịch sử tổng hợp" }] });
        QuickTranslator.AiEditor.Core.TextCleaning.BatchReplacementEngine.ApplyToChapter(source,
            new QuickTranslator.AiEditor.Core.TextCleaning.BatchReplacementEngine().Preview([source],
                [new() { Find = "Nháp", Replacement = "Bản nháp", Confirmed = true }], BatchReplacementScope.CurrentChapter), true,
            BatchReplacementScope.CurrentChapter, Guid.NewGuid(), DateTimeOffset.UtcNow);
        var store = new ChapterFileStore(Path.Combine(_root, "Legacy")); await store.SaveAsync(source); await store.SaveAsync(source);
        var sourceBefore = Capture(store.DirectoryPath);
        var importer = new LegacyProjectImporter(repository);
        var result = await importer.ImportAsync(target, [new((await importer.ScanAsync(store.DirectoryPath)).Single(), 7)]);
        var imported = (await repository.LoadChapterAsync(target, source.Id)).Chapter;
        var expected = DocumentJson.Clone(source); expected.ProjectId = target.Manifest.ProjectId; expected.ChapterNumber = 7; expected.CharacterProject.Id = target.Manifest.ProjectId;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected, DocumentJson.Options), System.Text.Json.JsonSerializer.Serialize(imported, DocumentJson.Options));
        AssertExact(sourceBefore, Capture(store.DirectoryPath)); Assert.Equal(resourceBefore, File.ReadAllBytes(resourceStore.PathName));
        Assert.All(result.Report!.Chapters, x => Assert.Equal(LegacyImportChapterState.Imported, x.State));
        using var zip = System.IO.Compression.ZipFile.OpenRead(result.SourceBackupPath);
        Assert.Contains(zip.Entries, x => x.Name == "legacy-source-manifest.json");
    }

    [Theory][InlineData("id")][InlineData("number")][InlineData("path")]
    public async Task F09_DuplicateSelectionsStopBeforeTargetPublication(string duplicate)
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        var store = new ChapterFileStore(Path.Combine(_root, "Legacy")); await store.SaveAsync(RichChapter()); await store.SaveAsync(RichChapter());
        var importer = new LegacyProjectImporter(repository); var candidates = (await importer.ScanAsync(store.DirectoryPath)).ToArray();
        if (duplicate == "id") candidates[1] = candidates[1] with { ChapterId = candidates[0].ChapterId };
        if (duplicate == "path") candidates[1] = candidates[1] with { Path = candidates[0].Path };
        var before = Capture(target.RootDirectory);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => importer.ImportAsync(target, [new(candidates[0], 1), new(candidates[1], duplicate == "number" ? 1 : 2)]));
        AssertExact(before, Capture(target.RootDirectory)); Assert.Empty(target.Manifest.Chapters);
    }

    [Theory][InlineData("AfterFile:0")][InlineData("BeforeCommit")]
    public async Task F09_InterruptedImportIsRecoverableFromJournalAfterReopen(string point)
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        var store = new ChapterFileStore(Path.Combine(_root, "Legacy")); await store.SaveAsync(RichChapter()); await store.SaveAsync(RichChapter());
        var importer = new LegacyProjectImporter(repository); var selected = (await importer.ScanAsync(store.DirectoryPath)).Select((x, i) => new LegacyChapterSelection(x, i + 1)).ToArray();
        var before = Capture(target.RootDirectory);
        repository.FaultPoint = value => { if (value == point) throw new ProjectOperationInterruptedException(point); };
        await Assert.ThrowsAsync<ProjectOperationInterruptedException>(() => importer.ImportAsync(target, selected));
        var fresh = new ProjectRepository(); var opened = (await fresh.OpenAsync(target.ManifestPath)).Session;
        var pending = opened.RecoveryReport.Transactions.Single(x => x.State == ProjectTransactionState.Pending);
        Assert.Equal("LegacyImport", pending.Operation); Assert.True(pending.CanRollBack);
        await fresh.RecoverTransactionAsync(opened, pending.Id, ProjectRecoveryDecision.RollBack);
        AssertExact(before, Capture(opened.RootDirectory));
        Assert.Equal(2, (await new LegacyProjectImporter(fresh).ImportAsync(opened, selected)).ImportedCount);
    }

    [Fact]
    public async Task F09_BackupOnlyLegacySourceIsListedAndImportedWithoutCreatingSourcePrimary()
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        var store = new ChapterFileStore(Path.Combine(_root, "Legacy")); var source = RichChapter(); await store.SaveAsync(source); await store.SaveAsync(source);
        File.Delete(store.GetPath(source.Id)); byte[] before = File.ReadAllBytes(store.GetPath(source.Id) + ".bak");
        var importer = new LegacyProjectImporter(repository); var candidate = (await importer.ScanAsync(store.DirectoryPath)).Single();
        Assert.Null(candidate.Error);
        Assert.Equal(1, (await importer.ImportAsync(target, [new(candidate, 1)])).ImportedCount);
        Assert.False(File.Exists(store.GetPath(source.Id))); Assert.Equal(before, File.ReadAllBytes(store.GetPath(source.Id) + ".bak"));
    }

    [Fact]
    public async Task F09_CorruptedPublishedOutputIsRetainedAndWholeImportCanRetry()
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        var sourceStore = new ChapterFileStore(Path.Combine(_root, "Legacy")); var source = RichChapter(); await sourceStore.SaveAsync(source);
        var importer = new LegacyProjectImporter(repository); var selected = new[] { new LegacyChapterSelection((await importer.ScanAsync(sourceStore.DirectoryPath)).Single(), 1) };
        var before = Capture(target.RootDirectory);
        repository.FaultPoint = point => { if (point == "BeforeCommit") File.WriteAllText(target.ChapterStore.GetPath(source.Id), "{injected corrupt verification output"); };
        var error = await Assert.ThrowsAsync<LegacyImportException>(() => importer.ImportAsync(target, selected));
        Assert.True(error.Report.RollbackComplete); AssertExact(before, Capture(target.RootDirectory));
        Assert.Contains(Directory.GetFiles(target.Resolve("History/Transactions"), "retained-changed-*.bin", SearchOption.AllDirectories),
            x => File.ReadAllText(x) == "{injected corrupt verification output");
        repository.FaultPoint = null; Assert.Equal(1, (await importer.ImportAsync(target, selected)).ImportedCount);
    }

    [Fact]
    public async Task F09_FailedRollbackReportsManualRecoveryAndRetryWorksAfterUnlock()
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        var sourceStore = new ChapterFileStore(Path.Combine(_root, "Legacy")); var source = RichChapter(); await sourceStore.SaveAsync(source);
        var importer = new LegacyProjectImporter(repository); var selected = new[] { new LegacyChapterSelection((await importer.ScanAsync(sourceStore.DirectoryPath)).Single(), 1) };
        var before = Capture(target.RootDirectory); FileStream? locked = null;
        repository.FaultPoint = point =>
        {
            if (point != "BeforeChapterRead") return;
            locked = new FileStream(target.ChapterStore.GetPath(source.Id), FileMode.Open, FileAccess.Read, FileShare.Read);
            throw new IOException("Verification interrupted while target file is locked");
        };
        LegacyImportException error;
        try { error = await Assert.ThrowsAsync<LegacyImportException>(() => importer.ImportAsync(target, selected)); }
        finally { locked?.Dispose(); repository.FaultPoint = null; }
        Assert.False(error.Report.RollbackComplete); Assert.Equal(LegacyImportChapterState.NeedsRecovery, error.Report.Chapters.Single().State);
        var fresh = new ProjectRepository(); var reopened = (await fresh.OpenAsync(target.ManifestPath)).Session;
        var pending = reopened.RecoveryReport.Transactions.Single(x => x.State == ProjectTransactionState.RecoveryRequired);
        Assert.True(pending.CanRollBack); Assert.NotNull(pending.SourceArchivePath);
        await fresh.RecoverTransactionAsync(reopened, pending.Id, ProjectRecoveryDecision.RollBack);
        AssertExact(before, Capture(reopened.RootDirectory));
        Assert.Equal(1, (await new LegacyProjectImporter(fresh).ImportAsync(reopened, selected)).ImportedCount);
    }

    [Fact]
    public async Task F09_SameBasenameFromDifferentDirectoriesHasUnambiguousVerifiedSourceArchive()
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        var candidates = new List<LegacyChapterCandidate>();
        foreach (string directory in new[] { "A", "B" })
        {
            var store = new ChapterFileStore(Path.Combine(_root, directory)); var source = RichChapter(); await store.SaveAsync(source);
            string renamed = Path.Combine(store.DirectoryPath, "same.qtaie.json"); File.Move(store.GetPath(source.Id), renamed);
            candidates.Add((await new LegacyProjectImporter(repository).ScanAsync(store.DirectoryPath)).Single());
        }
        var result = await new LegacyProjectImporter(repository).ImportAsync(target, candidates.Select((x, i) => new LegacyChapterSelection(x, i + 1)));
        using var zip = System.IO.Compression.ZipFile.OpenRead(result.SourceBackupPath);
        Assert.Equal(zip.Entries.Count, zip.Entries.Select(x => x.FullName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(2, result.ImportedCount);
    }

    [Fact]
    public async Task F09_OriginalV1FixtureRemainsByteIdenticalAndFullContentImports()
    {
        var repository = new ProjectRepository(); var target = await repository.CreateAsync(Path.Combine(_root, "Target"), "Đích tổng hợp");
        string sourceDir = Path.Combine(_root, "Legacy"); Directory.CreateDirectory(sourceDir);
        string path = Path.Combine(sourceDir, "original.qtaie.json");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-v1.fixture"), path);
        byte[] bytes = File.ReadAllBytes(path);
        var importer = new LegacyProjectImporter(repository); var selected = (await importer.ScanAsync(sourceDir)).Single();
        var source = (await new ChapterFileStore(sourceDir).LoadAsync(path)).Chapter;
        await importer.ImportAsync(target, [new(selected, 4)]);
        var expected = ProjectRepository.PrepareRegisteredChapter(target, source, 4, null);
        var imported = (await repository.LoadChapterAsync(target, source.Id)).Chapter;
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(expected, DocumentJson.Options), System.Text.Json.JsonSerializer.Serialize(imported, DocumentJson.Options));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    private static Dictionary<string, byte[]> Capture(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Where(x => !x.Contains(Path.Combine("History", "Transactions"), StringComparison.OrdinalIgnoreCase) &&
                    !x.Contains(Path.DirectorySeparatorChar + "Backups" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        .ToDictionary(x => Path.GetRelativePath(root, x), File.ReadAllBytes);
    private static void AssertExact(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (string key in before.Keys) Assert.Equal(before[key], after[key]);
    }

    internal static ChapterDocument RichChapter()
    {
        var chapter = TranslationService.CreateChapter("Nguồn legacy tổng hợp", "你好。", clock: new FixedClock());
        var segment = chapter.Segments[0];
        segment.UserDraft = "Nháp của người dùng."; segment.DraftRevision = 3; segment.Status = SegmentStatus.Approved;
        var attempt = new TranslationAttempt { Outcome = AttemptOutcome.Completed, AiText = "Bản AI lịch sử.", Prompt = "Prompt tổng hợp giữ nguyên", PromptVersion = "legacy-test" };
        segment.Attempts.Add(attempt); segment.DraftSourceAttemptId = attempt.Id;
        segment.Approvals.Add(new() { Text = segment.UserDraft, DraftRevision = 3, SourceAttemptId = attempt.Id });
        segment.DraftHistory.Add(new() { Text = "Nháp cũ khác.", Revision = 2, SourceAttemptId = attempt.Id, Reason = "Lịch sử cần bảo toàn" });
        chapter.ConfirmedContextNotes = "Ngữ cảnh snapshot cũ"; chapter.ConfirmedTerms = [new("名字", "Tên trong snapshot")];
        return chapter;
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero); }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
