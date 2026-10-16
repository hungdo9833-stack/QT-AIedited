using System.Diagnostics;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

int chapterCount = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 1_000;
int workerCount = args.Length > 1 && int.TryParse(args[1], out int parsedWorkers) ? parsedWorkers : 2;
if (chapterCount is < 10 or > 10_000) throw new ArgumentOutOfRangeException(nameof(chapterCount));
if (workerCount is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(workerCount));
string root = Path.Combine(Path.GetTempPath(), "QTStudio-Phase3-Benchmark", Guid.NewGuid().ToString("N"));
try
{
    var repository = new ProjectRepository();
    ProjectSession session = await repository.CreateAsync(root, "Phase 3 synthetic benchmark");
    var chapterIds = new List<Guid>(chapterCount);
    for (int number = 1; number <= chapterCount; number++)
    {
        ChapterDocument chapter = TranslationService.CreateChapter($"Chương {number}",
            $"天地玄黄宇宙洪荒第{number}章。", new SegmenterOptions
            { MinimumHanCharacters = 1, TargetHanCharacters = 100, MaximumHanCharacters = 200 });
        ProjectChapterEntry entry = await repository.AddChapterAsync(session, chapter, number, $"第{number}章");
        chapterIds.Add(entry.ChapterId);
    }

    var resources = new ProjectResources
    {
        ProjectId = session.Manifest.ProjectId,
        Characters = new CharacterProject
        {
            Id = session.Manifest.ProjectId,
            Name = "Phase 3 synthetic benchmark",
            UpdatedAt = DateTimeOffset.UtcNow
        }
    };
    ProjectTranslationEnvironment environment = new(new ModelOptions(),
        PromptPreset.Default with { UpdatedAt = DateTimeOffset.UnixEpoch }, resources);
    Task<ProjectTranslationEnvironment> Load(CancellationToken _) =>
        Task.FromResult(environment with { Resources = environment.Resources.Clone() });
    var local = new ImmediateClient("Local");
    var remote = new ImmediateClient("Remote");
    ModelOptions options = environment.ModelOptions;
    var workerList = new List<ProjectTranslationWorkerDefinition>
    {
        new(Guid.NewGuid(), OllamaBackendSnapshot.LocalDefault(options.Model), local, options with { })
    };
    if (workerCount == 2)
        workerList.Add(new(Guid.NewGuid(), new OllamaBackendSnapshot(Guid.NewGuid(), OllamaBackendKind.Remote,
                "Synthetic Remote", "http://127.0.0.1:43117/api/chat", options.Model), remote,
            options with { }, 0.45m, "synthetic-confirmed-host"));
    ProjectTranslationWorkerDefinition[] workers = workerList.ToArray();
    var store = new ProjectTranslationJobStore(session);
    var planner = new ProjectTranslationCoordinator(session, repository, workers, Load, store);
    ProjectTranslationJob saved = await store.SaveNewAsync(await planner.PlanAsync(chapterIds));
    saved.Status = ProjectTranslationJobStatus.Paused;
    saved = await store.SaveAsync(saved);
    long revisionBefore = saved.Revision;
    int switchAt = Math.Max(3, chapterCount / 10);
    long switchStarted = 0;
    long switchObserved = 0;
    var coordinator = new ProjectTranslationCoordinator(session, repository, workers, Load,
        new ProjectTranslationJobStore(session));
    await coordinator.LoadAsync();
    coordinator.ProgressChanged += (_, _) =>
    {
        if (switchStarted != 0 || coordinator.CurrentJob?.Items.Count(item =>
                item.Status == ProjectTranslationItemStatus.Completed) < switchAt) return;
        if (Interlocked.CompareExchange(ref switchStarted, Stopwatch.GetTimestamp(), 0) != 0) return;
        ProjectResources changed = environment.Resources.Clone();
        changed.Revision++;
        changed.Terms.Add(new ConfirmedTerm("天地", "Thiên Địa Phase 3"));
        environment = environment with { Resources = changed };
    };
    coordinator.ProgressChanged += (_, _) =>
    {
        if (switchStarted == 0 || switchObserved != 0 ||
            coordinator.CurrentJob?.ProjectResourceRevision != environment.Resources.Revision) return;
        Interlocked.CompareExchange(ref switchObserved, Stopwatch.GetTimestamp(), 0);
    };

    using StorageIoMetricsCapture capture = StorageIoMetricsCapture.Begin();
    long resumeStarted = Stopwatch.GetTimestamp();
    var watch = Stopwatch.StartNew();
    await coordinator.ResumeWithCurrentConfigurationAsync();
    watch.Stop();
    StorageIoMetricsSnapshot io = capture.Snapshot();
    ProjectTranslationJob result = coordinator.CurrentJob!;
    double switchMilliseconds = switchObserved > switchStarted
        ? Stopwatch.GetElapsedTime(switchStarted, switchObserved).TotalMilliseconds : double.NaN;
    long firstRequest = new[] { local.FirstRequestTimestamp, remote.FirstRequestTimestamp }
        .Where(value => value > 0).DefaultIfEmpty().Min();
    double resumeMilliseconds = firstRequest > resumeStarted
        ? Stopwatch.GetElapsedTime(resumeStarted, firstRequest).TotalMilliseconds : double.NaN;
    long projectBytes = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    Console.WriteLine($"Chapters: {chapterCount:N0}");
    Console.WriteLine($"ElapsedMs: {watch.Elapsed.TotalMilliseconds:F3}");
    Console.WriteLine($"ThroughputChaptersPerSecond: {chapterCount / watch.Elapsed.TotalSeconds:F3}");
    Console.WriteLine($"Workers: {workerCount}");
    Console.WriteLine($"ResumeToFirstRequestMs: {resumeMilliseconds:F3}");
    Console.WriteLine($"ConfigurationRefreshObservedMs: {switchMilliseconds:F3}");
    Console.WriteLine($"JobRevisionWrites: {result.Revision - revisionBefore:N0}");
    Console.WriteLine($"Completed: {result.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed):N0}");
    Console.WriteLine($"DistinctChapterIds: {result.Items.Select(item => item.ChapterId).Distinct().Count():N0}");
    Console.WriteLine($"LocalCalls: {local.Calls:N0}; RemoteCalls: {remote.Calls:N0}");
    Console.WriteLine($"IoReads: {io.FileReads:N0}; IoWrites: {io.FileWrites:N0}; BytesWritten: {io.BytesWritten:N0}");
    Console.WriteLine($"ProjectBytes: {projectBytes:N0}");
    Console.WriteLine($"CancelRunningRequestMs: {await MeasureCancellationAsync():F3}");
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, true);
}

