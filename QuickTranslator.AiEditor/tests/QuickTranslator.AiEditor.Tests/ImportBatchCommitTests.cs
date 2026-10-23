using System.Text;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ImportBatchCommitTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-import-batch-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(50)]
    [InlineData(200)]
    [InlineData(500)]
    public async Task Manifest_is_replaced_once_per_chunk_and_progress_reports_every_chapter(int chunkSize)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Count");
        ImportPreviewPlan plan = Plan(500);
        // Initialize resources outside the I/O capture: import then only writes stages, receipts and manifests.
        await new ProjectResourceStore(session).LoadAsync();
        int manifestWrites = 0;
        var progress = new List<BulkImportProgress>();
        repository.FaultPoint = point => { if (point == "AfterImportManifestWrite") manifestWrites++; };
        using var metrics = StorageIoMetricsCapture.Begin();

        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults(),
            new BulkImportOptions(chunkSize), new InlineProgress(progress.Add));

        Assert.True(report.VerifiedComplete, report.Error);
        int chunks = (500 + chunkSize - 1) / chunkSize;
        Assert.Equal(chunks, manifestWrites);
        // Each chapter contributes two atomic writes (stage + receipt); all remaining replacements are manifests.
        Assert.Equal(chunks, metrics.Snapshot().FileReplacements - 2 * 500);
        Assert.Equal(500 + chunks, progress.Count);
        Assert.Equal(Enumerable.Range(0, chunks).SelectMany(index => Enumerable.Repeat(index * chunkSize,
                Math.Min(chunkSize, 500 - index * chunkSize))),
            progress.Where(value => value.Message.Contains("ghi tạm")).Select(value => value.ImportedChapters));
        Assert.Equal(500, progress[^1].ImportedChapters);
    }

    [Fact]
    public async Task Byte_bound_also_limits_manifest_batches()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Bytes");
        ImportPreviewPlan plan = Plan(5, new string('文', 220));
        Assert.All(plan.ImportableChapters, item => Assert.InRange(Encoding.UTF8.GetByteCount(plan.Slice(item.ContentSpan)), 513, 1024));
        int writes = 0;
        repository.FaultPoint = point => { if (point == "AfterImportManifestWrite") writes++; };

        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults(),
            new BulkImportOptions(200, 1024));

        Assert.True(report.VerifiedComplete, report.Error);
        Assert.Equal(5, writes);
        Assert.Equal(5, report.CompletedChunks);
    }

    [Theory]
    [InlineData(false, "StageFault")]
    [InlineData(true, "StageFault")]
    [InlineData(false, "StageCancel")]
    [InlineData(true, "StageCancel")]
    [InlineData(false, "LastStageCancel")]
    [InlineData(true, "LastStageCancel")]
    [InlineData(false, "ManifestFault")]
    [InlineData(true, "ManifestFault")]
    [InlineData(false, "ManifestCancel")]
    [InlineData(true, "ManifestCancel")]
    public async Task Interrupted_chunk_registers_nothing_preserves_history_and_resumes_exact_bytes(bool replace, string fault)
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Resume");
        ImportPreviewPlan plan = Plan(7);
        ImportPreviewItem[] items = plan.ImportableChapters.ToArray();
        var history = new Dictionary<Guid, (string Path, byte[] Bytes)>();
        if (replace)
        {
            foreach (ImportPreviewItem item in items)
            {
                ChapterDocument old = TranslationService.CreateChapter("Old", "旧文。");
                old.Id = item.ChapterId;
                old.Segments = new TextSegmenter().Segment(old.OriginalText, old.Id, new()).ToList();
                old.SelectedSegmentId = old.Segments[0].Id;
                old.Segments[0].UserDraft = "User draft";
                old.Segments[0].DraftRevision = 1;
                old.Segments[0].Approvals.Add(new() { ApprovedAt = DateTimeOffset.UtcNow, DraftRevision = 1, Text = "User draft" });
                old.Segments[0].Status = SegmentStatus.Approved;
                await repository.AddChapterAsync(session, old, item.Number!.Value);
            }
            await repository.SoftDeleteChaptersAsync(session, items.Select(item => item.ChapterId).ToArray());
            foreach (ProjectChapterEntry entry in session.Manifest.Chapters)
            {
                string path = session.Resolve(entry.RelativePath);
                history.Add(entry.ChapterId, (path, File.ReadAllBytes(path)));
            }
        }
        using var cancellation = new CancellationTokenSource();
        int staged = 0, manifestAttempts = 0, commits = 0, historyChecks = 0;
        byte[]? committedManifest = null;
        repository.FaultPoint = point =>
        {
            if (point == "AfterImportChapterWrite")
            {
                staged++;
                if (fault == "StageFault" && staged == 5) throw new IOException("Staging fault");
            }
            if (point == "BeforeImportManifestWrite")
            {
                manifestAttempts++;
                if (manifestAttempts == 2 && fault.StartsWith("Manifest", StringComparison.Ordinal))
                    SimpleFileStorage.FaultPoint.Value = _ =>
                    {
                        if (fault == "ManifestFault") throw new IOException("Manifest flush fault");
                        cancellation.Cancel();
                    };
            }
            if (point == "AfterImportManifestWrite")
            {
                commits++;
                committedManifest = File.ReadAllBytes(session.ManifestPath);
                // All History files for this chunk must still exist after the durable replacement,
                // before cleanup runs. This also proves none were removed while staging.
                foreach (ImportPreviewItem item in items.Skip((commits - 1) * 3).Take(3))
                    if (history.TryGetValue(item.ChapterId, out var old))
                    {
                        Assert.Equal(old.Bytes, File.ReadAllBytes(old.Path));
                        historyChecks++;
                    }
            }
        };
        var progress = new InlineProgress(value =>
        {
            if (value.Message.Contains("ghi tạm") &&
                (fault == "StageCancel" && staged == 5 || fault == "LastStageCancel" && staged == 6))
                cancellation.Cancel();
        });
        var options = new BulkImportOptions(3, ReplaceSoftDeletedHistory: replace);
        BulkImportReport failed;
        try
        {
            failed = await new SafeBulkImportService(repository).ImportAsync(session, plan, Defaults(), options, progress, cancellation.Token);
        }
        finally { SimpleFileStorage.FaultPoint.Value = null; }

        Assert.False(failed.Completed);
        Assert.True(failed.CanResume);
        Assert.Equal(1, commits);
        Assert.Equal(1, failed.CompletedChunks);
        Assert.Equal(3, failed.ImportedCount);
        Assert.Equal(3, failed.Chapters.Count(item => item.State == BulkImportChapterState.Failed));
        Assert.Equal(BulkImportChapterState.NotStarted, failed.Chapters[^1].State);
        Assert.Equal(committedManifest, File.ReadAllBytes(session.ManifestPath));
        Assert.Equal(replace ? 3 : 0, historyChecks);
        var durableBytes = Directory.GetFiles(session.Resolve("Chapters"), "*.qtaie.json")
            .ToDictionary(path => path, File.ReadAllBytes);
        Assert.Equal(staged, durableBytes.Count);
        Assert.Equal(staged - 3, Directory.GetFiles(session.Resolve("Chapters"), "*.import-sha256").Length);
        foreach (ImportPreviewItem item in items.Skip(3))
            if (history.TryGetValue(item.ChapterId, out var old)) Assert.Equal(old.Bytes, File.ReadAllBytes(old.Path));

        repository.FaultPoint = null;
        ProjectSession reopened = (await repository.OpenAsync(session.ManifestPath)).Session;
        Assert.Equal(3, reopened.Manifest.ChapterCount);
        Assert.Equal(replace ? 4 : 0, reopened.Manifest.DeletedChapterCount);
        Assert.All(items.Skip(3), item => Assert.DoesNotContain(reopened.Manifest.Chapters,
            entry => entry.ChapterId == item.ChapterId && entry.DeletedAt is null));
        BulkImportReport retry = await new SafeBulkImportService(repository).ImportAsync(reopened, plan, Defaults(), options);

        Assert.True(retry.VerifiedComplete, retry.Error);
        Assert.Equal(3, retry.AlreadyImportedCount);
        Assert.Equal(4, retry.ImportedCount);
        Assert.Equal(7, reopened.Manifest.ChapterCount);
        Assert.Equal(0, reopened.Manifest.DeletedChapterCount);
        foreach (var (path, bytes) in durableBytes) Assert.Equal(bytes, File.ReadAllBytes(path));
        foreach (ImportPreviewItem item in items)
            Assert.Equal(plan.Slice(item.ContentSpan), (await repository.LoadChapterAsync(reopened, item.ChapterId)).Chapter.OriginalText);
        foreach (var old in history.Values) Assert.False(File.Exists(old.Path));
        Assert.Empty(Directory.GetFiles(reopened.Resolve("Chapters"), "*.import-*"));
    }

    private static ImportPreviewPlan Plan(int count, string content = "原文。") => ImportPreviewEditor.Create(
        new ChapterDetector().Detect(string.Concat(Enumerable.Range(1, count)
            .Select(number => $"第{number}章 标题{number}\n{content}\n")), "batch.txt")).Current;

    private static BulkImportChapterDefaults Defaults() => new(new(), new(), PromptPreset.Default);
    private sealed class InlineProgress(Action<BulkImportProgress> action) : IProgress<BulkImportProgress>
    {
        public void Report(BulkImportProgress value) => action(value);
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
