using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectTranslationCoordinatorTests
{
    [Theory]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(100)]
    public async Task Translates_chapters_strictly_sequentially_and_reopens_checkpoint(int count)
    {
        await WithProjectAsync(count, async context =>
        {
            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds);

            await coordinator.StartAsync(plan);

            ProjectTranslationProgress progress = coordinator.GetProgress();
            Assert.Equal(ProjectTranslationJobStatus.Completed, progress.Status);
            Assert.Equal(count, progress.CompletedChapters);
            Assert.Equal(count, client.Calls);
            Assert.Equal(1, client.MaximumConcurrency);
            var reopened = new ProjectTranslationCoordinator(context.Session, context.Repository, client, context.LoadEnvironment);
            ProjectTranslationJob saved = Assert.IsType<ProjectTranslationJob>(await reopened.LoadAsync());
            Assert.Equal(ProjectTranslationJobStatus.Completed, saved.Status);
            Assert.All(saved.Items, item => Assert.Equal(ProjectTranslationItemStatus.Completed, item.Status));
        });
    }

    [Fact]
    public async Task Pause_waits_for_active_request_then_resume_preserves_checkpoint()
    {
        await WithProjectAsync(5, async context =>
        {
            var client = new ProjectQueueFakeClient { BlockFirst = true };
            var coordinator = context.Coordinator(client);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task pause = coordinator.PauseAsync();
            client.ReleaseFirst.TrySetResult();
            await pause;
            await run;
            int callsAtPause = client.Calls;
            Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.GetProgress().Status);

            await coordinator.ResumeAsync();

            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.GetProgress().Status);
            Assert.Equal(5, coordinator.GetProgress().CompletedChapters);
            Assert.Equal(5, client.Calls);
            Assert.True(callsAtPause >= 1);
        });
    }

    [Fact]
    public async Task Cancel_pending_keeps_committed_result_and_never_sends_cancelled_chapters()
    {
        await WithProjectAsync(5, async context =>
        {
            var client = new ProjectQueueFakeClient { BlockFirst = true };
            var coordinator = context.Coordinator(client);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task cancel = coordinator.CancelPendingAsync();
            client.ReleaseFirst.TrySetResult();
            await cancel;
            await run;

            ProjectTranslationJob job = coordinator.CurrentJob!;
            Assert.Equal(ProjectTranslationJobStatus.Cancelled, job.Status);
            Assert.InRange(client.Calls, 1, 1);
            Assert.Equal(4, job.Items.Count(item => item.Status == ProjectTranslationItemStatus.Cancelled));
            ChapterDocument first = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[0])).Chapter;
            Assert.NotEmpty(first.Segments[0].Attempts);
        });
    }

    [Fact]
    public async Task Reopen_marks_running_job_interrupted_without_starting_translation()
    {
        await WithProjectAsync(2, async context =>
        {
            var store = new ProjectTranslationJobStore(context.Session);
            ProjectTranslationJob plan = await context.Coordinator(new ProjectQueueFakeClient()).PlanAsync(context.ChapterIds);
            ProjectTranslationJob saved = await store.SaveNewAsync(plan);
            saved.Status = ProjectTranslationJobStatus.Running;
            saved.Items[0].Status = ProjectTranslationItemStatus.Running;
            saved.CurrentChapterId = saved.Items[0].ChapterId;
            await store.SaveAsync(saved);
            var client = new ProjectQueueFakeClient();

            var reopened = new ProjectTranslationCoordinator(context.Session, context.Repository, client,
                context.LoadEnvironment, new ProjectTranslationJobStore(context.Session));
            ProjectTranslationJob recovered = Assert.IsType<ProjectTranslationJob>(await reopened.LoadAsync());

            Assert.Equal(ProjectTranslationJobStatus.Interrupted, recovered.Status);
            Assert.Equal(ProjectTranslationItemStatus.Interrupted, recovered.Items[0].Status);
            Assert.Equal(0, client.Calls);
        });
    }

    [Fact]
    public async Task Retry_failed_chapter_uses_existing_chapter_checkpoint()
    {
        await WithProjectAsync(3, async context =>
        {
            var client = new ProjectQueueFakeClient();
            client.FailCalls.Add(1);
            var coordinator = context.Coordinator(client);
            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            Assert.Equal(ProjectTranslationJobStatus.CompletedWithErrors, coordinator.GetProgress().Status);
            Assert.Equal(1, coordinator.GetProgress().FailedChapters);
            client.FailCalls.Clear();

            await coordinator.RetryFailedAsync();

            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.GetProgress().Status);
            Assert.Equal(4, client.Calls);
            Assert.Equal(3, coordinator.GetProgress().CompletedChapters);
        });
    }

    [Fact]
    public async Task Approved_and_user_drafts_are_skipped_and_preserved()
    {
        await WithProjectAsync(3, async context =>
        {
            await EditAsync(context, 0, service => service.UpdateDraft(service.GetSnapshot().Segments[0].Id, "Nháp người dùng."));
            await EditAsync(context, 1, async service =>
            {
                Guid id = service.GetSnapshot().Segments[0].Id;
                service.UpdateDraft(id, "Bản đã duyệt.");
                await service.ApproveAsync(id);
            });
            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);

            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));

            Assert.Equal(1, client.Calls);
            Assert.Equal(2, coordinator.CurrentJob!.Items.Count(item => item.Status == ProjectTranslationItemStatus.Skipped));
            ChapterDocument draft = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[0])).Chapter;
            ChapterDocument approved = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[1])).Chapter;
            Assert.Equal("Nháp người dùng.", draft.Segments[0].UserDraft);
            Assert.Equal("Bản đã duyệt.", approved.Segments[0].EditableText);
            Assert.Single(approved.Segments[0].Approvals);
        });
    }

    [Fact]
    public async Task Edit_after_enqueue_marks_needs_review_without_sending_AI_request()
    {
        await WithProjectAsync(1, async context =>
        {
            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds);
            await EditAsync(context, 0, service => service.UpdateDraft(service.GetSnapshot().Segments[0].Id, "Sửa sau enqueue."));

            await coordinator.StartAsync(plan);

            Assert.Equal(0, client.Calls);
            Assert.Equal(ProjectTranslationItemStatus.NeedsReview, coordinator.CurrentJob!.Items[0].Status);
            Assert.Contains("sau khi enqueue", coordinator.CurrentJob.Items[0].Error);
        });
    }

    [Fact]
    public async Task Late_AI_result_cannot_overwrite_edit_committed_while_request_is_running()
    {
        await WithProjectAsync(1, async context =>
        {
            var client = new ProjectQueueFakeClient { BlockFirst = true };
            var coordinator = context.Coordinator(client);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await EditAsync(context, 0, service =>
                service.UpdateDraft(service.GetSnapshot().Segments[0].Id, "Nháp thắng kết quả AI đến muộn."));

            client.ReleaseFirst.TrySetResult();
            await run;

            ProjectTranslationItem item = coordinator.CurrentJob!.Items[0];
            Assert.Equal(ProjectTranslationItemStatus.NeedsReview, item.Status);
            ChapterDocument saved = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[0])).Chapter;
            Assert.Equal("Nháp thắng kết quả AI đến muộn.", saved.Segments[0].UserDraft);
            Assert.DoesNotContain(saved.Segments[0].Attempts, attempt => attempt.AiText == "Bản dịch tuần tự 1.");
        });
    }

    [Fact]
    public async Task Two_coordinators_cannot_write_the_same_project()
    {
        await WithProjectAsync(2, async context =>
        {
            var firstClient = new ProjectQueueFakeClient { BlockFirst = true };
            var first = context.Coordinator(firstClient);
            var second = context.Coordinator(new ProjectQueueFakeClient());
            ProjectTranslationJob firstPlan = await first.PlanAsync(context.ChapterIds);
            ProjectTranslationJob secondPlan = await second.PlanAsync(context.ChapterIds);
            Task running = first.StartAsync(firstPlan);
            await firstClient.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.ThrowsAsync<InvalidOperationException>(() => second.StartAsync(secondPlan));

            Task cancel = first.CancelPendingAsync();
            firstClient.ReleaseFirst.TrySetResult();
            await cancel;
            await running;
        });
    }

    private static async Task EditAsync(TestProject context, int index, Action<TranslationService> edit)
    {
        ChapterDocument chapter = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[index])).Chapter;
        var service = new TranslationService(chapter, new ProjectQueueFakeClient(),
            new RegisteredChapterStore(context.Session, context.Repository));
        edit(service);
        await service.SaveAsync();
    }

    private static async Task EditAsync(TestProject context, int index, Func<TranslationService, Task> edit)
    {
        ChapterDocument chapter = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[index])).Chapter;
        var service = new TranslationService(chapter, new ProjectQueueFakeClient(),
            new RegisteredChapterStore(context.Session, context.Repository));
        await edit(service);
        await service.SaveAsync();
    }

    private static async Task WithProjectAsync(int chapterCount, Func<TestProject, Task> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "QTStudio-M4-Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProjectRepository();
            ProjectSession session = await repository.CreateAsync(root, "M4 synthetic");
            var ids = new List<Guid>(chapterCount);
            for (int number = 1; number <= chapterCount; number++)
            {
                ChapterDocument chapter = TranslationService.CreateChapter($"Chương {number}", $"天地玄黄宇宙洪荒第{number}章。",
                    new() { MinimumHanCharacters = 1, TargetHanCharacters = 100, MaximumHanCharacters = 200 });
                ProjectChapterEntry entry = await repository.AddChapterAsync(session, chapter, number, $"第{number}章");
                ids.Add(entry.ChapterId);
            }
            var resources = new ProjectResources
            {
                ProjectId = session.Manifest.ProjectId,
                Revision = 0,
                Characters = new CharacterProject
                {
                    Id = session.Manifest.ProjectId,
                    Name = "M4 synthetic",
                    UpdatedAt = DateTimeOffset.UtcNow
                }
            };
            var options = new ModelOptions();
            PromptPreset preset = PromptPreset.Default with { UpdatedAt = DateTimeOffset.UnixEpoch };
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) =>
                Task.FromResult(new ProjectTranslationEnvironment(options with { }, preset with { }, resources.Clone()));
            await test(new TestProject(session, repository, ids, Load));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed record TestProject(ProjectSession Session, ProjectRepository Repository, IReadOnlyList<Guid> ChapterIds,
        Func<CancellationToken, Task<ProjectTranslationEnvironment>> LoadEnvironment)
    {
        public ProjectTranslationCoordinator Coordinator(IOllamaClient client) =>
            new(Session, Repository, client, LoadEnvironment);
    }

    private sealed class ProjectQueueFakeClient : IOllamaClient
    {
        private int _calls;
        private int _concurrency;
        private int _maximumConcurrency;
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public bool BlockFirst { get; init; }
        public HashSet<int> FailCalls { get; } = [];
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _concurrency);
            int observed;
            do { observed = _maximumConcurrency; }
            while (active > observed && Interlocked.CompareExchange(ref _maximumConcurrency, active, observed) != observed);
            try
            {
                if (call == 1)
                {
                    FirstStarted.TrySetResult();
                    if (BlockFirst) await ReleaseFirst.Task.WaitAsync(cancellationToken);
                }
                if (FailCalls.Contains(call)) throw new OllamaRequestException(OllamaErrorKind.Http, "Synthetic Ollama failure.");
                return FakeClient.Completion($"Bản dịch tuần tự {call}.");
            }
            finally { Interlocked.Decrement(ref _concurrency); }
        }
    }
}
