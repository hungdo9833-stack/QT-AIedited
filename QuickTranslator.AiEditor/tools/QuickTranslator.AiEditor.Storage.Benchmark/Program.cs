using System.Diagnostics;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

int checkpoints = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 1_000;
string root = Path.Combine(Path.GetTempPath(), "QTStudio-Storage-Benchmark", Guid.NewGuid().ToString("N"));
try
{
    var repository = new ProjectRepository();
    ProjectSession session = await repository.CreateAsync(root, "Storage benchmark synthetic");
    string source = string.Concat(Enumerable.Repeat("天地玄黄宇宙洪荒。", 220));
    var chapter = TranslationService.CreateChapter("Chương tổng hợp", source);
    await repository.AddChapterAsync(session, chapter, 1);
    chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;

    var samples = new double[checkpoints];
    using StorageIoMetricsCapture io = StorageIoMetricsCapture.Begin();
    Stopwatch saves = Stopwatch.StartNew();
    for (int index = 0; index < checkpoints; index++)
    {
        chapter.ConfirmedContextNotes = "Synthetic checkpoint " + index;
        chapter.UpdatedAt = DateTimeOffset.UtcNow;
        long started = Stopwatch.GetTimestamp();
        await repository.UpdateChapterAsync(session, chapter);
        samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }
    saves.Stop();
    StorageIoMetricsSnapshot ioMetrics = io.Snapshot();

    Stopwatch reopen = Stopwatch.StartNew();
    ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
    reopen.Stop();
    var finalChapter = (await new ProjectRepository().LoadChapterAsync(reopened, chapter.Id)).Chapter;
    double[] sorted = samples.Order().ToArray();
    long projectBytes = Directory.EnumerateFiles(session.RootDirectory, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);
    DateTime chapterWriteTime = File.GetLastWriteTimeUtc(session.ChapterStore.GetPath(chapter.Id));
    DateTime manifestWriteTime = File.GetLastWriteTimeUtc(session.ManifestPath);
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Checkpoints = checkpoints,
        SaveMilliseconds = Math.Round(saves.Elapsed.TotalMilliseconds, 3),
        AverageSaveMilliseconds = Math.Round(saves.Elapsed.TotalMilliseconds / checkpoints, 3),
        MedianSaveMilliseconds = Math.Round(Percentile(sorted, 0.50), 3),
        P95SaveMilliseconds = Math.Round(Percentile(sorted, 0.95), 3),
        IoPerCheckpoint = new
        {
            Serializations = Ratio(ioMetrics.Serializations), Hashes = Ratio(ioMetrics.Hashes),
            FileReads = Ratio(ioMetrics.FileReads), BytesRead = Ratio(ioMetrics.BytesRead),
            FileWrites = Ratio(ioMetrics.FileWrites), BytesWritten = Ratio(ioMetrics.BytesWritten),
            TemporaryFilesCreated = Ratio(ioMetrics.TemporaryFilesCreated),
            FileReplacements = Ratio(ioMetrics.FileReplacements), FileDeletes = Ratio(ioMetrics.FileDeletes)
        },
        ProjectBytes = projectBytes,
        ChapterNewerThanManifest = chapterWriteTime > manifestWriteTime,
        ReopenMilliseconds = Math.Round(reopen.Elapsed.TotalMilliseconds, 3),
        FinalCheckpointExact = finalChapter.ConfirmedContextNotes == "Synthetic checkpoint " + (checkpoints - 1)
    }, new JsonSerializerOptions { WriteIndented = true }));

    double Ratio(long value) => Math.Round((double)value / checkpoints, 3);
}
finally
{
    string guard = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QTStudio-Storage-Benchmark")) + Path.DirectorySeparatorChar;
    string full = Path.GetFullPath(root);
    if (full.StartsWith(guard, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
        Directory.Delete(full, recursive: true);
}

static double Percentile(double[] sorted, double percentile)
{
    if (sorted.Length == 0) return 0;
    int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
    return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
}
