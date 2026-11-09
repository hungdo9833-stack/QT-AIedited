using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class HanResidueSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-han-sweep-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task A_saved_book_is_repaired_in_place_and_the_old_text_stays_in_history()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Thử");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương 1", "梁卓怡说：瞓啦。\n\n她笑了。",
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 100, MaximumHanCharacters = 200 });
        TranslationSegment segment = chapter.Segments[0];
        segment.Attempts.Add(new TranslationAttempt
        {
            Outcome = AttemptOutcome.Completed,
            AiText = "Lương Trác Di nói: 瞓 đi.\nCô cười.",
            Prompt = new TranslationPromptBuilder().Build(segment.SourceText, [], "")
        });
        segment.Status = SegmentStatus.AwaitingReview;
        await repository.AddChapterAsync(session, chapter, 1);

        var repairer = new HanResidueRepairClient(new NothingClient(), () => null,
            (_, _) => Task.FromResult(""), askForNoHan: false);
        HanSweepResult result = await new HanResidueSweep(repository, repairer, new ModelOptions { Model = "local" })
            .SweepAsync(session);

        Assert.Equal(1, result.FixedSegments);
        Assert.Equal(0, result.StillHanSegments);
        TranslationSegment saved = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter.Segments[0];
        Assert.Equal("Lương Trác Di nói: Ngủ đi.\nCô cười.", saved.EditableText);
        Assert.Equal("Lương Trác Di nói: 瞓 đi.\nCô cười.", saved.LatestAiAttempt!.AiText);
        ChapterDocument reloaded = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;
        Assert.Single(reloaded.BatchReplacementHistory);

        HanSweepResult undone = await new HanResidueSweep(repository, repairer, new ModelOptions { Model = "local" })
            .UndoAsync(session);
        Assert.Equal(1, undone.FixedSegments);
        Assert.Equal("Lương Trác Di nói: 瞓 đi.\nCô cười.",
            (await repository.LoadChapterAsync(session, chapter.Id)).Chapter.Segments[0].EditableText);
    }

    [Fact]
    public async Task A_book_without_Chinese_is_left_untouched()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Sạch");
        ChapterDocument chapter = TranslationService.CreateChapter("Chương 1", "她笑了。",
            new SegmenterOptions { MinimumHanCharacters = 1, TargetHanCharacters = 100, MaximumHanCharacters = 200 });
        chapter.Segments[0].Attempts.Add(new TranslationAttempt { Outcome = AttemptOutcome.Completed, AiText = "Cô cười." });
        await repository.AddChapterAsync(session, chapter, 1);
        var repairer = new HanResidueRepairClient(new NothingClient(), () => null, null, askForNoHan: false);

        HanSweepResult result = await new HanResidueSweep(repository, repairer, new ModelOptions()).SweepAsync(session);

        Assert.Equal(0, result.FixedSegments);
        Assert.Empty((await repository.LoadChapterAsync(session, chapter.Id)).Chapter.BatchReplacementHistory);
    }

    private sealed class NothingClient : ILlmClient
    {
        public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default) =>
            Task.FromResult(new LlmCompletion("", false, "skipped", new GenerationMetrics()));
    }
}
