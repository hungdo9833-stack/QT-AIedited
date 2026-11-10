using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChapterNumberGapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-gaps-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Deleting_an_inserted_chapter_puts_the_later_numbers_back()
    {
        ProjectSession session = await _repository.CreateAsync(_root, "Thử");
        Guid[] ids = await AddAsync(session, 5);
        ChapterDocument wrong = TranslationService.CreateChapter("Chèn nhầm", "错。");
        await _repository.InsertChapterAsync(session, wrong, 3);
        Assert.Equal(6, Number(session, ids[4]));

        await _repository.SoftDeleteChapterAsync(session, wrong.Id);

        Assert.Equal([1, 2, 3, 4, 5], ids.Select(id => Number(session, id)));
        Assert.Empty(ProjectRepository.MissingChapterNumbers(session.Manifest));
    }

    [Fact]
    public async Task Restoring_a_chapter_takes_its_old_number_and_pushes_the_rest_up()
    {
        ProjectSession session = await _repository.CreateAsync(_root, "Thử");
        Guid[] ids = await AddAsync(session, 4);
        await _repository.SoftDeleteChapterAsync(session, ids[1]);
        Assert.Equal([1, 2, 3], new[] { ids[0], ids[2], ids[3] }.Select(id => Number(session, id)));

        await _repository.RestoreDeletedChapterAsync(session, ids[1]);

        Assert.Equal([1, 2, 3, 4], ids.Select(id => Number(session, id)));
    }

    [Fact]
    public async Task A_book_left_with_a_gap_is_renumbered_in_order()
    {
        ProjectSession session = await _repository.CreateAsync(_root, "Thử");
        Guid first = (await AddAsync(session, 1, start: 1))[0];
        Guid[] later = await AddAsync(session, 2, start: 3); // 1, 3, 4: number 2 is missing
        Assert.Equal([2], ProjectRepository.MissingChapterNumbers(session.Manifest));

        int changed = await _repository.CloseChapterNumberGapsAsync(session);

        Assert.Equal(2, changed);
        Assert.Equal([1, 2, 3], new[] { first }.Concat(later).Select(id => Number(session, id)));
        Assert.Equal(2, (await _repository.LoadChapterAsync(session, later[0])).Chapter.ChapterNumber);
        Assert.Equal(0, await _repository.CloseChapterNumberGapsAsync(session));
    }

    private async Task<Guid[]> AddAsync(ProjectSession session, int count, int start = 1)
    {
        var ids = new List<Guid>();
        for (int i = 0; i < count; i++)
        {
            ChapterDocument chapter = TranslationService.CreateChapter($"Chương {start + i}", "你好。");
            await _repository.AddChapterAsync(session, chapter, start + i);
            ids.Add(chapter.Id);
        }
        return ids.ToArray();
    }

    private static int Number(ProjectSession session, Guid id) =>
        session.Manifest.Chapters.Single(entry => entry.ChapterId == id && entry.DeletedAt is null).Number;
}
