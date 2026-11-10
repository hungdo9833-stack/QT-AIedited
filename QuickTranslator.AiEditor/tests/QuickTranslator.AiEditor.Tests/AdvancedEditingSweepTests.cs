using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.TextCleaning;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class AdvancedEditingSweepTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-edit-sweep-" + Guid.NewGuid().ToString("N"));
    private static readonly SegmenterOptions OneSegment = new() { MinimumHanCharacters = 1, TargetHanCharacters = 100, MaximumHanCharacters = 200 };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Edits_saved_translations_with_one_request_each_and_can_be_undone()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Thử");
        ChapterDocument ai = await AddAsync(repository, session, 1, "Hắn gọi nàng là biểu di mẫu.");
        ChapterDocument user = await AddAsync(repository, session, 2, "Bản AI chương hai.", userDraft: "Bản tôi đã sửa.");
        var client = new FakeClient { Respond = (_, _) => Task.FromResult(FakeClient.Completion("Hắn gọi nàng là dì họ.")) };
        var sweep = new AdvancedEditingSweep(repository, [new AdvancedEditingLane(client, new ModelOptions(), 3)]);

        (int segments, int userSegments) = await sweep.CountAsync(session, [ai.Id, user.Id]);
        Assert.Equal((2, 1), (segments, userSegments));

        HanSweepResult result = await sweep.EditAsync(session, [ai.Id, user.Id], includeUserText: false);

        Assert.Equal(1, client.Calls);
        Assert.StartsWith(AdvancedEditing.Marker, client.Prompts[0]);
        Assert.Contains("他叫她表姨母。", client.Prompts[0]);
        Assert.Equal(1, result.FixedSegments);
        Assert.Equal("Hắn gọi nàng là dì họ.", await TextAsync(repository, session, ai.Id));
        Assert.Equal("Bản tôi đã sửa.", await TextAsync(repository, session, user.Id));

        HanSweepResult undone = await sweep.UndoAsync(session, [ai.Id, user.Id]);
        Assert.Equal(1, undone.FixedSegments);
        Assert.Equal("Hắn gọi nàng là biểu di mẫu.", await TextAsync(repository, session, ai.Id));
    }

    [Fact]
    public async Task Confirmed_run_edits_user_text_too_and_an_unsafe_edit_leaves_the_segment_alone()
    {
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(_root, "Thử");
        ChapterDocument user = await AddAsync(repository, session, 1, "Bản AI.", userDraft: "Bản tôi đã sửa lại.");
        ChapterDocument bad = await AddAsync(repository, session, 2, "Dòng một.\nDòng hai.");
        var client = new FakeClient
        {
            Respond = (_, _) => Task.FromResult(FakeClient.Completion("Bản tôi sửa lại rồi."))
        };
        var sweep = new AdvancedEditingSweep(repository, [new AdvancedEditingLane(client, new ModelOptions())]);

        HanSweepResult result = await sweep.EditAsync(session, [user.Id, bad.Id], includeUserText: true);

        Assert.Equal(2, client.Calls);
        Assert.Equal(1, result.FixedSegments);
        Assert.Equal(1, result.StillHanSegments);
        Assert.Equal("Bản tôi sửa lại rồi.", await TextAsync(repository, session, user.Id));
        Assert.Equal("Dòng một.\nDòng hai.", await TextAsync(repository, session, bad.Id));
    }

    private static async Task<ChapterDocument> AddAsync(ProjectRepository repository, ProjectSession session, int number,
        string aiText, string? userDraft = null)
    {
        ChapterDocument chapter = TranslationService.CreateChapter($"Chương {number}", "他叫她表姨母。", OneSegment);
        TranslationSegment segment = chapter.Segments[0];
        segment.Attempts.Add(new TranslationAttempt { Outcome = AttemptOutcome.Completed, AiText = aiText });
        segment.Status = SegmentStatus.AwaitingReview;
        if (userDraft is not null) { segment.UserDraft = userDraft; segment.DraftRevision = 1; }
        await repository.AddChapterAsync(session, chapter, number);
        return chapter;
    }

    private static async Task<string> TextAsync(ProjectRepository repository, ProjectSession session, Guid id) =>
        (await repository.LoadChapterAsync(session, id)).Chapter.Segments[0].EditableText;
}
