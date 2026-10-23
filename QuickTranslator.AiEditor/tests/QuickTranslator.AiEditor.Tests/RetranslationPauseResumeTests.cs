using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

public sealed partial class ProjectTranslationCoordinatorTests
{
    // Three paragraphs of ~150 Han characters: three segments per chapter, so a pause can land mid-chapter.
    private static readonly string ThreeSegments = string.Join("\n\n",
        Enumerable.Range(0, 3).Select(_ => string.Concat(Enumerable.Repeat("天地玄黄宇宙洪荒日月盈昃辰宿列张。", 9))));

    [Fact]
    public async Task Pausing_a_full_retranslation_keeps_the_chapter_open_and_resume_redoes_every_segment()
    {
        await WithProjectAsync(1, async context =>
        {
            var initial = context.Coordinator(new ProjectQueueFakeClient());
            await initial.StartAsync(await initial.PlanAsync(context.ChapterIds));
            Guid id = context.ChapterIds[0];
            int segments = (await context.Repository.LoadChapterAsync(context.Session, id)).Chapter.Segments.Count;
            Assert.True(segments >= 3);

            var client = new ProjectQueueFakeClient { BlockFirst = true };
            var coordinator = context.Coordinator(client);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds, retranslateAll: new HashSet<Guid> { id }));
            await client.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Task pause = coordinator.PauseAsync();
            client.ReleaseFirst.TrySetResult();
            await pause.WaitAsync(TimeSpan.FromSeconds(30));
            await run.WaitAsync(TimeSpan.FromSeconds(30));

            // Before the fix the chapter was marked Completed here: its untouched segments still held old translations.
            ProjectTranslationItem paused = coordinator.CurrentJob!.Items.Single();
            Assert.NotEqual(ProjectTranslationItemStatus.Completed, paused.Status);

            await coordinator.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(30));
            ChapterDocument after = (await context.Repository.LoadChapterAsync(context.Session, id)).Chapter;
            Assert.All(after.Segments, segment =>
                Assert.True(segment.Attempts.Count(attempt => attempt.Outcome == AttemptOutcome.Completed) >= 2));
            Assert.Equal(ProjectTranslationItemDisposition.TranslatedThisJob, coordinator.CurrentJob!.Items.Single().Disposition);
        }, ThreeSegments);
    }

    [Fact]
    public async Task Resume_reopens_a_retranslation_an_older_build_wrongly_saved_as_completed()
    {
        await WithProjectAsync(1, async context =>
        {
            var initial = context.Coordinator(new ProjectQueueFakeClient());
            await initial.StartAsync(await initial.PlanAsync(context.ChapterIds));
            Guid id = context.ChapterIds[0];

            var client = new ProjectQueueFakeClient { BlockFirst = true };
            var coordinator = context.Coordinator(client);
            Task run = coordinator.StartAsync(await coordinator.PlanAsync(context.ChapterIds, retranslateAll: new HashSet<Guid> { id }));
            await client.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Task pause = coordinator.PauseAsync();
            client.ReleaseFirst.TrySetResult();
            await pause.WaitAsync(TimeSpan.FromSeconds(30));
            await run.WaitAsync(TimeSpan.FromSeconds(30));

            // Recreate what the old build saved: item Completed while the chapter's own AllSegments run is paused.
            var store = new ProjectTranslationJobStore(context.Session);
            ProjectTranslationJob saved = (await store.LoadAsync())!;
            ProjectTranslationItem item = saved.Items.Single();
            item.Status = ProjectTranslationItemStatus.Completed;
            item.Disposition = ProjectTranslationItemDisposition.TranslatedThisJob;
            item.FinishedAt = DateTimeOffset.UtcNow;
            saved.Status = ProjectTranslationJobStatus.Paused;
            await store.SaveAsync(saved);

            var resumed = context.Coordinator(new ProjectQueueFakeClient());
            await resumed.ResumeAsync().WaitAsync(TimeSpan.FromSeconds(30));
            ChapterDocument after = (await context.Repository.LoadChapterAsync(context.Session, id)).Chapter;
            Assert.All(after.Segments, segment =>
                Assert.True(segment.Attempts.Count(attempt => attempt.Outcome == AttemptOutcome.Completed) >= 2));
        }, ThreeSegments);
    }
}
