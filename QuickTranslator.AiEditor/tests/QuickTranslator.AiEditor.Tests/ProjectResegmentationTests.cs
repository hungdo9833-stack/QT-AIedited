using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectResegmentationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-project-resegment-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();
    private static readonly SegmenterOptions Small = new()
    {
        MinimumHanCharacters = 1, TargetHanCharacters = 2,
        MaximumHanCharacters = 2, MaximumTextElements = 2000
    };

    [Fact]
    public async Task Batch_preview_and_commit_preserve_protected_data_and_update_manifest_once()
    {
        ProjectSession session = await CreateProjectAsync(3);
        Guid protectedId = session.Manifest.Chapters[1].ChapterId;
        ChapterDocument protectedChapter = (await _repository.LoadChapterAsync(session, protectedId)).Chapter;
        protectedChapter.Segments[0].UserDraft = "Nháp được bảo vệ";
        protectedChapter.Segments[0].Status = SegmentStatus.Approved;
        await _repository.UpdateChapterAsync(session, protectedChapter);
        ProjectResegmentationPreview preview = await _repository.PreviewResegmentationAsync(session,
            session.Manifest.Chapters.Select(entry => entry.ChapterId).ToArray(), Small);
        string before = JsonSerializer.Serialize(session.Manifest, DocumentJson.Options);

        await Assert.ThrowsAsync<SegmentationConfirmationRequiredException>(() =>
            _repository.ResegmentChaptersAsync(session, preview, confirmed: false));
        Assert.Equal(before, JsonSerializer.Serialize(session.Manifest, DocumentJson.Options));

        await _repository.ResegmentChaptersAsync(session, preview, confirmed: true);
        foreach (ProjectChapterEntry entry in session.Manifest.Chapters)
        {
            ChapterDocument chapter = (await _repository.LoadChapterAsync(session, entry.ChapterId)).Chapter;
            Assert.Equal(chapter.OriginalText, TextSegmenter.Reconstruct(chapter.Segments));
            Assert.Equal(Small, chapter.SegmentationOptions);
        }
        ChapterDocument preserved = (await _repository.LoadChapterAsync(session, protectedId)).Chapter;
        Assert.Contains(preserved.Segments, segment => segment.UserDraft == "Nháp được bảo vệ");
    }

    [Fact]
    public async Task Batch_fault_rolls_back_every_chapter_and_manifest()
    {
        ProjectSession session = await CreateProjectAsync(5);
        Guid[] ids = session.Manifest.Chapters.Select(entry => entry.ChapterId).ToArray();
        ProjectResegmentationPreview preview = await _repository.PreviewResegmentationAsync(session, ids, Small);
        Dictionary<string, byte[]> before = Capture(session);
        string manifest = JsonSerializer.Serialize(session.Manifest, DocumentJson.Options);
        _repository.FaultPoint = point =>
        {
            if (point == "AfterFile:3") throw new IOException("synthetic batch failure");
        };

        await Assert.ThrowsAsync<IOException>(() => _repository.ResegmentChaptersAsync(session, preview, confirmed: false));

        _repository.FaultPoint = null;
        Assert.Equal(manifest, JsonSerializer.Serialize(session.Manifest, DocumentJson.Options));
        AssertExact(before, Capture(session));
    }

    private async Task<ProjectSession> CreateProjectAsync(int count)
    {
        ProjectSession session = await _repository.CreateAsync(_root, "Chia lại tổng hợp");
        for (int number = 1; number <= count; number++)
        {
            ChapterDocument chapter = TranslationService.CreateChapter("Chương " + number,
                "甲乙丙。丁戊己。庚辛壬。", new SegmenterOptions
                {
                    MinimumHanCharacters = 1, TargetHanCharacters = 3,
                    MaximumHanCharacters = 4, MaximumTextElements = 2000
                });
            await _repository.AddChapterAsync(session, chapter, number);
        }
        return session;
    }

    private static Dictionary<string, byte[]> Capture(ProjectSession session) => Directory
        .GetFiles(session.RootDirectory, "*", SearchOption.AllDirectories)
        .Where(path => !path.Contains(Path.Combine("History", "Transactions"), StringComparison.OrdinalIgnoreCase))
        .ToDictionary(path => Path.GetRelativePath(session.RootDirectory, path), File.ReadAllBytes);

    private static void AssertExact(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (string key in before.Keys) Assert.Equal(before[key], after[key]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