static async Task<double> MeasureCancellationAsync()
{
    string root = Path.Combine(Path.GetTempPath(), "QTStudio-Phase3-Cancel", Guid.NewGuid().ToString("N"));
    try
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(root, "Cancel benchmark");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương 1", "天地玄黄宇宙洪荒。");
        ProjectChapterEntry entry = await repository.AddChapterAsync(session, chapter, 1, "第一章");
        var resources = new ProjectResources
        {
            ProjectId = session.Manifest.ProjectId,
            Characters = new CharacterProject
            {
                Id = session.Manifest.ProjectId, Name = "Cancel benchmark", UpdatedAt = DateTimeOffset.UtcNow
            }
        };
        var client = new HangingClient();
        Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(
            new ProjectTranslationEnvironment(new ModelOptions(), PromptPreset.Default, resources.Clone()));
        var coordinator = new ProjectTranslationCoordinator(session, repository, client, Load);
        Task run = coordinator.StartAsync(await coordinator.PlanAsync([entry.ChapterId]));
        await client.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var watch = Stopwatch.StartNew();
        await coordinator.StopAsync();
        await run;
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

file sealed class ImmediateClient(string name) : IOllamaClient
{
    private int _calls;
    private long _firstRequestTimestamp;
    public int Calls => Volatile.Read(ref _calls);
    public long FirstRequestTimestamp => Volatile.Read(ref _firstRequestTimestamp);

    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.CompareExchange(ref _firstRequestTimestamp, Stopwatch.GetTimestamp(), 0);
        int call = Interlocked.Increment(ref _calls);
        return Task.FromResult(new OllamaCompletion($"{name} synthetic {call}", true, "stop",
            new GenerationMetrics { InputTokens = 100, OutputTokens = 40, ClientElapsed = TimeSpan.FromMilliseconds(1) }));
    }
}

file sealed class HangingClient : IOllamaClient
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new UnreachableException();
    }
}
