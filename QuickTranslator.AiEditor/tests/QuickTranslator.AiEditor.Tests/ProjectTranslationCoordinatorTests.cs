using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed partial class ProjectTranslationCoordinatorTests
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
            if (count >= 100)
                Assert.InRange(coordinator.CurrentJob!.Revision, 1, 30);
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
    public async Task Crash_resume_reconciles_deferred_manifest_and_does_not_translate_completed_segment_again()
    {
        await WithProjectAsync(3, async context =>
        {
            var store = new ProjectTranslationJobStore(context.Session);
            var planner = context.Coordinator(new ProjectQueueFakeClient());
            ProjectTranslationJob savedJob = await store.SaveNewAsync(await planner.PlanAsync(context.ChapterIds));
            savedJob.Status = ProjectTranslationJobStatus.Interrupted;
            await store.SaveAsync(savedJob);
            ChapterDocument first = (await context.Repository.LoadChapterAsync(context.Session,
                context.ChapterIds[0])).Chapter;
            var firstClient = new ProjectQueueFakeClient();
            var service = new TranslationService(first, firstClient,
                new RegisteredChapterStore(context.Session, context.Repository));
            await service.TranslateAsync(first.Segments[0].Id);
            Assert.Equal(1, firstClient.Calls);

            var reopenedRepository = new ProjectRepository();
            ProjectSession reopenedSession = (await reopenedRepository.OpenAsync(context.Session.ManifestPath)).Session;
            var resumeClient = new ProjectQueueFakeClient();
            var resumed = new ProjectTranslationCoordinator(reopenedSession, reopenedRepository, resumeClient,
                context.LoadEnvironment, new ProjectTranslationJobStore(reopenedSession));
            await resumed.LoadAsync();

            await resumed.ResumeAsync();

            Assert.Equal(2, resumeClient.Calls);
            Assert.Equal(ProjectTranslationJobStatus.Completed, resumed.CurrentJob!.Status);
            Assert.Equal(3, resumed.CurrentJob.Items.Count(item =>
                item.Status == ProjectTranslationItemStatus.Completed));
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
    public async Task Changing_model_options_mid_run_refreshes_next_chapter_without_manual_replan()
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

            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
            Assert.Equal(3, client.Calls);
            Assert.Equal(currentOptions, coordinator.CurrentJob.ModelOptions);
            Assert.All(coordinator.CurrentJob.Items,
                item => Assert.Equal(ProjectTranslationItemStatus.Completed, item.Status));
        });
    }

    [Fact]
    public async Task Changing_glossary_after_three_chapters_applies_to_remaining_twenty_chapter_queue()
    {
        await WithProjectAsync(20, async context =>
        {
            ProjectTranslationEnvironment current = context.Environment;
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(current with
                { Resources = current.Resources.Clone() });
            var client = new ProjectQueueFakeClient();
            client.AfterCompletion = call =>
            {
                if (call != 3) return;
                ProjectResources changed = current.Resources.Clone();
                changed.Revision++;
                changed.Terms.Add(new("天地", "Thiên Địa mới"));
                current = current with { Resources = changed };
            };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, client, Load);

            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));

            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
            Assert.Equal(20, client.Calls);
            Assert.All(client.Prompts.Take(3), prompt => Assert.DoesNotContain("Thiên Địa mới", prompt));
            Assert.All(client.Prompts.Skip(3), prompt => Assert.Contains("Thiên Địa mới", prompt));
            Assert.Equal(current.Resources.Revision, coordinator.CurrentJob.ProjectResourceRevision);
        });
    }

    [Fact]
    public async Task Resume_after_pause_adopts_current_model_options_without_replan()
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
            Task pause = coordinator.PauseAsync();
            client.ReleaseFirst.TrySetResult();
            await pause;
            await run;
            currentOptions = currentOptions with { Temperature = 0.37 };

            await coordinator.ResumeAsync();

            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
            Assert.Equal(currentOptions, coordinator.CurrentJob.ModelOptions);
            Assert.Equal(3, client.Calls);
        });
    }

    [Fact]
    public async Task Retranslate_all_translates_finished_chapters_again_but_keeps_user_edits()
    {
        await WithProjectAsync(3, async context =>
        {
            var first = new ProjectQueueFakeClient();
            var initial = context.Coordinator(first);
            await initial.StartAsync(await initial.PlanAsync(context.ChapterIds));
            Assert.Equal(3, first.Calls);
            await EditAsync(context, 2, service => service.UpdateDraft(service.GetSnapshot().Segments[0].Id, "Nháp người dùng."));

            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds,
                retranslateAll: new HashSet<Guid> { context.ChapterIds[0], context.ChapterIds[2] });
            await coordinator.StartAsync(plan);

            Assert.Equal(1, client.Calls); // Chapter 1 again; chapter 2 was not chosen; chapter 3's only segment is a user draft.
            ProjectTranslationItem Item(int index) => coordinator.CurrentJob!.Items.Single(item => item.ChapterId == context.ChapterIds[index]);
            Assert.Equal(ProjectTranslationItemDisposition.TranslatedThisJob, Item(0).Disposition);
            Assert.Equal(ProjectTranslationItemDisposition.AlreadyTranslated, Item(1).Disposition);
            Assert.Equal(ProjectTranslationItemDisposition.ProtectedContent, Item(2).Disposition);
            ChapterDocument again = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[0])).Chapter;
            Assert.Equal(2, again.Segments[0].Attempts.Count(attempt => attempt.Outcome == AttemptOutcome.Completed));
            ChapterDocument edited = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[2])).Chapter;
            Assert.Equal("Nháp người dùng.", edited.Segments[0].UserDraft);
        });
    }

    [Fact]
    public async Task Paused_job_takes_new_chapters_at_its_end_and_reopens_finished_ones_for_retranslation()
    {
        await WithProjectAsync(3, async context =>
        {
            var first = new ProjectQueueFakeClient();
            var initial = context.Coordinator(first);
            await initial.StartAsync(await initial.PlanAsync([context.ChapterIds[0]]));
            var store = new ProjectTranslationJobStore(context.Session);
            ProjectTranslationJob saved = await store.SaveNewAsync(await context.Coordinator(first).PlanAsync(context.ChapterIds.Take(2)));
            ProjectTranslationItem done = saved.Items.Single(item => item.ChapterId == context.ChapterIds[0]);
            done.Status = ProjectTranslationItemStatus.Completed;
            done.Disposition = ProjectTranslationItemDisposition.AlreadyTranslated;
            done.FinishedAt = DateTimeOffset.UtcNow;
            saved.Status = ProjectTranslationJobStatus.Paused;
            await store.SaveAsync(saved);

            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);
            await coordinator.LoadAsync();
            ProjectTranslationAppendResult result = await coordinator.AppendAsync(
                [context.ChapterIds[0], context.ChapterIds[2]], new HashSet<Guid> { context.ChapterIds[0] });

            Assert.Equal(1, result.Added);
            Assert.Equal(1, result.Reopened);
            Assert.Equal(3, coordinator.CurrentJob!.Items.Single(item => item.ChapterId == context.ChapterIds[2]).Order);
            await coordinator.ResumeAsync();
            Assert.Equal(3, client.Calls); // Chapter 1 again (retranslation), chapter 2, then the appended chapter 3.
            Assert.All(coordinator.CurrentJob!.Items, item => Assert.Equal(ProjectTranslationItemStatus.Completed, item.Status));
            ChapterDocument again = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[0])).Chapter;
            Assert.Equal(2, again.Segments[0].Attempts.Count(attempt => attempt.Outcome == AttemptOutcome.Completed));
        });
    }

    [Fact]
    public async Task Append_is_refused_while_the_job_runs_or_after_it_completed()
    {
        await WithProjectAsync(2, async context =>
        {
            var coordinator = context.Coordinator(new ProjectQueueFakeClient());
            await coordinator.StartAsync(await coordinator.PlanAsync([context.ChapterIds[0]]));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AppendAsync([context.ChapterIds[1]]));
        });
    }

    [Theory]
    [InlineData(ProjectTranslationJobStatus.Paused, ProjectTranslationItemStatus.Pending)]
    [InlineData(ProjectTranslationJobStatus.Interrupted, ProjectTranslationItemStatus.Pending)]
    [InlineData(ProjectTranslationJobStatus.Paused, ProjectTranslationItemStatus.Paused)]
    [InlineData(ProjectTranslationJobStatus.Interrupted, ProjectTranslationItemStatus.Interrupted)]
    public async Task Append_retranslation_reopens_started_waiting_item_and_translates_failed_segment(
        ProjectTranslationJobStatus jobStatus, ProjectTranslationItemStatus itemStatus)
    {
        await WithProjectAsync(1, async context =>
        {
            ProjectTranslationJob saved = await SaveWaitingJobWithFailedSegmentAsync(context, jobStatus, itemStatus);
            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);
            await coordinator.LoadAsync();
            Guid id = context.ChapterIds[0];

            ProjectTranslationAppendResult result = await coordinator.AppendAsync([id], new HashSet<Guid> { id });

            Assert.Equal(0, result.Added);
            Assert.Equal(1, result.Reopened);
            ProjectTranslationItem reopened = Assert.Single(coordinator.CurrentJob!.Items);
            Assert.Equal(ProjectTranslationItemStatus.Pending, reopened.Status);
            Assert.True(reopened.RetranslateAll);
            Assert.Null(reopened.ChapterQueueRunId);
            Assert.Equal(ProjectTranslationItemDisposition.None, reopened.Disposition);
            Assert.Null(reopened.Error);
            Assert.Null(reopened.StartedAt);
            Assert.Null(reopened.FinishedAt);
            Assert.Equal(0, reopened.CompletedSegments);
            Assert.False(reopened.WorkerMetricsRecorded);
            Assert.True(reopened.WorkerMetricsStartedAt > saved.Items[0].WorkerMetricsStartedAt);
            Assert.Empty(reopened.RecordedWorkerAttemptIds!);
            Assert.Empty(reopened.RecordedWorkerSegmentIds!);
            Assert.Equal(0, client.Calls);

            await coordinator.ResumeAsync();

            Assert.Equal(1, client.Calls);
            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
            Assert.Equal(ProjectTranslationItemStatus.Completed, Assert.Single(coordinator.CurrentJob.Items).Status);
            ChapterDocument chapter = (await context.Repository.LoadChapterAsync(context.Session, id)).Chapter;
            Assert.Equal(QueueMode.AllSegments, chapter.Queue.Mode);
            Assert.NotEqual(saved.Items[0].ChapterQueueRunId, chapter.Queue.RunId);
            TranslationSegment segment = Assert.Single(chapter.Segments);
            Assert.Collection(segment.Attempts,
                attempt => Assert.Equal(AttemptOutcome.Failed, attempt.Outcome),
                attempt => Assert.Equal(AttemptOutcome.Completed, attempt.Outcome));
        });
    }

    [Theory]
    [InlineData(ProjectTranslationJobStatus.Paused, ProjectTranslationItemStatus.Pending)]
    [InlineData(ProjectTranslationJobStatus.Interrupted, ProjectTranslationItemStatus.Pending)]
    [InlineData(ProjectTranslationJobStatus.Paused, ProjectTranslationItemStatus.Paused)]
    [InlineData(ProjectTranslationJobStatus.Interrupted, ProjectTranslationItemStatus.Interrupted)]
    public async Task Append_without_retranslation_preserves_started_waiting_item(
        ProjectTranslationJobStatus jobStatus, ProjectTranslationItemStatus itemStatus)
    {
        await WithProjectAsync(1, async context =>
        {
            ProjectTranslationJob saved = await SaveWaitingJobWithFailedSegmentAsync(context, jobStatus, itemStatus);
            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);
            await coordinator.LoadAsync();

            ProjectTranslationAppendResult result = await coordinator.AppendAsync(context.ChapterIds);

            Assert.Equal(0, result.Added);
            Assert.Equal(0, result.Reopened);
            ProjectTranslationItem item = Assert.Single(coordinator.CurrentJob!.Items);
            ProjectTranslationItem before = Assert.Single(saved.Items);
            Assert.Equal(itemStatus, item.Status);
            Assert.Equal(before.ChapterQueueRunId, item.ChapterQueueRunId);
            Assert.False(item.RetranslateAll);
            Assert.Equal(before.Error, item.Error);
            Assert.Equal(before.Disposition, item.Disposition);
            Assert.Equal(before.StartedAt, item.StartedAt);
            Assert.Equal(before.FinishedAt, item.FinishedAt);
            Assert.Equal(before.WorkerMetricsRecorded, item.WorkerMetricsRecorded);
            Assert.Equal(before.WorkerMetricsStartedAt, item.WorkerMetricsStartedAt);
            Assert.Equal(before.RecordedWorkerAttemptIds, item.RecordedWorkerAttemptIds);
            Assert.Equal(before.RecordedWorkerSegmentIds, item.RecordedWorkerSegmentIds);
            Assert.Equal(0, client.Calls);
        });
    }

    [Fact]
    public async Task Clearing_a_paused_job_deletes_it_keeps_translations_and_allows_a_new_plan()
    {
        await WithProjectAsync(3, async context =>
        {
            var client = new ProjectQueueFakeClient();
            var initial = context.Coordinator(client);
            await initial.StartAsync(await initial.PlanAsync([context.ChapterIds[0]]));
            var store = new ProjectTranslationJobStore(context.Session);
            ProjectTranslationJob saved = await store.SaveNewAsync(await context.Coordinator(client).PlanAsync(context.ChapterIds.Take(2)));
            saved.Status = ProjectTranslationJobStatus.Paused;
            await store.SaveAsync(saved);

            var coordinator = context.Coordinator(client);
            await coordinator.LoadAsync();
            await coordinator.ClearAsync();

            Assert.Null(coordinator.CurrentJob);
            Assert.False(File.Exists(store.PathName));
            Assert.Null(await context.Coordinator(client).LoadAsync());
            ChapterDocument translated = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[0])).Chapter;
            Assert.Contains(translated.Segments[0].Attempts, attempt => attempt.Outcome == AttemptOutcome.Completed);
            ProjectTranslationJob plan = await coordinator.PlanAsync([context.ChapterIds[2]]);
            await coordinator.StartAsync(plan);
            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
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
            Assert.Equal(1, coordinator.CurrentJob!.Items.Count(item => item.Status == ProjectTranslationItemStatus.Skipped));
            Assert.All(coordinator.CurrentJob.Items.Where(item => item.Status == ProjectTranslationItemStatus.Skipped),
                item => Assert.Equal(ProjectTranslationItemDisposition.ProtectedContent, item.Disposition));
            ProjectTranslationItem approvedItem = coordinator.CurrentJob.Items.Single(item =>
                item.ChapterId == context.ChapterIds[1]);
            Assert.Equal(ProjectTranslationItemStatus.Completed, approvedItem.Status);
            Assert.Equal(ProjectTranslationItemDisposition.AlreadyTranslated, approvedItem.Disposition);
            ChapterDocument draft = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[0])).Chapter;
            ChapterDocument approved = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[1])).Chapter;
            Assert.Equal("Nháp người dùng.", draft.Segments[0].UserDraft);
            Assert.Equal("Bản đã duyệt.", approved.Segments[0].EditableText);
            Assert.Single(approved.Segments[0].Approvals);
        });
    }

    [Fact]
    public async Task Failed_or_invalid_attempts_are_reported_as_needs_review_instead_of_clean_skip()
    {
        await WithProjectAsync(2, async context =>
        {
            AttemptOutcome[] outcomes = [AttemptOutcome.Failed, AttemptOutcome.InvalidOutput];
            for (int index = 0; index < outcomes.Length; index++)
            {
                ChapterDocument chapter = (await context.Repository.LoadChapterAsync(
                    context.Session, context.ChapterIds[index])).Chapter;
                TranslationSegment segment = Assert.Single(chapter.Segments);
                segment.Status = SegmentStatus.Error;
                segment.Attempts.Add(new TranslationAttempt
                {
                    StartedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
                    FinishedAt = DateTimeOffset.UtcNow,
                    Outcome = outcomes[index],
                    ModelOptions = context.Environment.ModelOptions with { },
                    PromptVersion = "p0-synthetic",
                    Prompt = "synthetic",
                    Error = outcomes[index] == AttemptOutcome.Failed ? "synthetic failure" : null
                });
                await context.Repository.UpdateChapterAsync(context.Session, chapter);
            }
            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);

            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));

            Assert.Equal(0, client.Calls);
            Assert.Equal(ProjectTranslationJobStatus.CompletedWithErrors, coordinator.CurrentJob!.Status);
            Assert.All(coordinator.CurrentJob.Items, item =>
            {
                Assert.Equal(ProjectTranslationItemStatus.NeedsReview, item.Status);
                Assert.Equal(ProjectTranslationItemDisposition.MissingEligibility, item.Disposition);
                Assert.Contains("Còn 1 đoạn", item.Error, StringComparison.Ordinal);
                Assert.Contains("Dịch lại lỗi", item.Error, StringComparison.Ordinal);
                TranslationEligibilityDiagnostic diagnostic = Assert.Single(item.EligibilityDiagnostics,
                    value => value.Stage == TranslationEligibilityDiagnosticStage.BeforeEligibilityDecision);
                Assert.Equal(0, diagnostic.EligibleSegmentCount);
            });
        });
    }

    [Fact]
    public async Task Edit_after_enqueue_preserves_user_draft_and_continues_without_needs_review()
    {
        await WithProjectAsync(1, async context =>
        {
            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds);
            await EditAsync(context, 0, service => service.UpdateDraft(service.GetSnapshot().Segments[0].Id, "Sửa sau enqueue."));

            await coordinator.StartAsync(plan);

            Assert.Equal(0, client.Calls);
            Assert.Equal(ProjectTranslationItemStatus.Skipped, coordinator.CurrentJob!.Items[0].Status);
            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob.Status);
            ChapterDocument saved = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[0])).Chapter;
            Assert.Equal("Sửa sau enqueue.", saved.Segments[0].UserDraft);
        });
    }

    [Fact]
    public async Task Drafted_segment_after_enqueue_is_skipped_while_other_segments_continue()
    {
        await WithProjectAsync(1, async context =>
        {
            var segmentation = new SegmenterOptions
            {
                MinimumHanCharacters = 1, TargetHanCharacters = 5, MaximumHanCharacters = 8
            };
            ProjectResegmentationPreview preview = await context.Repository.PreviewResegmentationAsync(
                context.Session, [context.ChapterIds[0]], segmentation);
            await context.Repository.ResegmentChaptersAsync(context.Session, preview, confirmed: true);
            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds);
            ChapterDocument before = (await context.Repository.LoadChapterAsync(context.Session,
                context.ChapterIds[0])).Chapter;
            Assert.True(before.Segments.Count > 1);
            await EditAsync(context, 0, service =>
                service.UpdateDraft(service.GetSnapshot().Segments[0].Id, "Đoạn người dùng sửa."));

            await coordinator.StartAsync(plan);

            ChapterDocument saved = (await context.Repository.LoadChapterAsync(context.Session,
                context.ChapterIds[0])).Chapter;
            Assert.Equal("Đoạn người dùng sửa.", saved.Segments[0].UserDraft);
            Assert.Equal(before.Segments.Count - 1, client.Calls);
            Assert.Equal(ProjectTranslationItemStatus.Completed, coordinator.CurrentJob!.Items[0].Status);
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
    public async Task Character_rules_are_used_by_queue_without_extra_requests()
    {
        await WithProjectAsync(1, async context =>
        {
            ProjectTranslationEnvironment current = context.Environment with
            {
                Resources = context.Environment.Resources.Clone()
            };
            current.Resources.Characters.ConsistencyLockEnabled = true;
            current.Resources.Characters.Profiles.Add(new CharacterProfile
            {
                ChineseName = "天地", VietnameseName = "Thiên Địa", PrimaryNarrativePronoun = "hắn",
                SecondaryNarrativePronoun = "hắn ta", NarrativePronounLocked = true,
                DisallowedNarrativePronouns = ["anh ấy"]
            });
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(current);
            var client = new ProjectQueueFakeClient();
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, client, Load);

            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));

            Assert.Equal(1, client.Calls);
            Assert.Single(client.Prompts);
            Assert.Contains("CHARACTER CONSISTENCY", client.Prompts[0]);
            Assert.Contains("天地 = Thiên Địa", client.Prompts[0]);
        });
    }

    [Fact]
    public async Task Changed_character_resource_revision_is_applied_without_replanning()
    {
        await WithProjectAsync(1, async context =>
        {
            ProjectTranslationEnvironment current = context.Environment;
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(current);
            var client = new ProjectQueueFakeClient();
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, client, Load);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds);
            ProjectResources changed = current.Resources.Clone();
            changed.Revision++;
            changed.Characters.ConsistencyLockEnabled = true;
            current = current with { Resources = changed };

            await coordinator.StartAsync(plan);

            Assert.Equal(1, client.Calls);
            Assert.Equal(changed.Revision, coordinator.CurrentJob!.ProjectResourceRevision);
            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob.Status);
        });
    }

    [Fact]
    public async Task Resume_refreshes_changed_character_rules_without_manual_replan()
    {
        await WithProjectAsync(1, async context =>
        {
            ProjectTranslationEnvironment current = context.Environment;
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(current);
            var store = new ProjectTranslationJobStore(context.Session);
            var planner = new ProjectTranslationCoordinator(context.Session, context.Repository,
                new ProjectQueueFakeClient(), Load, store);
            ProjectTranslationJob saved = await store.SaveNewAsync(await planner.PlanAsync(context.ChapterIds));
            saved.Status = ProjectTranslationJobStatus.Interrupted;
            await store.SaveAsync(saved);
            ProjectResources changed = current.Resources.Clone();
            changed.Revision++;
            changed.Characters.ConsistencyLockEnabled = true;
            current = current with { Resources = changed };
            var client = new ProjectQueueFakeClient();
            var reopened = new ProjectTranslationCoordinator(context.Session, context.Repository, client, Load,
                new ProjectTranslationJobStore(context.Session));
            await reopened.LoadAsync();

            await reopened.ResumeAsync();

            Assert.Equal(1, client.Calls);
            Assert.Equal(changed.Revision, reopened.CurrentJob!.ProjectResourceRevision);
            Assert.Equal(ProjectTranslationJobStatus.Completed, reopened.CurrentJob.Status);
        });
    }

    [Fact]
    public async Task Source_change_after_enqueue_still_requires_review_and_sends_no_request()
    {
        await WithProjectAsync(1, async context =>
        {
            var client = new ProjectQueueFakeClient();
            var coordinator = context.Coordinator(client);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds);
            ChapterDocument changed = (await context.Repository.LoadChapterAsync(context.Session,
                context.ChapterIds[0])).Chapter;
            var editor = new TranslationService(changed, new ProjectQueueFakeClient(),
                new RegisteredChapterStore(context.Session, context.Repository));
            SourceEditPreview preview = editor.PreviewSourceEdit(changed.OriginalText + "新增原文。");
            await editor.ApplySourceEditAsync(preview, confirmed: true);

            await coordinator.StartAsync(plan);

            Assert.Equal(0, client.Calls);
            Assert.Equal(ProjectTranslationItemStatus.NeedsReview, coordinator.CurrentJob!.Items[0].Status);
            Assert.Contains("Nguyên văn", coordinator.CurrentJob.Items[0].Error);
        });
    }

    [Fact]
    public async Task Stop_cancels_hanging_request_quickly_and_keeps_job_resumable()
    {
        await WithProjectAsync(3, async context =>
        {
            var client = new ProjectQueueFakeClient { BlockFirst = true };
            var coordinator = context.Coordinator(client);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            // The first request never completes on its own, so finishing at all proves Stop cancelled it.
            // The generous bound only turns a hang into a failure; it is not a speed assertion.
            await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await run.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(ProjectTranslationJobStatus.Interrupted, coordinator.CurrentJob!.Status);
            Assert.DoesNotContain(coordinator.CurrentJob.Items,
                item => item.Status == ProjectTranslationItemStatus.Cancelled);
            Assert.Equal(0, coordinator.CurrentJob.Items.Count(item =>
                item.Status == ProjectTranslationItemStatus.Completed));
        });
    }

    [Fact]
    public async Task Draft_plan_adopts_current_confirmed_backend_before_first_request()
    {
        await WithProjectAsync(1, async context =>
        {
            GpuBackendSnapshot local = GpuBackendSnapshot.LocalDefault(context.Environment.ModelOptions.Model);
            GpuBackendSnapshot current = local;
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(
                context.Environment with { Backend = current });
            var client = new ProjectQueueFakeClient();
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, client, Load);
            ProjectTranslationJob plan = await coordinator.PlanAsync(context.ChapterIds);
            Assert.Equal(local, plan.Backend);

            current = new(Guid.NewGuid(), GpuBackendKind.Remote, "GPU thuê", "https://gpu.example.com/api/chat",
                context.Environment.ModelOptions.Model);
            await coordinator.StartAsync(plan);

            Assert.Equal(1, client.Calls);
            Assert.Equal(current, coordinator.CurrentJob!.Backend);
            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob.Status);
        });
    }

    [Fact]
    public async Task Reopened_interrupted_job_keeps_backend_snapshot_and_blocks_resume_on_another_server()
    {
        await WithProjectAsync(1, async context =>
        {
            GpuBackendSnapshot local = GpuBackendSnapshot.LocalDefault(context.Environment.ModelOptions.Model);
            GpuBackendSnapshot current = local;
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(
                context.Environment with { Backend = current });
            var store = new ProjectTranslationJobStore(context.Session);
            var first = new ProjectTranslationCoordinator(context.Session, context.Repository,
                new ProjectQueueFakeClient(), Load, store);
            ProjectTranslationJob saved = await store.SaveNewAsync(await first.PlanAsync(context.ChapterIds));
            saved.Status = ProjectTranslationJobStatus.Interrupted;
            saved = await store.SaveAsync(saved);

            current = new(Guid.NewGuid(), GpuBackendKind.Remote, "GPU sau restart",
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
    public async Task Job_without_backend_snapshot_is_rejected_by_current_schema()
    {
        await WithProjectAsync(1, async context =>
        {
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(context.Environment);
            var store = new ProjectTranslationJobStore(context.Session);
            var first = new ProjectTranslationCoordinator(context.Session, context.Repository,
                new ProjectQueueFakeClient(), Load, store);
            ProjectTranslationJob invalid = await first.PlanAsync(context.ChapterIds);
            invalid.Backend = null;
            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveNewAsync(invalid));
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
            Assert.All(coordinator.CurrentJob.Items, item => Assert.True(item.WorkerMetricsRecorded));
            Assert.Equal(8 * 200, coordinator.GetWorkerProgress().Sum(worker => worker.InputTokens));
            Assert.Equal(8 * 80, coordinator.GetWorkerProgress().Sum(worker => worker.OutputTokens));
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
    public async Task Parallel_workers_finish_current_snapshots_then_use_new_glossary_without_double_claim()
    {
        await WithProjectAsync(8, async context =>
        {
            ProjectTranslationEnvironment current = context.Environment;
            Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(current with
                { Resources = current.Resources.Clone() });
            var concurrency = new ParallelConcurrency();
            var local = new ParallelFakeClient("local", concurrency, 0) { BlockFirst = true };
            var remote = new ParallelFakeClient("remote", concurrency, 0) { BlockFirst = true };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository,
                Workers(context, local, remote), Load);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await Task.WhenAll(local.FirstStarted.Task, remote.FirstStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            ProjectResources changed = current.Resources.Clone();
            changed.Revision++;
            changed.Terms.Add(new("天地", "Thiên Địa song song"));
            current = current with { Resources = changed };

            local.Release.TrySetResult();
            remote.Release.TrySetResult();
            await run;

            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob!.Status);
            Assert.Equal(8, local.Calls + remote.Calls);
            Assert.Equal(8, coordinator.CurrentJob.Items.Select(item => item.ChapterId).Distinct().Count());
            Assert.Equal(8, coordinator.CurrentJob.Items.Count(item =>
                item.Status == ProjectTranslationItemStatus.Completed));
            Assert.DoesNotContain(local.Prompts.Take(1).Concat(remote.Prompts.Take(1)),
                prompt => prompt.Contains("Thiên Địa song song", StringComparison.Ordinal));
            Assert.Contains(local.Prompts.Skip(1).Concat(remote.Prompts.Skip(1)),
                prompt => prompt.Contains("Thiên Địa song song", StringComparison.Ordinal));
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
    public async Task Paused_parallel_job_reopens_with_same_workers_segment_progress_and_models_before_resume()
    {
        await WithProjectAsync(6, async context =>
        {
            var local = new ParallelFakeClient("local", new(), 0) { BlockFirst = true };
            var remote = new ParallelFakeClient("remote", new(), 0) { BlockFirst = true };
            ProjectTranslationWorkerDefinition[] original = Workers(context, local, remote);
            original[0] = original[0] with { BackendIdentity = "local:verified" };
            ModelOptions remoteOptions = original[1].ModelOptions with { Model = "index-translate:9b" };
            original[1] = original[1] with
            {
                Backend = original[1].Backend with { Model = remoteOptions.Model },
                ModelOptions = remoteOptions,
                BackendIdentity = "remote:ssh-verified"
            };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, original,
                context.LoadEnvironment);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await Task.WhenAll(local.FirstStarted.Task, remote.FirstStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

            Task pause = coordinator.PauseAsync();
            local.Release.TrySetResult();
            remote.Release.TrySetResult();
            await pause;
            await run;
            ProjectTranslationJob checkpoint = coordinator.CurrentJob!;
            int completedSegments = checkpoint.Items.Sum(item => item.CompletedSegments);
            Guid[] workerIds = checkpoint.Workers.Select(worker => worker.WorkerId).ToArray();
            string[] models = checkpoint.Workers.Select(worker => worker.ModelOptions.Model).ToArray();

            var reopenedLocal = new ParallelFakeClient("local reopened", new(), 0);
            var reopenedRemote = new ParallelFakeClient("remote reopened", new(), 0);
            ProjectTranslationWorkerDefinition[] reopenedDefinitions =
            [original[0] with { Client = reopenedLocal }, original[1] with { Client = reopenedRemote }];
            var reopened = new ProjectTranslationCoordinator(context.Session, context.Repository,
                reopenedDefinitions, context.LoadEnvironment, new ProjectTranslationJobStore(context.Session));
            ProjectTranslationJob restored = Assert.IsType<ProjectTranslationJob>(await reopened.LoadAsync());

            Assert.Equal(ProjectTranslationJobStatus.Paused, restored.Status);
            Assert.Equal(workerIds, restored.Workers.Select(worker => worker.WorkerId));
            Assert.Equal(models, restored.Workers.Select(worker => worker.ModelOptions.Model));
            Assert.Equal(completedSegments, restored.Items.Sum(item => item.CompletedSegments));

            await reopened.ResumeAsync();

            Assert.Equal(ProjectTranslationJobStatus.Completed, reopened.CurrentJob!.Status);
            Assert.Equal(6, reopened.CurrentJob.Items.Sum(item => item.CompletedSegments));
            Assert.Equal(workerIds, reopened.CurrentJob.Workers.Select(worker => worker.WorkerId));
            Assert.Equal(models, reopened.CurrentJob.Workers.Select(worker => worker.ModelOptions.Model));
        });
    }

    [Fact]
    public async Task Paused_parallel_job_switches_to_one_confirmed_worker_without_creating_new_job()
    {
        await WithProjectAsync(4, async context =>
        {
            var originalLocal = new ParallelFakeClient("old-local", new(), 0);
            var originalRemote = new ParallelFakeClient("old-remote", new(), 0);
            ProjectTranslationWorkerDefinition[] original = Workers(context, originalLocal, originalRemote);
            var store = new ProjectTranslationJobStore(context.Session);
            var planner = new ProjectTranslationCoordinator(context.Session, context.Repository, original,
                context.LoadEnvironment, store);
            ProjectTranslationJob saved = await store.SaveNewAsync(await planner.PlanAsync(context.ChapterIds));
            Guid originalJobId = saved.JobId;
            saved.Status = ProjectTranslationJobStatus.Paused;
            saved.Workers[0].CompletedHanCharacters = 123;
            saved.Workers[1].CompletedHanCharacters = 456;
            await store.SaveAsync(saved);

            var currentClient = new ParallelFakeClient("new-local", new(), 0);
            ModelOptions changedOptions = context.Environment.ModelOptions with { Temperature = 0.42 };
            ProjectTranslationWorkerDefinition current = original[0] with
            {
                Client = currentClient,
                ModelOptions = changedOptions,
                Backend = original[0].Backend with { Model = changedOptions.Model }
            };
            var resumed = new ProjectTranslationCoordinator(context.Session, context.Repository, [current],
                context.LoadEnvironment, new ProjectTranslationJobStore(context.Session));
            await resumed.LoadAsync();

            await resumed.ResumeWithCurrentConfigurationAsync();

            Assert.Equal(originalJobId, resumed.CurrentJob!.JobId);
            Assert.Equal(ProjectTranslationJobStatus.Completed, resumed.CurrentJob.Status);
            Assert.Single(resumed.CurrentJob.Workers);
            Assert.Equal(changedOptions, resumed.CurrentJob.Workers[0].ModelOptions);
            Assert.True(resumed.CurrentJob.Workers[0].CompletedHanCharacters >= 579);
            Assert.Equal(4, currentClient.Calls);
            Assert.Equal(4, resumed.CurrentJob.Items.Select(item => item.ChapterId).Distinct().Count());
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
                result.Workers.Single(worker => worker.Backend.Kind == GpuBackendKind.Remote).Status);
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
            Guid remoteId = coordinator.CurrentJob!.Workers.Single(worker => worker.Backend.Kind == GpuBackendKind.Remote).WorkerId;

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

    [Fact]
    public async Task Worker_with_three_slots_limits_overlap_and_translates_each_chapter_once()
    {
        await WithProjectAsync(5, async context =>
        {
            var client = new SlotFakeClient(3) { Block = true };
            ProjectTranslationWorkerDefinition worker = SingleWorker(context, client, 3);
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, [worker],
                context.LoadEnvironment);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.TargetConcurrencyReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(3, coordinator.CurrentJob!.Workers.Single().RunningChapterIds.Count);
            client.Release.TrySetResult();
            await run;

            Assert.InRange(client.MaximumConcurrency, 2, 3);
            Assert.Equal(5, client.Calls);
            Assert.Equal(5, client.Prompts.Count);
            Assert.Equal(5, coordinator.CurrentJob.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed));
            Assert.Equal(3, coordinator.CurrentJob.Workers.Single().Slots);
            Assert.Empty(coordinator.CurrentJob.Workers.Single().RunningChapterIds);
            foreach (Guid id in context.ChapterIds)
            {
                ChapterDocument chapter = (await context.Repository.LoadChapterAsync(context.Session, id)).Chapter;
                Assert.Single(chapter.Segments[0].Attempts);
            }
        });
    }

    [Fact]
    public async Task Pause_stops_all_three_slots_and_resume_finishes_remaining_chapters()
    {
        await WithProjectAsync(6, async context =>
        {
            var client = new SlotFakeClient(3) { Block = true };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository,
                [SingleWorker(context, client, 3)], context.LoadEnvironment);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.TargetConcurrencyReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task pause = coordinator.PauseAsync();
            client.Release.TrySetResult();
            await Task.WhenAll(pause, run);

            Assert.Equal(ProjectTranslationJobStatus.Paused, coordinator.CurrentJob!.Status);
            Assert.Equal(ProjectTranslationWorkerStatus.Paused, coordinator.CurrentJob.Workers.Single().Status);
            Assert.Empty(coordinator.CurrentJob.Workers.Single().RunningChapterIds);
            client.Block = false;
            await coordinator.ResumeAsync();

            Assert.Equal(ProjectTranslationJobStatus.Completed, coordinator.CurrentJob.Status);
            Assert.Equal(6, coordinator.CurrentJob.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed));
            Assert.Equal(6, client.Calls);
        });
    }

    [Fact]
    public async Task Cancel_stops_all_three_slots_and_marks_pending_chapters_cancelled()
    {
        await WithProjectAsync(6, async context =>
        {
            var client = new SlotFakeClient(3) { Block = true };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository,
                [SingleWorker(context, client, 3)], context.LoadEnvironment);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.TargetConcurrencyReached.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Task cancel = coordinator.CancelPendingAsync();
            client.Release.TrySetResult();
            await Task.WhenAll(cancel, run);

            Assert.Equal(ProjectTranslationJobStatus.Cancelled, coordinator.CurrentJob!.Status);
            Assert.Equal(ProjectTranslationWorkerStatus.Cancelled, coordinator.CurrentJob.Workers.Single().Status);
            Assert.Empty(coordinator.CurrentJob.Workers.Single().RunningChapterIds);
            Assert.Equal(3, client.Calls);
            Assert.Equal(3, coordinator.CurrentJob.Items.Count(item => item.Status == ProjectTranslationItemStatus.Cancelled));
        });
    }

    [Fact]
    public async Task Failure_in_one_slot_does_not_stop_other_slots_and_is_counted_once()
    {
        await WithProjectAsync(5, async context =>
        {
            var client = new SlotFakeClient(2);
            client.FailCalls.Add(1);
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository,
                [SingleWorker(context, client, 3)], context.LoadEnvironment);

            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));

            Assert.Equal(5, client.Calls);
            Assert.Equal(4, coordinator.CurrentJob!.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed));
            Assert.Single(coordinator.CurrentJob.Items, item => item.Status == ProjectTranslationItemStatus.Failed);
            Assert.Equal(1, coordinator.CurrentJob.Workers.Single().FailedChapters);
        });
    }

    [Fact]
    public async Task Old_worker_json_without_slots_or_running_chapters_loads_with_defaults()
    {
        await WithProjectAsync(1, async context =>
        {
            var client = new SlotFakeClient(1);
            ProjectTranslationWorkerDefinition worker = SingleWorker(context, client, 1);
            var store = new ProjectTranslationJobStore(context.Session);
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, [worker],
                context.LoadEnvironment, store);
            await store.SaveNewAsync(await coordinator.PlanAsync(context.ChapterIds));
            System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(
                await File.ReadAllTextAsync(store.PathName))!;
            foreach (System.Text.Json.Nodes.JsonNode? workerNode in root["workers"]!.AsArray())
            {
                workerNode!.AsObject().Remove("runningChapterIds");
                workerNode.AsObject().Remove("slots");
            }
            foreach (System.Text.Json.Nodes.JsonNode? itemNode in root["items"]!.AsArray())
            {
                itemNode!.AsObject().Remove("workerMetricsStartedAt");
                itemNode.AsObject().Remove("completedHanCharacters");
                itemNode.AsObject().Remove("recordedWorkerAttemptIds");
                itemNode.AsObject().Remove("recordedWorkerSegmentIds");
            }
            await File.WriteAllTextAsync(store.PathName, root.ToJsonString(new() { WriteIndented = true }));

            ProjectTranslationJob loaded = Assert.IsType<ProjectTranslationJob>(await store.LoadAsync());

            Assert.Equal(1, loaded.Workers.Single().Slots);
            Assert.Empty(loaded.Workers.Single().RunningChapterIds);
            Assert.Empty(loaded.Items.Single().RecordedWorkerAttemptIds ?? []);
            Assert.Empty(loaded.Items.Single().RecordedWorkerSegmentIds ?? []);
        });
    }

    [Fact]
    public async Task Two_slots_report_total_gpu_throughput_and_single_slot_generation_speed()
    {
        await WithProjectAsync(2, async context =>
        {
            var client = new MetricsSlotFakeClient(TimeSpan.FromSeconds(1), outputTokens: 100,
                evaluationSeconds: 1);
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository,
                [SingleWorker(context, client, 2)], context.LoadEnvironment);

            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));

            ProjectTranslationWorkerProgress worker = Assert.Single(coordinator.GetWorkerProgress());
            Assert.Equal(200, worker.OutputTokens);
            Assert.Equal(100, worker.PerSlotTokensPerSecond, 6);
            Assert.InRange(worker.TotalTokensPerSecond, 150, 230);
            ProjectTranslationAggregateMetrics aggregate = coordinator.GetViewSnapshot()!.Aggregate;
            Assert.Equal(worker.TotalTokensPerSecond, aggregate.TotalTokensPerSecond, 6);
            Assert.Equal(100, aggregate.PerSlotTokensPerSecond, 6);
            Assert.Equal(2, aggregate.Slots);
        });
    }

    [Fact]
    public async Task Han_tokens_and_busy_time_move_after_first_segment_before_chapter_finishes()
    {
        await WithProjectAsync(1, async context =>
        {
            var segmentation = new SegmenterOptions
            {
                MinimumHanCharacters = 1, TargetHanCharacters = 3, MaximumHanCharacters = 4
            };
            ProjectResegmentationPreview preview = await context.Repository.PreviewResegmentationAsync(
                context.Session, context.ChapterIds, segmentation);
            await context.Repository.ResegmentChaptersAsync(context.Session, preview, confirmed: true);
            var client = new MetricsSlotFakeClient(TimeSpan.FromMilliseconds(80), 50, 0.08, blockCall: 2);
            ProjectTranslationWorkerDefinition worker = SingleWorker(context, client, 1) with
                { SegmentationOptions = segmentation };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository,
                [worker], context.LoadEnvironment);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            ProjectTranslationWorkerState workerState = coordinator.CurrentJob!.Workers.Single();
            Assert.Equal(50, workerState.OutputTokens);
            Assert.True(workerState.CompletedHanCharacters > 0);
            Assert.True(workerState.ActiveSeconds > 0);
            Assert.True(coordinator.GetWorkerProgress().Single().HanCharactersPerMinute > 0);
            Assert.Equal(ProjectTranslationItemStatus.Running, coordinator.CurrentJob.Items.Single().Status);

            client.ReleaseBlocked.TrySetResult();
            await run;
        });
    }

    [Fact]
    public async Task Segment_metrics_are_not_double_counted_across_pause_resume_and_retry()
    {
        await WithProjectAsync(1, async context =>
        {
            var segmentation = new SegmenterOptions
            {
                MinimumHanCharacters = 1, TargetHanCharacters = 3, MaximumHanCharacters = 4
            };
            ProjectResegmentationPreview preview = await context.Repository.PreviewResegmentationAsync(
                context.Session, context.ChapterIds, segmentation);
            await context.Repository.ResegmentChaptersAsync(context.Session, preview, confirmed: true);
            var client = new MetricsSlotFakeClient(TimeSpan.FromMilliseconds(40), 25, 0.04, blockCall: 2);
            ProjectTranslationWorkerDefinition worker = SingleWorker(context, client, 1) with
                { SegmentationOptions = segmentation };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository,
                [worker], context.LoadEnvironment);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));
            await client.SecondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task pause = coordinator.PauseAsync();
            client.ReleaseBlocked.TrySetResult();
            await Task.WhenAll(run, pause);
            long tokensAtPause = coordinator.CurrentJob!.Workers.Single().OutputTokens;
            Assert.Equal(client.Calls * 25, tokensAtPause);

            await coordinator.ResumeAsync();
            Assert.Equal(client.Calls * 25, coordinator.CurrentJob.Workers.Single().OutputTokens);

            await WithProjectAsync(1, async retryContext =>
            {
                var retrySegmentation = new SegmenterOptions
                {
                    MinimumHanCharacters = 1, TargetHanCharacters = 3, MaximumHanCharacters = 4
                };
                ProjectResegmentationPreview retryPreview = await retryContext.Repository.PreviewResegmentationAsync(
                    retryContext.Session, retryContext.ChapterIds, retrySegmentation);
                await retryContext.Repository.ResegmentChaptersAsync(retryContext.Session, retryPreview,
                    confirmed: true);
                var retryClient = new MetricsSlotFakeClient(TimeSpan.FromMilliseconds(20), 30, 0.02);
                retryClient.FailCalls.Add(2);
                ProjectTranslationWorkerDefinition retryWorker = SingleWorker(retryContext, retryClient, 1) with
                    { SegmentationOptions = retrySegmentation };
                var retryCoordinator = new ProjectTranslationCoordinator(retryContext.Session, retryContext.Repository,
                    [retryWorker], retryContext.LoadEnvironment);
                await retryCoordinator.StartAsync(await retryCoordinator.PlanAsync(retryContext.ChapterIds));
                Assert.Equal((retryClient.Calls - 1) * 30,
                    retryCoordinator.CurrentJob!.Workers.Single().OutputTokens);
                retryClient.FailCalls.Clear();
                await retryCoordinator.RetryFailedAsync();
                Assert.Equal((retryClient.Calls - 1) * 30,
                    retryCoordinator.CurrentJob.Workers.Single().OutputTokens);
            });
        });
    }

    [Fact]
    public async Task Two_workers_combine_two_and_one_slots()
    {
        await WithProjectAsync(7, async context =>
        {
            var concurrency = new ParallelConcurrency();
            var local = new ParallelFakeClient("local", concurrency, 40);
            var remote = new ParallelFakeClient("remote", concurrency, 40);
            ProjectTranslationWorkerDefinition[] workers = Workers(context, local, remote);
            workers[0] = workers[0] with { Slots = 2 };
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository, workers,
                context.LoadEnvironment);

            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));

            Assert.Equal(3, concurrency.Maximum);
            Assert.Equal(7, local.Calls + remote.Calls);
            Assert.Equal([2, 1], coordinator.CurrentJob!.Workers.Select(worker => worker.Slots).ToArray());
            Assert.Equal(7, coordinator.CurrentJob.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed));
        });
    }

    [Fact]
    public async Task Resume_allows_slots_to_change_without_changing_backend_identity()
    {
        await WithProjectAsync(5, async context =>
        {
            var initialClient = new SlotFakeClient(1);
            ProjectTranslationWorkerDefinition initial = SingleWorker(context, initialClient, 1);
            var store = new ProjectTranslationJobStore(context.Session);
            var planner = new ProjectTranslationCoordinator(context.Session, context.Repository, [initial],
                context.LoadEnvironment, store);
            ProjectTranslationJob saved = await store.SaveNewAsync(await planner.PlanAsync(context.ChapterIds));
            saved.Status = ProjectTranslationJobStatus.Paused;
            await store.SaveAsync(saved);
            var resumedClient = new SlotFakeClient(3) { Block = true };
            ProjectTranslationWorkerDefinition changed = initial with { Client = resumedClient, Slots = 3 };
            var resumed = new ProjectTranslationCoordinator(context.Session, context.Repository, [changed],
                context.LoadEnvironment, new(context.Session));
            await resumed.LoadAsync();

            Task run = resumed.ResumeAsync();
            await resumedClient.TargetConcurrencyReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            resumedClient.Release.TrySetResult();
            await run;

            Assert.Equal(ProjectTranslationJobStatus.Completed, resumed.CurrentJob!.Status);
            Assert.Equal(3, resumed.CurrentJob.Workers.Single().Slots);
            Assert.Equal(3, resumedClient.MaximumConcurrency);
        });
    }

    [Fact]
    public async Task Busy_time_is_banked_per_finished_chapter_while_other_slots_keep_running()
    {
        await WithProjectAsync(5, async context =>
        {
            var client = new ParallelFakeClient("remote", new ParallelConcurrency(), 60);
            var coordinator = new ProjectTranslationCoordinator(context.Session, context.Repository,
                [SingleWorker(context, client, 2)], context.LoadEnvironment);
            bool bankedWhileBusy = false;
            coordinator.ProgressChanged += (_, _) =>
            {
                ProjectTranslationWorkerState? worker = coordinator.CurrentJob?.Workers.Single();
                if (worker is { CompletedChapters: > 0, RunningChapterIds.Count: > 0, ActiveSeconds: > 0 }) bankedWhileBusy = true;
            };

            await coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds));

            // Speed, ETA and cost read ActiveSeconds: it must grow while every slot stays busy, not only at the end.
            Assert.True(bankedWhileBusy);
            Assert.InRange(coordinator.CurrentJob!.Workers.Single().ActiveSeconds, 0.1, 2.5);
        });
    }

    private static ProjectTranslationWorkerDefinition SingleWorker(TestProject context, ILlmClient client, int slots)
    {
        ModelOptions options = context.Environment.ModelOptions;
        return new ProjectTranslationWorkerDefinition(Guid.NewGuid(), GpuBackendSnapshot.LocalDefault(options.Model),
            client, options with { }) with { Slots = slots };
    }

    private static ProjectTranslationWorkerDefinition[] Workers(TestProject context, ILlmClient local, ILlmClient remote)
    {
        ModelOptions options = context.Environment.ModelOptions;
        return
        [
            new(Guid.NewGuid(), GpuBackendSnapshot.LocalDefault(options.Model), local, options with { }),
            new(Guid.NewGuid(), new GpuBackendSnapshot(Guid.NewGuid(), GpuBackendKind.Remote,
                "EZYCloudX RTX 3090 Ti", "http://127.0.0.1:43117/api/chat", options.Model), remote,
                options with { }, 0.45m)
        ];
    }

    private static async Task<ProjectTranslationJob> SaveWaitingJobWithFailedSegmentAsync(TestProject context,
        ProjectTranslationJobStatus jobStatus, ProjectTranslationItemStatus itemStatus)
    {
        ChapterDocument chapter = (await context.Repository.LoadChapterAsync(context.Session, context.ChapterIds[0])).Chapter;
        TranslationSegment segment = Assert.Single(chapter.Segments);
        segment.Status = SegmentStatus.Error;
        segment.Attempts.Add(new TranslationAttempt
        {
            StartedAt = DateTimeOffset.UtcNow.AddSeconds(-2), FinishedAt = DateTimeOffset.UtcNow.AddSeconds(-1),
            Outcome = AttemptOutcome.Failed, ModelOptions = context.Environment.ModelOptions with { },
            PromptVersion = "p0-synthetic", Prompt = "synthetic", Error = "synthetic failure"
        });
        chapter.Queue = new QueueCheckpoint
        {
            RunId = Guid.NewGuid(), Status = QueueStatus.Interrupted, Mode = QueueMode.MissingSegments,
            CandidateIds = [segment.Id], NextIndex = 1, ModelOptions = context.Environment.ModelOptions with { },
            Preset = context.Environment.PromptPreset with { }
        };
        await context.Repository.UpdateChapterAsync(context.Session, chapter);
        var store = new ProjectTranslationJobStore(context.Session);
        ProjectTranslationJob saved = await store.SaveNewAsync(
            await context.Coordinator(new ProjectQueueFakeClient()).PlanAsync(context.ChapterIds));
        saved.Status = jobStatus;
        ProjectTranslationItem item = Assert.Single(saved.Items);
        item.Status = itemStatus;
        item.ChapterQueueRunId = chapter.Queue.RunId;
        item.Error = "synthetic failure";
        item.Disposition = ProjectTranslationItemDisposition.MissingEligibility;
        item.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        item.FinishedAt = DateTimeOffset.UtcNow.AddSeconds(-1);
        item.WorkerMetricsRecorded = true;
        item.WorkerMetricsStartedAt = item.StartedAt;
        item.RecordedWorkerAttemptIds = [segment.Attempts[0].Id];
        item.RecordedWorkerSegmentIds = [segment.Id];
        return await store.SaveAsync(saved);
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

    private static async Task WithProjectAsync(int chapterCount, Func<TestProject, Task> test, string? sourceText = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "QTStudio-M4-Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new ProjectRepository();
            ProjectSession session = await repository.CreateAsync(root, "M4 synthetic");
            var ids = new List<Guid>(chapterCount);
            for (int number = 1; number <= chapterCount; number++)
            {
                ChapterDocument chapter = TranslationService.CreateChapter($"Chương {number}", sourceText ?? $"天地玄黄宇宙洪荒第{number}章。",
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
        public ProjectTranslationCoordinator Coordinator(ILlmClient client) =>
            new(Session, Repository, client, LoadEnvironment);
    }

    private sealed class ProjectQueueFakeClient : ILlmClient
    {
        private int _calls;
        private int _concurrency;
        private int _maximumConcurrency;
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public bool BlockFirst { get; init; }
        public HashSet<int> FailCalls { get; } = [];
        public List<string> Prompts { get; } = [];
        public Action<int>? AfterCompletion { get; set; }
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            lock (Prompts) Prompts.Add(prompt);
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
                if (FailCalls.Contains(call)) throw new LlmRequestException(LlmErrorKind.Http, "Synthetic Ollama failure.");
                LlmCompletion completion = FakeClient.Completion($"Bản dịch tuần tự {call}.");
                AfterCompletion?.Invoke(call);
                return completion;
            }
            finally { Interlocked.Decrement(ref _concurrency); }
        }
    }

    private sealed class SlotFakeClient(int targetConcurrency) : ILlmClient
    {
        private int _calls;
        private int _concurrency;
        private int _maximumConcurrency;
        public int Calls => Volatile.Read(ref _calls);
        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);
        public bool Block { get; set; }
        public HashSet<int> FailCalls { get; } = [];
        public List<string> Prompts { get; } = [];
        public TaskCompletionSource TargetConcurrencyReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            lock (Prompts) Prompts.Add(prompt);
            int active = Interlocked.Increment(ref _concurrency);
            int observed;
            do { observed = _maximumConcurrency; }
            while (active > observed && Interlocked.CompareExchange(ref _maximumConcurrency, active, observed) != observed);
            if (active >= targetConcurrency) TargetConcurrencyReached.TrySetResult();
            try
            {
                if (Block) await Release.Task.WaitAsync(cancellationToken);
                lock (FailCalls)
                {
                    if (FailCalls.Contains(call))
                        throw new LlmRequestException(LlmErrorKind.Http, "Synthetic slot failure.");
                }
                return FakeClient.Completion($"slot translated {call}");
            }
            finally { Interlocked.Decrement(ref _concurrency); }
        }
    }

    private sealed class MetricsSlotFakeClient(TimeSpan delay, int outputTokens, double evaluationSeconds,
        int blockCall = 0) : ILlmClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public HashSet<int> FailCalls { get; } = [];
        public TaskCompletionSource SecondStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlocked { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            if (call == 2) SecondStarted.TrySetResult();
            if (call == blockCall) await ReleaseBlocked.Task.WaitAsync(cancellationToken);
            await Task.Delay(delay, cancellationToken);
            if (FailCalls.Contains(call))
                throw new LlmRequestException(LlmErrorKind.Http, "Synthetic metrics failure.");
            return new LlmCompletion($"metrics translated {call}", true, "stop", new GenerationMetrics
            {
                InputTokens = outputTokens * 2,
                OutputTokens = outputTokens,
                EvaluationDurationNanoseconds = (long)(evaluationSeconds * 1_000_000_000d),
                ClientElapsed = delay
            });
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

    private sealed class ParallelFakeClient(string name, ParallelConcurrency concurrency, int delayMilliseconds) : ILlmClient
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public bool BlockFirst { get; init; }
        public bool FailWithConnection { get; init; }
        public List<string> Prompts { get; } = [];
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            int call = Interlocked.Increment(ref _calls);
            lock (Prompts) Prompts.Add(prompt);
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
                    throw new LlmRequestException(LlmErrorKind.Connection, "synthetic remote disconnect");
                return FakeClient.Completion($"{name} translated {call}");
            }
            finally { concurrency.Exit(); }
        }
    }
}
