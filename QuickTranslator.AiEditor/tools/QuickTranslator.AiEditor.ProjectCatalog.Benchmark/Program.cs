using System.Diagnostics;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Projects;

string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QTStudio-M3-Catalog-Benchmark", Guid.NewGuid().ToString("N")));
var results = new List<CatalogBenchmarkResult>();
try
{
    foreach (int count in new[] { 1_000, 5_000, 10_000 })
        results.Add(await RunAsync(root, count));

    var report = new CatalogBenchmarkReport(
        DateTimeOffset.UtcNow,
        Environment.Version.ToString(),
        Environment.OSVersion.ToString(),
        results);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    string guardedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QTStudio-M3-Catalog-Benchmark")) + Path.DirectorySeparatorChar;
    if (root.StartsWith(guardedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}

static async Task<CatalogBenchmarkResult> RunAsync(string root, int count)
{
    string projectRoot = Path.Combine(root, count.ToString());
    var repository = new ProjectRepository();
    ProjectSession session = await repository.CreateAsync(projectRoot, $"Synthetic {count:N0}");
    DateTimeOffset now = DateTimeOffset.UtcNow;
    for (int index = 1; index <= count; index++)
    {
        session.Manifest.Chapters.Add(new ProjectChapterEntry
        {
            ChapterId = Guid.NewGuid(),
            Number = index,
            VietnameseTitle = $"Chương tổng hợp {index:N0}",
            ChineseTitle = $"第{index}章 合成目录",
            RelativePath = $"Chapters/{index:D5}.qtaie.json",
            State = (ProjectChapterState)(index % Enum.GetValues<ProjectChapterState>().Length),
            SegmentCount = 100,
            CompletedSegmentCount = index % 101,
            UpdatedAt = now.AddSeconds(-index)
        });
    }
    await repository.SaveManifestAsync(session);
    int bodyFileCount = Directory.EnumerateFiles(Path.Combine(projectRoot, "Chapters"), "*.qtaie.json").Count();

    using var catalog = new ProjectCatalog(repository);
    ForceGc();
    long beforeMemory = GC.GetTotalMemory(forceFullCollection: true);
    Stopwatch stopwatch = Stopwatch.StartNew();
    ProjectCatalogSnapshot snapshot = catalog.GetSnapshot(session);
    stopwatch.Stop();
    long catalogBytes = Math.Max(0, GC.GetTotalMemory(forceFullCollection: true) - beforeMemory);

    double searchMs = Measure(() => catalog.Query(session, new(Search: "9")));
    double filterMs = Measure(() => catalog.Query(session, new(State: ProjectChapterState.Approved)));
    double sortMs = Measure(() => catalog.Query(session, new(Sort: ProjectCatalogSort.Title)));

    var reopenRepository = new ProjectRepository();
    ProjectSession reopened = (await reopenRepository.OpenAsync(session.ManifestPath)).Session;
    using var reopenedCatalog = new ProjectCatalog(reopenRepository);
    stopwatch.Restart();
    ProjectCatalogSnapshot reopenedSnapshot = reopenedCatalog.GetSnapshot(reopened);
    stopwatch.Stop();

    if (snapshot.Chapters.Length != count || reopenedSnapshot.Chapters.Length != count || bodyFileCount != 0)
        throw new InvalidOperationException("Catalog benchmark violated metadata-only loading constraints.");

    return new CatalogBenchmarkResult(count, bodyFileCount, snapshot.Revision,
        Math.Round(stopwatch.Elapsed.TotalMilliseconds, 3),
        Math.Round(searchMs, 3), Math.Round(filterMs, 3), Math.Round(sortMs, 3), catalogBytes);
}

static double Measure(Func<IReadOnlyCollection<ProjectCatalogChapter>> operation)
{
    operation();
    const int iterations = 12;
    Stopwatch stopwatch = Stopwatch.StartNew();
    int observed = 0;
    for (int i = 0; i < iterations; i++) observed += operation().Count;
    stopwatch.Stop();
    GC.KeepAlive(observed);
    return stopwatch.Elapsed.TotalMilliseconds / iterations;
}

static void ForceGc()
{
    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();
}

internal sealed record CatalogBenchmarkReport(
    DateTimeOffset MeasuredAtUtc,
    string DotnetRuntime,
    string OperatingSystem,
    IReadOnlyList<CatalogBenchmarkResult> Results);

internal sealed record CatalogBenchmarkResult(
    int ChapterCount,
    int ChapterBodyFilesPresent,
    string ManifestRevision,
    double ReopenCatalogMilliseconds,
    double SearchMilliseconds,
    double FilterMilliseconds,
    double SortMilliseconds,
    long ApproximateCatalogBytes);
