using System.IO.Compression;
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
        Assert.True(Directory.Exists(Path.Combine(project.RootDirectory, "Backups")));
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
    public async Task ManifestChecksumDamage_RecoversLastValidBackup()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "Recover"), "Bản một");
        project.Manifest.Description = "bản hai";
        await _repository.SaveManifestAsync(project);
        await File.WriteAllTextAsync(project.ManifestPath, "{damaged");
        var recovered = await _repository.OpenAsync(project.ManifestPath);
        Assert.True(recovered.RecoveredFromBackup);
        Assert.Equal("Bản một", recovered.Session.Manifest.VietnameseTitle);
    }

    [Fact]
    public void ManifestRejectsAbsoluteAndTraversalPaths()
    {
        var manifest = ValidManifest();
        manifest.Files.Glossary = "../outside.json";
        Assert.Throws<InvalidDataException>(manifest.Validate);
        manifest.Files.Glossary = Path.GetFullPath(Path.Combine(_root, "outside.json"));
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
        Assert.NotNull(deleted.DeletedAt); Assert.Contains("History/Deleted/", deleted.RelativePath);
        Assert.True(File.Exists(project.Resolve(deleted.RelativePath)));
        await _repository.RestoreDeletedChapterAsync(project, id);
        var restored = await _repository.LoadChapterAsync(project, id);
        Assert.Equal("你好。", restored.Chapter.OriginalText);
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
        await store.SaveGlossaryAsync(new() { Terms = [new("陸雪琪", "Lục Tuyết Kỳ")] });
        await store.SaveContextNotesAsync("Giữ cách xưng hô đã duyệt.");
        Assert.Equal("model-rieng", (await store.LoadTranslationAsync()).Model.Model);
        Assert.Equal("Lục Tuyết Kỳ", (await store.LoadGlossaryAsync()).Terms.Single().Vietnamese);
        Assert.Equal("Giữ cách xưng hô đã duyệt.", await store.LoadContextNotesAsync());
    }

    [Fact]
    public async Task Legacy_project_settings_without_profile_id_keep_default_local_backend()
    {
        var project = await _repository.CreateAsync(Path.Combine(_root, "LegacyBackend"), "Truyện cũ");
        var value = new ProjectTranslationSettings { TraditionalDataDirectory = "LegacyData" };
        JsonObject json = Assert.IsType<JsonObject>(JsonNode.Parse(JsonSerializer.Serialize(value, DocumentJson.Options)));
        Assert.True(json.Remove("ollamaProfileId"));
        string path = project.Resolve(project.Manifest.Files.TranslationSettings);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, json.ToJsonString(DocumentJson.Options));

        ProjectTranslationSettings reopened = await new ProjectSettingsStore(project).LoadTranslationAsync();
        using var manager = new OllamaBackendManager(new OllamaProfileStore(
            Path.Combine(_root, "legacy-profiles.json"), new PassThroughProtector()));

        Assert.Null(reopened.OllamaProfileId);
        Assert.Equal(OllamaBackendProfile.DefaultLocalId, manager.GetProfile(reopened.OllamaProfileId).Id);
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
    public async Task BackupAndRestore_VerifiesAllFilesAndUsesNewDirectory()
    {
        var project = await CreateWithChapterAsync("Backup");
        var service = new ProjectBackupService();
        var backup = await service.CreateAsync(project, Path.Combine(_root, "out", "project.zip"));
        Assert.True(backup.FileCount >= 2); await ProjectBackupService.VerifyArchiveAsync(backup.ArchivePath);
        var restored = await service.RestoreAsync(backup.ArchivePath, Path.Combine(_root, "Restored"));
        var chapter = await _repository.LoadChapterAsync(restored, restored.Manifest.CurrentChapterId!.Value);
        Assert.Equal("你好。", chapter.Chapter.OriginalText);
    }

    [Fact]
    public async Task Restore_RefusesNonEmptyDestination()
    {
        var project = await CreateWithChapterAsync("NoOverwrite"); var service = new ProjectBackupService();
        var backup = await service.CreateAsync(project, Path.Combine(_root, "archive.zip"));
        string target = Path.Combine(_root, "Occupied"); Directory.CreateDirectory(target); await File.WriteAllTextAsync(Path.Combine(target, "keep.txt"), "keep");
        await Assert.ThrowsAsync<IOException>(() => service.RestoreAsync(backup.ArchivePath, target));
        Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(target, "keep.txt")));
    }

    [Fact]
    public async Task Restore_RejectsZipSlip()
    {
        string zip = Path.Combine(_root, "unsafe.zip"); Directory.CreateDirectory(_root);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        { using var writer = new StreamWriter(archive.CreateEntry("../escape.qtproj").Open()); writer.Write("bad"); }
        await Assert.ThrowsAsync<InvalidDataException>(() => new ProjectBackupService().RestoreAsync(zip, Path.Combine(_root, "UnsafeTarget")));
        Assert.False(File.Exists(Path.Combine(_root, "escape.qtproj")));
    }

    [Fact]
    public async Task LegacyImport_ScansBacksUpAndLeavesSourceUnchanged()
    {
        string legacy = Path.Combine(_root, "Legacy"); var legacyStore = new ChapterFileStore(legacy);
        var old = Chapter("Chương cũ"); old.Segments[0].UserDraft = "Bản sửa cũ"; await legacyStore.SaveAsync(old);
        byte[] before = await File.ReadAllBytesAsync(legacyStore.GetPath(old.Id));
        var importer = new LegacyProjectImporter(_repository); var candidates = await importer.ScanAsync(legacy);
        var target = await _repository.CreateAsync(Path.Combine(_root, "Imported"), "Truyện nhập");
        var result = await importer.ImportAsync(target, [new(candidates.Single(), 7)]);
        Assert.Equal(1, result.ImportedCount); Assert.True(File.Exists(result.SourceBackupPath));
        Assert.Equal(before, await File.ReadAllBytesAsync(legacyStore.GetPath(old.Id)));
        var imported = (await _repository.LoadChapterAsync(target, old.Id)).Chapter;
        Assert.Equal("Bản sửa cũ", imported.Segments[0].UserDraft); Assert.Equal(7, imported.ChapterNumber);
    }

    [Fact]
    public async Task LegacyImport_DuplicateNumberStopsBeforeChangingTarget()
    {
        var target = await CreateWithChapterAsync("Collision");
        string legacy = Path.Combine(_root, "Legacy2"); var store = new ChapterFileStore(legacy); var old = Chapter("Cũ"); await store.SaveAsync(old);
        var importer = new LegacyProjectImporter(_repository); var candidate = (await importer.ScanAsync(legacy)).Single();
        await Assert.ThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(target, [new(candidate, 1)]));
        Assert.Single(target.Manifest.Chapters);
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

    private sealed class PassThroughProtector : IOllamaSecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }
}
