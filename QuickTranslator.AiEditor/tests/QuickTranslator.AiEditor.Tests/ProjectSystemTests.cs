using System.Text.Json;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-project-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRepository _repository = new();

    [Fact]
    public async Task Create_WritesPortableManifestAndExpectedLayout()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "Novel"), "Truyện thử");
        Assert.EndsWith(".qtproj", project.ManifestPath);
        Assert.True(File.Exists(project.ManifestPath));
        Assert.True(Directory.Exists(Path.Combine(project.RootDirectory, "Chapters")));
        Assert.True(Directory.Exists(Path.Combine(project.RootDirectory, "Queue")));
        Assert.False(Directory.Exists(Path.Combine(project.RootDirectory, "Backups")));
        Assert.All(project.Manifest.Files.GetType().GetProperties().Select(x => (string)x.GetValue(project.Manifest.Files)!),
            path => Assert.False(Path.IsPathRooted(path)));
    }

    [Fact]
    public async Task MoveWholeFolder_ProjectStillOpensWithRelativeChapterPaths()
    {
        var project = await CreateWithChapterAsync("Old");
        string moved = Path.Combine(_root, "Moved");
        Directory.Move(project.RootDirectory, moved);
        var opened = await _repository.OpenAsync(Path.Combine(moved, Path.GetFileName(project.ManifestPath)));
        var chapter = await _repository.LoadChapterAsync(opened.Session, opened.Session.Manifest.CurrentChapterId!.Value);
        Assert.Equal("你好。", chapter.Chapter.OriginalText);
    }

    [Fact]
    public async Task ValidManifestEditedOutsideTheApplicationOpensWithoutChecksum()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "ExternalEdit"), "Bản một");
        var node = JsonNode.Parse(await File.ReadAllTextAsync(project.ManifestPath))!.AsObject();
        node["description"] = "được sửa bằng công cụ ngoài";
        await File.WriteAllTextAsync(project.ManifestPath, node.ToJsonString());
        var opened = await _repository.OpenAsync(project.ManifestPath);
        Assert.Equal("được sửa bằng công cụ ngoài", opened.Session.Manifest.Description);
        Assert.False(File.Exists(project.ManifestPath + ".bak"));
    }

    [Fact]
    public void ManifestRejectsAbsoluteAndTraversalPaths()
    {
        var manifest = ValidManifest();
        manifest.Files.Resources = "../outside.json";
        Assert.Throws<InvalidDataException>(manifest.Validate);
        manifest.Files.Resources = Path.GetFullPath(Path.Combine(_root, "outside.json"));
        Assert.Throws<InvalidDataException>(manifest.Validate);
    }

    [Fact]
    public async Task AddChapter_BindsProjectAndCharacterIdsAndRejectsDuplicateNumber()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "Add"), "Truyện");
        var chapter = Chapter("Một");
        await _repository.AddChapterAsync(project, chapter, 1, "第一章");
        var loaded = (await _repository.LoadChapterAsync(project, chapter.Id)).Chapter;
        Assert.Equal(project.Manifest.ProjectId, loaded.ProjectId);
        Assert.Equal(project.Manifest.ProjectId, loaded.CharacterProject.Id);
        Assert.Equal(1, loaded.ChapterNumber);
        Assert.Equal("第一章", loaded.ChineseTitle);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.AddChapterAsync(project, Chapter("Hai"), 1));
    }

    [Fact]
    public async Task InsertChapter_RenumbersManifestOnceAndLeavesExistingChapterFilesUntouched()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "Insert"), "Truyện");
        ChapterDocument first = Chapter("Một");
        ChapterDocument second = Chapter("Hai");
        ChapterDocument third = Chapter("Ba");
        await _repository.AddChapterAsync(project, first, 1);
        await _repository.AddChapterAsync(project, second, 2);
        await _repository.AddChapterAsync(project, third, 3);
        var existingFiles = new[] { first.Id, second.Id, third.Id }
            .ToDictionary(id => id, id => File.ReadAllBytes(project.ChapterStore.GetPath(id)));

        ChapterDocument inserted = Chapter("Chèn");
        ProjectChapterEntry entry = await _repository.InsertChapterAsync(project, inserted, 2);

        Assert.Equal(inserted.Id, entry.ChapterId);
        Assert.Equal(inserted.Id, project.Manifest.CurrentChapterId);
        Assert.Equal(new[] { first.Id, inserted.Id, second.Id, third.Id }, project.Manifest.Chapters
            .Where(item => item.DeletedAt is null).OrderBy(item => item.Number).Select(item => item.ChapterId));
        Assert.Equal(new[] { 1, 2, 3, 4 }, project.Manifest.Chapters
            .Where(item => item.DeletedAt is null).OrderBy(item => item.Number).Select(item => item.Number));
        Assert.All(existingFiles, item => Assert.Equal(item.Value, File.ReadAllBytes(project.ChapterStore.GetPath(item.Key))));

        ProjectSession reopened = (await _repository.OpenAsync(project.ManifestPath)).Session;
        Assert.Equal(new[] { 1, 2, 3, 4 }, reopened.Manifest.Chapters
            .Where(item => item.DeletedAt is null).OrderBy(item => item.Number).Select(item => item.Number));
    }

    [Fact]
    public async Task UpdateChapter_PreservesProtectedUserDraft()
    {
        var project = await CreateWithChapterAsync("Draft");
        var chapter = (await _repository.LoadChapterAsync(project, project.Manifest.CurrentChapterId!.Value)).Chapter;
        chapter.Segments[0].UserDraft = "Bản người dùng sửa";
        chapter.Segments[0].Status = SegmentStatus.Approved;
        await _repository.UpdateChapterAsync(project, chapter);
        var reloaded = (await _repository.LoadChapterAsync(project, chapter.Id)).Chapter;
        Assert.Equal("Bản người dùng sửa", reloaded.Segments[0].UserDraft);
        Assert.Equal(ProjectChapterState.Approved, project.Manifest.Chapters.Single().State);
    }

    [Fact]
    public async Task SoftDeleteAndRestore_MovesFileWithoutLosingContent()
    {
        var project = await CreateWithChapterAsync("Delete"); Guid id = project.Manifest.CurrentChapterId!.Value;
        await _repository.SoftDeleteChapterAsync(project, id);
        var deleted = project.Manifest.Chapters.Single();
        Assert.NotNull(deleted.DeletedAt); Assert.Contains("Deleted/", deleted.RelativePath);
        Assert.True(File.Exists(project.Resolve(deleted.RelativePath)));
        await _repository.RestoreDeletedChapterAsync(project, id);
        var restored = await _repository.LoadChapterAsync(project, id);
        Assert.Equal("你好。", restored.Chapter.OriginalText);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(300)]
    public async Task BatchSoftDeleteAndRestore_CommitsAllSelectedChaptersAtomically(int count)
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "Batch-" + count), "Truyện batch");
        var ids = new List<Guid>(count);
        for (int number = 1; number <= count; number++)
        {
            ChapterDocument chapter = TranslationService.CreateChapter("Chương " + number, $"第{number}章。你好。 ");
            chapter.Segments[0].UserDraft = "Nháp " + number;
            chapter.Segments[0].DraftRevision = 1;
            ids.Add(chapter.Id);
            await _repository.AddChapterAsync(project, chapter, number);
        }

        await _repository.SoftDeleteChaptersAsync(project, ids);
        Assert.Equal(count, project.Manifest.DeletedChapterCount);
        Assert.All(project.Manifest.Chapters, entry =>
        {
            Assert.NotNull(entry.DeletedAt);
            Assert.True(File.Exists(project.Resolve(entry.RelativePath)));
        });

        await _repository.RestoreDeletedChaptersAsync(project, ids);
        Assert.Equal(count, project.Manifest.ChapterCount);
        Assert.All(project.Manifest.Chapters, entry => Assert.Null(entry.DeletedAt));
        ChapterDocument restored = (await _repository.LoadChapterAsync(project, ids[^1])).Chapter;
        Assert.Equal("Nháp " + count, restored.Segments[0].UserDraft);
    }

    [Fact]
    public async Task DuplicateChapter_GetsStableNewIdsAndProtectedDraft()
    {
        var project = await CreateWithChapterAsync("Duplicate");
        var source = (await _repository.LoadChapterAsync(project, project.Manifest.CurrentChapterId!.Value)).Chapter;
        source.Segments[0].UserDraft = "Nháp đã sửa"; await _repository.UpdateChapterAsync(project, source);
        var entry = await _repository.DuplicateChapterAsync(project, source.Id, 2, "Bản sao");
        var duplicate = (await _repository.LoadChapterAsync(project, entry.ChapterId)).Chapter;
        Assert.NotEqual(source.Id, duplicate.Id);
        Assert.NotEqual(source.Segments[0].Id, duplicate.Segments[0].Id);
        Assert.Equal("Nháp đã sửa", duplicate.Segments[0].UserDraft);
        Assert.True(duplicate.Segments[0].IsProtectedFromAutomaticTranslation);
    }

    [Fact]
    public async Task ProjectSettings_RoundTripSeparatelyFromAppSettings()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "Settings"), "Truyện");
        var store = new ProjectSettingsStore(project);
        await store.SaveTranslationAsync(new() { Model = new() { Model = "model-rieng", Temperature = 0.2 }, TraditionalDataDirectory = "D:/QTData" });
        var resourceStore = new ProjectResourceStore(project);
        ProjectResources resources = await resourceStore.LoadAsync();
        resources.Terms.Add(new("陸雪琪", "Lục Tuyết Kỳ"));
        resources.Context = "Giữ cách xưng hô đã duyệt.";
        await resourceStore.SaveAsync(resources);
        Assert.Equal("model-rieng", (await store.LoadTranslationAsync()).Model.Model);
        ProjectResources reopened = await resourceStore.LoadAsync();
        Assert.Equal("Lục Tuyết Kỳ", reopened.Terms.Single().Vietnamese);
        Assert.Equal("Giữ cách xưng hô đã duyệt.", reopened.Context);
    }

    [Fact]
    public async Task TranslationSettings_RequireTheCurrentUnifiedSchema()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "CurrentSchema"), "Truyện");
        var value = new ProjectTranslationSettings { TraditionalDataDirectory = "Data" };
        JsonObject json = Assert.IsType<JsonObject>(JsonNode.Parse(JsonSerializer.Serialize(value, DocumentJson.Options)));
        Assert.True(json.Remove("ollamaProfileId"));
        string path = project.Resolve(project.Manifest.Files.TranslationSettings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json.ToJsonString(DocumentJson.Options));

        await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectSettingsStore(project).LoadTranslationAsync());
    }

    [Fact]
    public async Task Library_RememberSearchRemoveAndRelocateOnlyChangesShortcut()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "Library"), "Tiên hiệp");
        var library = new ProjectLibrary(); ProjectLibraryStore.Remember(library, project);
        Assert.Single(ProjectLibraryStore.Search(library, "Tiên"));
        string other = Path.Combine(_root, "other.qtproj");
        ProjectLibraryStore.Relocate(library, project.Manifest.ProjectId, other);
        Assert.True(library.RecentProjects.Single().Missing);
        ProjectLibraryStore.RemoveShortcut(library, project.Manifest.ProjectId);
        Assert.Empty(library.RecentProjects);
        Assert.True(File.Exists(project.ManifestPath));
    }

    [Fact]
    public async Task LibraryStore_PersistsAndMarksMissingProject()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "Recent"), "Gần đây");
        var store = new ProjectLibraryStore(Path.Combine(_root, "state", "library.json"));
        var library = new ProjectLibrary(); ProjectLibraryStore.Remember(library, project); await store.SaveAsync(library);
        File.Delete(project.ManifestPath);
        var loaded = await store.LoadAsync();
        Assert.True(loaded.RecentProjects.Single().Missing);
    }

    [Fact]
    public async Task NewProjectLayout_UsesDedicatedDeletedFolderWithoutHistoryInfrastructure()
    {
        ProjectSession project = await _repository.CreateAsync(Path.Combine(_root, "Layout"), "Truyện mới");
        Assert.True(Directory.Exists(project.Resolve("Deleted")));
        Assert.False(Directory.Exists(project.Resolve("History")));
    }

    [Fact]
    public void ManifestRejectsDuplicateActiveNumbersButAllowsDeletedHistoryNumber()
    {
        var manifest = ValidManifest();
        manifest.Chapters = [Entry(1), Entry(1)];
        Assert.Throws<InvalidDataException>(manifest.Validate);
        manifest.Chapters[1].DeletedAt = DateTimeOffset.UtcNow;
        manifest.ChapterCount = 1; manifest.DeletedChapterCount = 1;
        manifest.Validate();
    }

    private async Task<ProjectSession> CreateWithChapterAsync(string folder)
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, folder), "Truyện " + folder);
        await _repository.AddChapterAsync(project, Chapter("Chương 1"), 1);
        return project;
    }
    private static ChapterDocument Chapter(string title) => TranslationService.CreateChapter(title, "你好。");
    private static ProjectManifest ValidManifest() => new() { ProjectId = Guid.NewGuid(), VietnameseTitle = "Hợp lệ", CreatedAt = DateTimeOffset.UtcNow };
    private static ProjectChapterEntry Entry(int number) => new() { ChapterId = Guid.NewGuid(), Number = number, RelativePath = $"Chapters/{Guid.NewGuid():D}.qtaie.json" };
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

}
