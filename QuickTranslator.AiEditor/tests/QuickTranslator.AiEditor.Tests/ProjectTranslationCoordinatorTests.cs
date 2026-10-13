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
    public async Task Changing_model_options_mid_run_interrupts_once_and_replans_only_unfinished_chapters()
    {
        await WithProjectAsync(3, async context =>
        {
            ModelOptions currentOptions = context.Environment.ModelOptions;
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(
                context.Environment with { ModelOptions = currentOptions with { } });
            var client = new ProjectQueueFakeClient { BlockFirst = true };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, client, Load);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            currentOptions = currentOptions with { Temperature = currentOptions.Temperature + 0.1 };
            client.ReleaseFirst.TrySetResult();
            await run;

            ProjectTranslationJob interrupted = coordinator.CurrentJob!;
            Assert.Equal(ProjectTranslationJobStatus.Interrupted, interrupted.Status);
            Assert.Equal(1, interrupted.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed));
            Assert.Equal(2, interrupted.Items.Count(item => item.Status == ProjectTranslationItemStatus.Pending));
            Assert.DoesNotContain(interrupted.Items, item => item.Status == ProjectTranslationItemStatus.NeedsReview);
            Assert.Equal(1, client.Calls);

            ProjectTranslationJob replacement = await coordinator.ReplanUnfinishedAsync();

            Assert.Equal(ProjectTranslationJobStatus.Draft, replacement.Status);
            Assert.Equal(2, replacement.Items.Count);
            Assert.Equal(currentOptions, replacement.ModelOptions);
            Assert.Null(coordinator.CurrentJob);

            await coordinator.StartAsync(replacement);
            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
            Assert.Equal(3, client.Calls);
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

    [Fact]
    public async Task Backend_snapshot_blocks_start_or_resume_after_profile_changes()
    {
        await WithProjectAsync(1, async context =>
        {
            OllamaBackendSnapshot local = OllamaBackendSnapshot.LocalDefault(context.Environment.ModelOptions.Model);
            OllamaBackendSnapshot current = local;
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(
                context.Environment with { Backend = current });
            var client = new ProjectQueueFakeClient();
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, client, Load);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds);
            Assert.Equal(local, plan.Backend);

            current = new(Guid.NewGuid(), OllamaBackendKind.Remote, "GPU thuê", "https://gpu.example.com/api/chat",
                context.Environment.ModelOptions.Model);
            InvalidOperationException startError = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.StartAsync(plan));

            Assert.Contains("backend", startError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, client.Calls);
            Assert.Null(coordinator.CurrentJob);
        });
    }

    [Fact]
    public async Task Reopened_interrupted_job_keeps_backend_snapshot_and_blocks_resume_on_another_server()
    {
        await WithProjectAsync(1, async context =>
        {
            OllamaBackendSnapshot local = OllamaBackendSnapshot.LocalDefault(context.Environment.ModelOptions.Model);
            OllamaBackendSnapshot current = local;
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(
                context.Environment with { Backend = current });
            var store = new ProjectTranslationJobStore(context.Session);
            var first = new ProjectTranslationCoordinator(context.Session, context.Repository,
                new ProjectQueueFakeClient(), Load, store);
            ProjectTranslationJob saved = await store.SaveNewAsync(await first.PlanAsync(context.ChapterIds));
            saved.Status = ProjectTranslationJobStatus.Interrupted;
            saved = await store.SaveAsync(saved);

            current = new(Guid.NewGuid(), OllamaBackendKind.Remote, "GPU sau restart",
                "https://gpu.example.com/api/chat", context.Environment.ModelOptions.Model);
            var client = new ProjectQueueFakeClient();
            var reopened = new ProjectTranslationCoordinator(context.Session, context.Repository, client, Load,
                new ProjectTranslationJobStore(context.Session));
            ProjectTranslationJob loaded = Assert.IsType<ProjectTranslationJob>(await reopened.LoadAsync());

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ResumeAsync());

            Assert.Equal(local, loaded.Backend);
            Assert.Contains("Queue Plan", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, client.Calls);
            Assert.Equal(ProjectTranslationJobStatus.Interrupted,
                Assert.IsType<ProjectTranslationJob>(await new ProjectTranslationJobStore(context.Session).LoadAsync()).Status);
        });
    }

    [Fact]
    public async Task Legacy_job_without_backend_snapshot_resumes_on_default_local_backend()
    {
        await WithProjectAsync(1, async context =>
        {
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(context.Environment);
            var store = new ProjectTranslationJobStore(context.Session);
            var first = new ProjectTranslationCoordinator(context.Session, context.Repository,
                new ProjectQueueFakeClient(), Load, store);
            ProjectTranslationJob legacy = await first.PlanAsync(context.ChapterIds);
            legacy.Backend = null;
            ProjectTranslationJob saved = await store.SaveNewAsync(legacy);
            saved.Status = ProjectTranslationJobStatus.Interrupted;
            await store.SaveAsync(saved);
            var client = new ProjectQueueFakeClient();
            var reopened = new ProjectTranslationCoordinator(context.Session, context.Repository, client, Load,
                new ProjectTranslationJobStore(context.Session));

            ProjectTranslationJob loaded = Assert.IsType<ProjectTranslationJob>(await reopened.LoadAsync());
            await reopened.ResumeAsync();

            Assert.Null(loaded.Backend);
            Assert.Equal(1, client.Calls);
            Assert.Equal(ProjectTranslationJobStatus.Completed, reopened.CurrentJob!.Status);
        });
    }

    [Fact]
    public async Task Two_workers_claim_unique_chapters_and_faster_worker_takes_more_work()
    {
        await WithProjectAsync(8, async context =>
        {
            var concurrency = new ParallelConcurrency();
            // Repository checkpoints are deliberately serialized; make the provider
            // speed gap larger than disk latency so pull-based assignment is observable.
            var slow = new ParallelFakeClient("local", concurrency, 1200);
            var fast = new ParallelFakeClient("remote", concurrency, 1);
            ProjectTranslationWorkerDefinition[] workers = Workers(context, slow, fast);
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, workers,
                context.LoadEnvironment);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds);
            Guid[] manifestOrder = context.Session.Manifest.Chapters.OrderBy(chapter => chapter.Number)
                .Select(chapter => chapter.ChapterId).ToArray();

            await coordinator.StartAsync(plan);

            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
            Assert.Equal(2, concurrency.Maximum);
            Assert.Equal(8, slow.Calls + fast.Calls);
            Assert.True(fast.Calls > slow.Calls);
            Assert.Equal(8, coordinator.CurrentJob.Items.Select(item => item.ChapterId).Distinct().Count());
            Assert.Equal(manifestOrder, context.Session.Manifest.Chapters.OrderBy(chapter => chapter.Number)
                .Select(chapter => chapter.ChapterId));
            foreach (Guid id in context.ChapterIds)
            {
                ChapterDocument chapter = (await context.Repository.LoadChapterAsync(context.Session, id)).Chapter;
                Assert.Single(chapter.Segments[0].Attempts);
            }
        });
    }

    [Fact]
    public async Task Parallel_pause_waits_for_both_workers_and_resume_keeps_committed_checkpoints()
    {
        await WithProjectAsync(6, async context =>
        {
            var concurrency = new ParallelConcurrency();
            var local = new ParallelFakeClient("local", concurrency, 0) { BlockFirst = true };
            var remote = new ParallelFakeClient("remote", concurrency, 0) { BlockFirst = true };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository,
                Workers(context, local, remote), context.LoadEnvironment);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await Task.WhenAll(local.FirstStarted.Task, remote.FirstStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

            Task pause = coordinator.PauseAsync();
            local.Release.TrySetResult(); remote.Release.TrySetResult();
            await pause; await run;
            Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob!.Status);
            int committedAtPause = coordinator.CurrentJob.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed);

            await coordinator.ResumeAsync();

            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
            Assert.Equal(6, coordinator.CurrentJob.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed));
            Assert.InRange(committedAtPause, 0, 2);
            Assert.Equal(6, local.Calls + remote.Calls);
        });
    }

    [Fact]
    public async Task Remote_failure_marks_unknown_chapter_needs_review_while_local_finishes_pending()
    {
        await WithProjectAsync(6, async context =>
        {
            var concurrency = new ParallelConcurrency();
            var local = new ParallelFakeClient("local", concurrency, 8);
            var remote = new ParallelFakeClient("remote", concurrency, 1) { FailWithConnection = true };
            ProjectTranslationWorkerDefinition[] workers = Workers(context, local, remote);
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, workers,
                context.LoadEnvironment);

            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));

            ProjectTranslationJob result = coordinator.CurrentJob!;
            Assert.Equal(ProjectTranslationJobStatus.CompletedWithErrors, result.Status);
            Assert.Single(result.Items, item => item.Status == ProjectTranslationItemStatus.NeedsReview);
            Assert.Equal(5, result.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed));
            Assert.Equal(ProjectTranslationWorkerStatus.Offline,
                result.Workers.Single(worker => worker.Backend.Kind == OllamaBackendKind.Remote).Status);
            Assert.True(local.Calls >= 5);
            Assert.Equal(1, remote.Calls);
        });
    }

    [Fact]
    public async Task Reopen_interrupts_each_running_worker_and_never_auto_resends()
    {
        await WithProjectAsync(3, async context =>
        {
            var local = new ParallelFakeClient("local", new(), 0);
            var remote = new ParallelFakeClient("remote", new(), 0);
            ProjectTranslationWorkerDefinition[] workers = Workers(context, local, remote);
            var store = new ProjectTranslationJobStore(context.Session);
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, workers,
                context.LoadEnvironment, store);
            ProjectTranslationJob saved = await store.SaveNewAsync(await coordinator.PlanAsync(context.ChapterIds));
            saved.Status = ProjectTranslationJobStatus.Running;
            for (int index = 0; index < 2; index++)
            {
                saved.Workers[index].Status = ProjectTranslationWorkerStatus.Running;
                saved.Workers[index].CurrentChapterId = saved.Items[index].ChapterId;
                saved.Items[index].Status = ProjectTranslationItemStatus.Running;
                saved.Items[index].AssignedWorkerId = saved.Workers[index].WorkerId;
            }
            await store.SaveAsync(saved);

            var reopened = new ProjectTranslationCoordinator(context.Session, context.Repository, workers,
                context.LoadEnvironment, new(context.Session));
            ProjectTranslationJob interrupted = Assert.IsType<ProjectTranslationJob>(await reopened.LoadAsync());

            Assert.Equal(ProjectTranslationJobStatus.Interrupted, interrupted.Status);
            Assert.Equal(2, interrupted.Items.Count(item => item.Status == ProjectTranslationItemStatus.Interrupted));
            Assert.All(interrupted.Workers, worker => Assert.Equal(ProjectTranslationWorkerStatus.Interrupted, worker.Status));
            Assert.Equal(0, local.Calls + remote.Calls);
        });
    }

    [Fact]
    public async Task Unknown_remote_result_requires_explicit_confirmation_before_retry()
    {
        await WithProjectAsync(2, async context =>
        {
            var local = new ParallelFakeClient("local", new(), 20);
            var remote = new ParallelFakeClient("remote", new(), 1) { FailWithConnection = true };
            ProjectTranslationWorkerDefinition[] workers = Workers(context, local, remote);
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, workers,
                context.LoadEnvironment);
            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            Guid remoteId = coordinator.CurrentJob!.Workers.Single(worker => worker.Backend.Kind == OllamaBackendKind.Remote).WorkerId;

            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.RetryWorkerAsync(remoteId, false));
            await coordinator.RetryWorkerAsync(remoteId, true);

            Assert.Contains(coordinator.CurrentJob!.Items, item => item.AssignedWorkerId == remoteId &&
                item.Status == ProjectTranslationItemStatus.Pending);
            Assert.Equal(ProjectTranslationJobStatus.Interrupted, coordinator.CurrentJob.Status);
        });
    }

    [Fact]
    public async Task Changed_ssh_backend_identity_blocks_old_parallel_plan_before_any_request()
    {
        await WithProjectAsync(1, async context =>
        {
            var local = new ParallelFakeClient("local", new(), 0);
            var remote = new ParallelFakeClient("remote", new(), 0);
            ProjectTranslationWorkerDefinition[] original = Workers(context, local, remote);
            original[1] = original[1] with { BackendIdentity = "SHA256:original-host" };
            var planner = new ProjectTranslationCoordinator(context.Session, context.Repository, original,
                context.LoadEnvironment);
            ProjectTranslationJob plan = await planner.PlanAsync(context.ChapterIds);
            ProjectTranslationWorkerDefinition[] changed =
            [original[0], original[1] with { BackendIdentity = "SHA256:different-host" }];
            var starter = new ProjectTranslationCoordinator(context.Session, context.Repository, changed,
                context.LoadEnvironment);

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => starter.StartAsync(plan));

            Assert.Contains("đã đổi", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, local.Calls + remote.Calls);
            Assert.Null(starter.CurrentJob);
        });
    }

    private static ProjectTranslationWorkerDefinition[] Workers(TestProject context, IOllamaClient local, IOllamaClient remote)
    {
        ModelOptions options = context.Environment.ModelOptions;
        return
        [
            new(Guid.NewGuid(), OllamaBackendSnapshot.LocalDefault(options.Model), local, options with { }),
            new(Guid.NewGuid(), new OllamaBackendSnapshot(Guid.NewGuid(), OllamaBackendKind.Remote,
                "EZYCloudX RTX 3090 Ti", "http://127.0.0.1:43117/api/chat", options.Model), remote,
                options with { }, 0.45m)
        ];
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
            var environment = new ProjectTranslationEnvironment(options with { }, preset with { }, resources.Clone());
            await test(new TestProject(session, repository, ids, Load, environment));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private sealed record TestProject(ProjectSession Session, ProjectRepository Repository, IReadOnlyList<Guid> ChapterIds,
        Func<CancellationToken, Task<ProjectTranslationEnvironment>> LoadEnvironment, ProjectTranslationEnvironment Environment)
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

    private sealed class ParallelConcurrency
    {
        private int _current;
        private int _maximum;
        public int Maximum => Volatile.Read(ref _maximum);
        public void Enter()
        {
            int active = Interlocked.Increment(ref _current);
            int observed;
            do { observed = _maximum; }
            while (active > observed && Interlocked.CompareExchange(ref _maximum, active, observed) != observed);
        }
        public void Exit() => Interlocked.Decrement(ref _current);
    }

    private sealed class ParallelFakeClient(string name, ParallelConcurrency concurrency, int delayMilliseconds) : IOllamaClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool BlockFirst { get; init; }
        public bool FailWithConnection { get; init; }
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            concurrency.Enter();
            try
            {
                if (call == 1)
                {
                    FirstStarted.TrySetResult();
                    if (BlockFirst) await Release.Task.WaitAsync(cancellationToken);
                }
                if (delayMilliseconds > 0) await Task.Delay(delayMilliseconds, cancellationToken);
                if (FailWithConnection)
                    throw new OllamaRequestException(OllamaErrorKind.Connection, "synthetic remote disconnect");
                return FakeClient.Completion($"{name} translated {call}");
            }
            finally { concurrency.Exit(); }
        }
    }
}
