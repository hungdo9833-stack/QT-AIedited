using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class WorkerSegmentationPreflightTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-worker-preflight-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();

    [Fact]
    public async Task Two_workers_snapshot_and_apply_independent_model_and_segmentation_options()
    {
        (ProjectSession session, Guid[] ids, Func<CancellationToken, Task<ProjectTranslationEnvironment>> load) =
            await CreateProjectAsync(2);
        var localClient = new CapturingClient();
        var remoteClient = new CapturingClient();
        ModelOptions localModel = Model("local-model", 4096, 2048, 0.1, 300);
        ModelOptions remoteModel = Model("remote-model", 8192, 2048, 0.2, 420);
        SegmenterOptions localSegmentation = Segmentation(250, 375, 500);
        SegmenterOptions remoteSegmentation = Segmentation(350, 550, 750);
        ProjectTranslationWorkerDefinition[] workers =
        [
            new(Guid.NewGuid(), GpuBackendSnapshot.LocalDefault(localModel.Model), localClient,
                localModel, localSegmentation),
            new(Guid.NewGuid(), new GpuBackendSnapshot(Guid.NewGuid(), GpuBackendKind.Remote,
                "RTX 5060 Ti Remote", "http://127.0.0.1:43117/api/chat", remoteModel.Model), remoteClient,
                remoteModel, remoteSegmentation)
        ];
        var coordinator = new ProjectTranslationCoordinator(session, _repository, workers, load);
        ProjectTranslationJob plan = await coordinator.PlanAsync(ids);

        await coordinator.StartAsync(plan);

        ProjectTranslationJob completed = coordinator.CurrentJob!;
        Assert.Equal(ProjectTranslationJobStatus.Completed, completed.Status);
        Assert.Equal(localModel, completed.Workers.Single(worker => worker.WorkerId == workers[0].WorkerId).ModelOptions);
        Assert.Equal(localSegmentation, completed.Workers.Single(worker => worker.WorkerId == workers[0].WorkerId).SegmentationOptions);
        Assert.Equal(remoteModel, completed.Workers.Single(worker => worker.WorkerId == workers[1].WorkerId).ModelOptions);
        Assert.Equal(remoteSegmentation, completed.Workers.Single(worker => worker.WorkerId == workers[1].WorkerId).SegmentationOptions);
        foreach (ProjectTranslationItem item in completed.Items)
        {
            ProjectTranslationWorkerDefinition worker = workers.Single(value => value.WorkerId == item.AssignedWorkerId);
            ChapterDocument chapter = (await _repository.LoadChapterAsync(session, item.ChapterId)).Chapter;
            Assert.Equal(worker.SegmentationOptions, chapter.SegmentationOptions);
            Assert.Equal(worker.SegmentationOptions, item.AppliedSegmentationOptions);
            Assert.NotNull(item.PreflightCompletedAt);
            Assert.Equal(ProjectTranslationFingerprints.Source(chapter), item.SourceFingerprint);
            Assert.Equal(ProjectTranslationFingerprints.Protected(chapter), item.ProtectedFingerprint);
        }
        Assert.All(localClient.Options, options => Assert.Equal(localModel, options));
        Assert.All(remoteClient.Options, options => Assert.Equal(remoteModel, options));
        Assert.NotEmpty(localClient.Options);
        Assert.NotEmpty(remoteClient.Options);

        ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
        Assert.False(Directory.Exists(reopened.Resolve("History/Transactions")));
    }

    [Fact]
    public async Task Partial_chapter_keeps_protected_anchor_and_only_translates_eligible_segments()
    {
        (ProjectSession session, Guid[] ids, Func<CancellationToken, Task<ProjectTranslationEnvironment>> load) =
            await CreateProjectAsync(1);
        ChapterDocument chapter = (await _repository.LoadChapterAsync(session, ids[0])).Chapter;
        TranslationSegment anchor = chapter.Segments[0];
        anchor.UserDraft = "Bản người dùng đã duyệt";
        anchor.DraftRevision = 1;
        anchor.Status = SegmentStatus.Approved;
        anchor.Approvals.Add(new ApprovalRecord { ApprovedAt = DateTimeOffset.UtcNow,
            DraftRevision = 1, Text = anchor.UserDraft });
        await _repository.UpdateChapterAsync(session, chapter);
        var client = new CapturingClient();
        SegmenterOptions segmentation = Segmentation(80, 120, 160);
        var worker = new ProjectTranslationWorkerDefinition(Guid.NewGuid(),
            GpuBackendSnapshot.LocalDefault("local-model"), client, Model("local-model", 4096, 1024, 0.1, 300), segmentation);
        var coordinator = new ProjectTranslationCoordinator(session, _repository, [worker], load);

        await coordinator.StartAsync(await coordinator.PlanAsync(ids));

        ChapterDocument after = (await _repository.LoadChapterAsync(session, ids[0])).Chapter;
        TranslationSegment preserved = after.Segments.Single(segment => segment.Id == anchor.Id);
        Assert.Equal("Bản người dùng đã duyệt", preserved.UserDraft);
        Assert.Single(preserved.Approvals);
        Assert.Equal(SegmentStatus.Approved, preserved.Status);
        Assert.DoesNotContain(preserved.Attempts, _ => true);
        Assert.Contains(after.Segments, segment => segment.Id != anchor.Id && segment.Attempts.Count > 0);
    }

    [Fact]
    public async Task Retranslating_a_translated_chapter_resplits_it_with_the_worker_segment_size_and_counts_the_new_run()
    {
        (ProjectSession session, Guid[] ids, Func<CancellationToken, Task<ProjectTranslationEnvironment>> load) =
            await CreateProjectAsync(1);
        var small = new ProjectTranslationWorkerDefinition(Guid.NewGuid(), GpuBackendSnapshot.LocalDefault("local-model"),
            new CapturingClient(), Model("local-model", 4096, 1024, 0.1, 300), Segmentation(80, 120, 160));
        var first = new ProjectTranslationCoordinator(session, _repository, [small], load);
        await first.StartAsync(await first.PlanAsync(ids));
        ChapterDocument translated = (await _repository.LoadChapterAsync(session, ids[0])).Chapter;
        Assert.All(translated.Segments, segment => Assert.True(segment.HasCompletedTranslation));
        TranslationSegment edited = translated.Segments[^1];
        edited.UserDraft = "Câu người dùng sửa";
        edited.DraftRevision = 1;
        await _repository.UpdateChapterAsync(session, translated);

        var client = new CapturingClient();
        SegmenterOptions large = Segmentation(300, 400, 500);
        var worker = new ProjectTranslationWorkerDefinition(Guid.NewGuid(), GpuBackendSnapshot.LocalDefault("local-model"),
            client, Model("local-model", 4096, 1024, 0.1, 300), large);
        var coordinator = new ProjectTranslationCoordinator(session, _repository, [worker], load);
        ProjectTranslationJob plan = await coordinator.PlanAsync(ids, retranslateAll: new HashSet<Guid>(ids));
        Assert.Equal(1, plan.Items[0].CompletedSegments); // Only the user's segment; old AI translations do not count.
        await coordinator.StartAsync(plan);

        ChapterDocument after = (await _repository.LoadChapterAsync(session, ids[0])).Chapter;
        Assert.True(after.Segments.Count < translated.Segments.Count);
        Assert.Equal(large, after.SegmentationOptions);
        Assert.Equal("Câu người dùng sửa", after.Segments.Single(segment => segment.Id == edited.Id).UserDraft);
        Assert.NotEmpty(after.SegmentationHistory);
        ProjectTranslationItem item = coordinator.CurrentJob!.Items[0];
        Assert.Equal(ProjectTranslationItemStatus.Completed, item.Status);
        Assert.Equal(after.Segments.Count, item.TotalSegments);
        Assert.Equal(after.Segments.Count, item.CompletedSegments);
        Assert.Equal(after.Segments.Count - 1, client.Options.Count);
    }

    [Fact]
    public async Task Failure_after_preflight_checkpoint_reports_interrupted_without_calling_provider()
    {
        (ProjectSession session, Guid[] ids, Func<CancellationToken, Task<ProjectTranslationEnvironment>> load) =
            await CreateProjectAsync(1);
        ChapterDocument before = (await _repository.LoadChapterAsync(session, ids[0])).Chapter;
        var client = new CapturingClient();
        var worker = new ProjectTranslationWorkerDefinition(Guid.NewGuid(),
            GpuBackendSnapshot.LocalDefault("local-model"), client, Model("local-model", 4096, 1024, 0.1, 300),
            Segmentation(80, 120, 160));
        _repository.FaultPoint = point =>
        {
            if (point == "AfterFastChapterWrite") throw new IOException("synthetic preflight failure");
        };
        var coordinator = new ProjectTranslationCoordinator(session, _repository, [worker], load);

        await coordinator.StartAsync(await coordinator.PlanAsync(ids));

        _repository.FaultPoint = null;
        ChapterDocument after = (await _repository.LoadChapterAsync(session, ids[0])).Chapter;
        Assert.NotEqual(before.SegmentationOptions, after.SegmentationOptions);
        Assert.Equal(worker.SegmentationOptions, after.SegmentationOptions);
        Assert.Empty(client.Options);
        Assert.Equal(ProjectTranslationJobStatus.Interrupted, coordinator.CurrentJob!.Status);
    }

    private async Task<(ProjectSession, Guid[], Func<CancellationToken, Task<ProjectTranslationEnvironment>>)> CreateProjectAsync(int count)
    {
        ProjectSession session = await _repository.CreateAsync(_root, "Worker preflight synthetic");
        var ids = new List<Guid>();
        string source = string.Concat(Enumerable.Repeat("天地玄黄宇宙洪荒。", 140));
        for (int number = 1; number <= count; number++)
        {
            ChapterDocument chapter = TranslationService.CreateChapter("Chương " + number, source,
                Segmentation(300, 400, 500));
            ids.Add((await _repository.AddChapterAsync(session, chapter, number)).ChapterId);
        }
        var resources = new ProjectResources
        {
            ProjectId = session.Manifest.ProjectId,
            Characters = new CharacterProject { Id = session.Manifest.ProjectId, Name = "Synthetic", UpdatedAt = DateTimeOffset.UtcNow }
        };
        PromptPreset preset = PromptPreset.Default with { UpdatedAt = DateTimeOffset.UnixEpoch };
        Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(new ProjectTranslationEnvironment(
            new ModelOptions(), preset, resources.Clone()));
        return (session, ids.ToArray(), Load);
    }

    private static ModelOptions Model(string model, int context, int output, double temperature, int timeout) => new()
    {
        Model = model, ContextSize = context, MaximumOutputTokens = output,
        Temperature = temperature, RequestTimeout = TimeSpan.FromSeconds(timeout)
    };

    private static SegmenterOptions Segmentation(int minimum, int target, int maximum) => new()
    {
        MinimumHanCharacters = minimum, TargetHanCharacters = target,
        MaximumHanCharacters = maximum, MaximumTextElements = 4000
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CapturingClient : ILlmClient
    {
        private readonly object _sync = new();
        private readonly List<ModelOptions> _options = [];
        public IReadOnlyList<ModelOptions> Options { get { lock (_sync) return _options.ToArray(); } }
        public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            lock (_sync) _options.Add(options with { });
            return Task.FromResult(FakeClient.Completion("Bản dịch synthetic."));
        }
    }
}
