using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ImportedChapterQueueRegressionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(),
        "qtaie-p0-import-queue-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Fifty_two_newly_imported_chapters_are_dispatched_after_reopen_and_worker_preflight()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "P0 synthetic import queue");
        await AddCompletedChapterAsync(repository, session, 398);
        await AddCompletedChapterAsync(repository, session, 399);

        const int importedCount = 52;
        ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(
            MultiChapterSource(400, importedCount), "p0-52-chapters.txt")).Current;
        var importDefaults = new BulkImportChapterDefaults(
            Segmentation(15, 20, 25), Model("p0-fake"), PromptPreset.Default);
        BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(
            session, plan, importDefaults, new BulkImportOptions(MaxChaptersPerChunk: 20));

        Assert.True(report.Completed);
        Assert.Equal(importedCount, report.ImportedCount);
        Guid[] importedIds = report.Chapters.Where(chapter => chapter.State == BulkImportChapterState.Imported)
            .Select(chapter => chapter.ChapterId).ToArray();
        Assert.Equal(importedCount, importedIds.Length);
        foreach (Guid chapterId in importedIds)
        {
            ChapterDocument imported = (await repository.LoadChapterAsync(session, chapterId)).Chapter;
            TranslationEligibilityDiagnostic diagnostic = TranslationEligibilityDiagnostics.Analyze(imported,
                QueueMode.MissingSegments, TranslationEligibilityDiagnosticStage.AfterImport);
            Assert.Equal(imported.Segments.Count, diagnostic.EligibleSegmentCount);
            Assert.Equal(imported.Segments.Count, diagnostic.UntranslatedSegmentCount);
            Assert.Equal(0, diagnostic.ExistingAttemptCount);
        }

        var reopenedRepository = new ProjectRepository();
        session = (await reopenedRepository.OpenAsync(session.ManifestPath)).Session;
        var before = new Dictionary<Guid, ChapterDocument>();
        foreach (Guid chapterId in importedIds)
        {
            ChapterDocument chapter = (await reopenedRepository.LoadChapterAsync(session, chapterId)).Chapter;
            before.Add(chapterId, chapter);
            Assert.InRange(chapter.Segments.Count, 8, 15);
            TranslationEligibilityDiagnostic diagnostic = TranslationEligibilityDiagnostics.Analyze(chapter,
                QueueMode.MissingSegments, TranslationEligibilityDiagnosticStage.AfterPersistedReload);
            Assert.Equal(chapter.Segments.Count, diagnostic.EligibleSegmentCount);
            Assert.Equal(chapter.Segments.Count, diagnostic.UntranslatedSegmentCount);
            Assert.Equal(0, diagnostic.ExistingAttemptCount);
            Assert.All(chapter.Segments, segment =>
            {
                Assert.Equal(SegmentStatus.Untranslated, segment.Status);
                Assert.Empty(segment.Attempts);
                Assert.Null(segment.UserDraft);
                Assert.Empty(segment.Approvals);
                Assert.False(segment.ProtectedByPreviousEdit);
                Assert.False(segment.HasCompletedTranslation);
            });
        }

        var client = new RecordingClient();
        ModelOptions model = Model("p0-fake");
        PromptPreset preset = PromptPreset.Default with { UpdatedAt = DateTimeOffset.UnixEpoch };
        var worker = new ProjectTranslationWorkerDefinition(Guid.NewGuid(),
            OllamaBackendSnapshot.LocalDefault(model.Model), client, model,
            Segmentation(18, 24, 30));
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(
            new ProjectTranslationEnvironment(model, preset, resources.Clone()));
        var coordinator = new ProjectTranslationCoordinator(session, reopenedRepository, [worker], Load);
        Guid[] allChapterIds = session.Manifest.Chapters.Where(entry => entry.DeletedAt is null)
            .OrderBy(entry => entry.Number).Select(entry => entry.ChapterId).ToArray();

        await coordinator.StartAsync(await coordinator.PlanAsync(allChapterIds));

        ProjectTranslationJob completed = coordinator.CurrentJob!;
        Assert.True(completed.Status == ProjectTranslationJobStatus.Completed,
            string.Join(Environment.NewLine, completed.Items.Where(item => item.Status != ProjectTranslationItemStatus.Completed)
                .Select(item => $"{item.Number}: {item.Status} - {item.Error}")));
        Assert.All(completed.Items.Where(item => item.Number is 398 or 399), item =>
        {
            Assert.Equal(ProjectTranslationItemStatus.Completed, item.Status);
            Assert.Equal(ProjectTranslationItemDisposition.AlreadyTranslated, item.Disposition);
        });
        Assert.DoesNotContain(completed.Items.Where(item => importedIds.Contains(item.ChapterId)),
            item => item.Status == ProjectTranslationItemStatus.Skipped);
        int expectedRequests = 0;
        foreach (Guid chapterId in importedIds)
        {
            ChapterDocument chapter = (await reopenedRepository.LoadChapterAsync(session, chapterId)).Chapter;
            ProjectTranslationItem item = completed.Items.Single(value => value.ChapterId == chapterId);
            Assert.Equal(ProjectTranslationItemStatus.Completed, item.Status);
            Assert.Equal(ProjectTranslationItemDisposition.TranslatedThisJob, item.Disposition);
            Assert.Contains(item.EligibilityDiagnostics,
                value => value.Stage == TranslationEligibilityDiagnosticStage.Plan);
            Assert.Contains(item.EligibilityDiagnostics,
                value => value.Stage == TranslationEligibilityDiagnosticStage.AfterWorkerPreflight);
            TranslationEligibilityDiagnostic beforeEligibility = Assert.Single(item.EligibilityDiagnostics,
                value => value.Stage == TranslationEligibilityDiagnosticStage.BeforeEligibilityDecision);
            Assert.True(beforeEligibility.EligibleSegmentCount > 0);
            Assert.True(beforeEligibility.WorkerPreflightChangedSegmentation);
            expectedRequests += chapter.Segments.Count;
            Assert.All(chapter.Segments, segment =>
            {
                TranslationAttempt attempt = Assert.Single(segment.Attempts);
                Assert.Equal(AttemptOutcome.Completed, attempt.Outcome);
                Assert.Null(segment.UserDraft);
                Assert.Empty(segment.Approvals);
                Assert.False(segment.ProtectedByPreviousEdit);
            });
            Assert.Equal(before[chapterId].OriginalText, chapter.OriginalText);
        }
        Assert.Equal(expectedRequests, client.RequestCount);

        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        foreach (Guid chapterId in importedIds)
        {
            ChapterDocument chapter = (await reopenedRepository.LoadChapterAsync(reopened, chapterId)).Chapter;
            Assert.All(chapter.Segments, segment => Assert.True(segment.HasCompletedTranslation));
        }
    }

    private static async Task AddCompletedChapterAsync(ProjectRepository repository,
        ProjectSession session, int number)
    {
        ChapterDocument chapter = TranslationService.CreateChapter("Chương " + number,
            string.Concat(Enumerable.Repeat("天地玄黄宇宙洪荒。", 10)), Segmentation(15, 20, 25), Model("p0-fake"));
        foreach (TranslationSegment segment in chapter.Segments)
        {
            segment.Attempts.Add(new TranslationAttempt
            {
                StartedAt = DateTimeOffset.UtcNow,
                FinishedAt = DateTimeOffset.UtcNow,
                Outcome = AttemptOutcome.Completed,
                ModelOptions = Model("p0-fake"),
                PromptVersion = "synthetic",
                Prompt = "synthetic",
                AiText = "Bản dịch đã hoàn thành."
            });
            segment.Status = SegmentStatus.AwaitingReview;
        }
        await repository.AddChapterAsync(session, chapter, number);
    }

    private static string MultiChapterSource(int firstNumber, int count) => string.Concat(
        Enumerable.Range(firstNumber, count).Select(number =>
            $"第{number}章 标题{number}\n" + string.Concat(Enumerable.Range(1, 12)
                .Select(line => $"第{line}段天地玄黄宇宙洪荒日月盈昃。\n"))));

    private static SegmenterOptions Segmentation(int minimum, int target, int maximum) => new()
    {
        MinimumHanCharacters = minimum,
        TargetHanCharacters = target,
        MaximumHanCharacters = maximum,
        MaximumTextElements = 4000
    };

    private static ModelOptions Model(string model) => new()
    {
        Model = model,
        ContextSize = 4096,
        MaximumOutputTokens = 1024,
        Temperature = 0.1,
        RequestTimeout = TimeSpan.FromSeconds(30)
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class RecordingClient : IOllamaClient
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(FakeClient.Completion("Bản dịch synthetic hợp lệ."));
        }
    }
}
