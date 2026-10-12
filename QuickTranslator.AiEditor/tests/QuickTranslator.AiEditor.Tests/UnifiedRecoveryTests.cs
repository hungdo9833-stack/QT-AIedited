using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Reading;

namespace QuickTranslator.AiEditor.Tests;

public sealed class UnifiedRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-phase3-recovery-" + Guid.NewGuid());
    public static IEnumerable<object[]> Stores => new[] { "manifest", "character", "translation", "export", "glossary", "context", "reader", "library", "resources", "preset", "chapter", "traditional" }.Select(x => new object[] { x });

    [Theory][MemberData(nameof(Stores))]
    public async Task F08_PrimaryCorruptBackupGoodLoadsWithoutChangingEitherFile(string kind)
    {
        var store = await SetupAsync(kind);
        byte[] good = File.ReadAllBytes(store.Path + ".bak");
        File.WriteAllBytes(store.Path, kind == "context" ? [0xff, 0xfe, 0x61] : System.Text.Encoding.UTF8.GetBytes("{corrupt"));
        byte[] damaged = File.ReadAllBytes(store.Path);
        await store.Load();
        Assert.Equal(damaged, File.ReadAllBytes(store.Path)); Assert.Equal(good, File.ReadAllBytes(store.Path + ".bak"));
    }

    [Theory][MemberData(nameof(Stores))]
    public async Task F08_MissingPrimaryUsesBackupInsteadOfDefaults(string kind)
    {
        var store = await SetupAsync(kind);
        byte[] backup = File.ReadAllBytes(store.Path + ".bak");
        File.Delete(store.Path);
        await store.Load();
        Assert.False(File.Exists(store.Path)); Assert.Equal(backup, File.ReadAllBytes(store.Path + ".bak"));
        await store.Save();
        Assert.Equal(backup, File.ReadAllBytes(store.Path + ".bak"));
    }

    [Theory][MemberData(nameof(Stores))]
    public async Task F08_BothCorruptCannotBeOverwrittenWithDefaultsOrNewSettings(string kind)
    {
        var store = await SetupAsync(kind);
        byte[] damaged = kind == "context" ? [0xff, 0xfe, 0x61] : System.Text.Encoding.UTF8.GetBytes("{corrupt");
        File.WriteAllBytes(store.Path, damaged); File.WriteAllBytes(store.Path + ".bak", damaged);
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => store.Load());
        await Assert.ThrowsAnyAsync<InvalidDataException>(() => store.Save());
        Assert.Equal(damaged, File.ReadAllBytes(store.Path)); Assert.Equal(damaged, File.ReadAllBytes(store.Path + ".bak"));
    }

    [Theory][MemberData(nameof(Stores))]
    public async Task F08_RecoveryWriteMustPreserveGoodBackupAndDamagedPrimaryEvidence(string kind)
    {
        var store = await SetupAsync(kind);
        byte[] backup = File.ReadAllBytes(store.Path + ".bak");
        byte[] damaged = kind == "context" ? [0xff, 0xfe, 0x61] : System.Text.Encoding.UTF8.GetBytes("{corrupt");
        File.WriteAllBytes(store.Path, damaged);
        await store.Load(); await store.Save();
        Assert.Equal(backup, File.ReadAllBytes(store.Path + ".bak"));
        Assert.NotEqual(damaged, File.ReadAllBytes(store.Path));
    }

    [Theory][MemberData(nameof(Stores))]
    public async Task F08_SharingFailureCannotBeTreatedAsMissingOrReplaced(string kind)
    {
        var store = await SetupAsync(kind);
        byte[] before = File.ReadAllBytes(store.Path), backup = File.ReadAllBytes(store.Path + ".bak");
        using (var locked = new FileStream(store.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.Load());
            await Assert.ThrowsAnyAsync<IOException>(() => store.Save());
        }
        Assert.Equal(before, File.ReadAllBytes(store.Path)); Assert.Equal(backup, File.ReadAllBytes(store.Path + ".bak"));
    }

    [Theory]
    [InlineData("BeforeRescue", "io")]
    [InlineData("AfterRescue", "io")]
    [InlineData("BeforeReplacement", "io")]
    [InlineData("BeforeRescue", "permissions")]
    [InlineData("AfterRescue", "permissions")]
    [InlineData("BeforeReplacement", "permissions")]
    [InlineData("BeforeRescue", "cancel")]
    [InlineData("AfterRescue", "cancel")]
    [InlineData("BeforeReplacement", "cancel")]
    public async Task F08_RecoveryWriteFaultKeepsBothSourceFilesAndCanRetry(string point, string error)
    {
        var store = await SetupAsync("reader");
        File.WriteAllText(store.Path, "{keep damaged evidence");
        byte[] before = File.ReadAllBytes(store.Path), backup = File.ReadAllBytes(store.Path + ".bak");
        RecoveryFiles.FaultPoint.Value = value => { if (value == point) throw error switch
            { "permissions" => new UnauthorizedAccessException("Injected denied"), "cancel" => new OperationCanceledException("Injected cancel"), _ => new IOException("Injected IO") }; };
        try { await Assert.ThrowsAnyAsync<Exception>(() => store.Save()); }
        finally { RecoveryFiles.FaultPoint.Value = null; }
        Assert.Equal(before, File.ReadAllBytes(store.Path)); Assert.Equal(backup, File.ReadAllBytes(store.Path + ".bak"));
        await store.Save(); await store.Load(); Assert.Equal(backup, File.ReadAllBytes(store.Path + ".bak"));
    }

    [Fact]
    public async Task F08_RescueExportFailureCannotRepairOrAlterSourceAndVerifiedZipKeepsRawBytes()
    {
        var store = await SetupAsync("reader");
        File.WriteAllText(store.Path, "{diagnostic bytes");
        byte[] before = File.ReadAllBytes(store.Path), backup = File.ReadAllBytes(store.Path + ".bak");
        string destination = Path.Combine(_root, "rescue.zip"); File.WriteAllText(destination, "Existing artifact");
        await Assert.ThrowsAnyAsync<IOException>(() => RecoveryFiles.ExportRescueAsync([store.Path], destination));
        Assert.Equal("Existing artifact", File.ReadAllText(destination));
        Assert.Equal(before, File.ReadAllBytes(store.Path)); Assert.Equal(backup, File.ReadAllBytes(store.Path + ".bak"));
        string good = Path.Combine(_root, "verified.zip"); await RecoveryFiles.ExportRescueAsync([store.Path], good);
        using var archive = System.IO.Compression.ZipFile.OpenRead(good);
        using var stream = archive.Entries.Single(x => x.Name.StartsWith("0000-")).Open();
        using var buffer = new MemoryStream(); await stream.CopyToAsync(buffer); Assert.Equal(before, buffer.ToArray());
    }

    [Fact]
    public async Task F08_RepairRejectsChangedBackupAfterPreview()
    {
        var store = await SetupAsync("reader"); File.WriteAllText(store.Path, "{damaged");
        var reader = new ReaderPreferencesStore(store.Path); await reader.LoadAsync();
        var newer = new ReaderPreferences { FontFamily = "Một bản backup khác" };
        File.WriteAllText(store.Path + ".bak", System.Text.Json.JsonSerializer.Serialize(newer, DocumentJson.Options));
        await Assert.ThrowsAnyAsync<IOException>(() => reader.RestoreBackupAsync());
        Assert.Equal("{damaged", File.ReadAllText(store.Path));
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("character")]
    public async Task F08_ChecksummedNullFieldsAreInvalidContentAndUseVerifiedBackup(string kind)
    {
        var store = await SetupAsync(kind);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(store.Path))!;
        if (kind == "manifest") json["project"]!["chapters"] = null;
        else
        {
            var profile = new CharacterProfile { VietnameseName = "Nhân vật tổng hợp", ChineseName = "王秀兰", NormalizationEnabled = false };
            var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(profile, DocumentJson.Options))!;
            node["chineseName"] = null;
            json["project"]!["profiles"] = new System.Text.Json.Nodes.JsonArray(node);
        }
        var element = System.Text.Json.JsonSerializer.SerializeToElement(json["project"], DocumentJson.Options);
        json["contentSha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(element, DocumentJson.Options)));
        File.WriteAllText(store.Path, json.ToJsonString(DocumentJson.Options));
        await store.Load(); // Correct checksum is insufficient without validating the model.
    }

    [Fact]
    public async Task F08_InvalidBackupIsArchivedBeforeAValidPrimaryRotatesIntoItsPath()
    {
        var store = await SetupAsync("reader");
        const string bad = "{bad backup evidence"; File.WriteAllText(store.Path + ".bak", bad);
        await store.Save();
        bool found = false;
        foreach (string zip in Directory.GetFiles(_root, "*.rescue-*.zip", SearchOption.AllDirectories))
        {
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            foreach (var entry in archive.Entries.Where(x => x.Name.EndsWith(".bak")))
            {
                using var input = new StreamReader(entry.Open());
                if (await input.ReadToEndAsync() == bad) found = true;
            }
        }
        Assert.True(found, "Damaged backup bytes must be preserved for diagnosis before rotation.");
    }

    private async Task<StoreCase> SetupAsync(string kind)
    {
        var repository = new ProjectRepository();
        var session = await repository.CreateAsync(_root, "Recovery tổng hợp");
        var settings = new ProjectSettingsStore(session);
        string path; Func<Task> load, save;
        switch (kind)
        {
            case "manifest":
                path = session.ManifestPath; load = async () => { _ = await repository.OpenAsync(path); };
                save = () => repository.SaveManifestAsync(session); break;
            case "character":
                var characters = new CharacterProject { Id = session.Manifest.ProjectId, Name = "Hồ sơ cần giữ" };
                var characterStore = new CharacterProjectStore(Path.Combine(_root, "Characters"));
                path = characterStore.GetPath(characters.Id); load = async () => { Assert.Equal("Hồ sơ cần giữ", (await characterStore.LoadAsync(path)).Name); };
                save = () => characterStore.SaveAsync(characters); break;
            case "translation":
                path = session.Resolve(session.Manifest.Files.TranslationSettings);
                var translation = new ProjectTranslationSettings { TraditionalDataDirectory = "SyntheticDataPath" };
                load = async () => { Assert.Equal("SyntheticDataPath", (await settings.LoadTranslationAsync()).TraditionalDataDirectory); };
                save = () => settings.SaveTranslationAsync(translation); break;
            case "export":
                path = session.Resolve(session.Manifest.Files.ExportSettings);
                var export = new ProjectExportSettings { FileNameTemplate = "Synthetic {title}" };
                load = async () => { Assert.Equal(export.FileNameTemplate, (await settings.LoadExportAsync()).FileNameTemplate); };
                save = () => settings.SaveExportAsync(export); break;
            case "glossary":
                path = session.Resolve(session.Manifest.Files.Glossary);
                var glossary = new ProjectGlossary { Terms = [new("王秀兰", "Vương Tú Lan")] };
                load = async () => { Assert.Single((await settings.LoadGlossaryAsync()).Terms); };
                save = () => settings.SaveGlossaryAsync(glossary); break;
            case "context":
                path = session.Resolve(session.Manifest.Files.ContextNotes);
                load = async () => { Assert.Equal("Ngữ cảnh tổng hợp phải giữ", await settings.LoadContextNotesAsync()); };
                save = () => settings.SaveContextNotesAsync("Ngữ cảnh tổng hợp phải giữ"); break;
            case "reader":
                path = session.Resolve(session.Manifest.Files.ReaderSettings);
                var readerStore = new ReaderPreferencesStore(path); var reader = new ReaderPreferences { FontFamily = "Synthetic Font" };
                load = async () => { Assert.Equal(reader.FontFamily, (await readerStore.LoadAsync()).FontFamily); };
                save = () => readerStore.SaveAsync(reader); break;
            case "resources":
                var resourceStore = new ProjectResourceStore(session); path = resourceStore.PathName; int resourceWrites = 0;
                load = async () => { Assert.Equal(session.Manifest.ProjectId, (await resourceStore.LoadAsync()).ProjectId); };
                save = async () => { var value = await resourceStore.LoadAsync(); value.Context = "Context synthetic " + ++resourceWrites; await resourceStore.SaveAsync(value); }; break;
            case "preset":
                path = session.Resolve(session.Manifest.Files.PromptPresets); var presetStore = new PromptPresetStore(path);
                load = async () => { Assert.Single((await presetStore.LoadAsync()).Presets); };
                save = () => presetStore.SaveAsync(new QuickTranslator.AiEditor.Core.Translation.PresetLibrary()); break;
            case "chapter":
                var chapter = QuickTranslator.AiEditor.Core.Translation.TranslationService.CreateChapter("Chương tổng hợp", "你好。");
                path = session.ChapterStore.GetPath(chapter.Id);
                load = async () => { Assert.Equal(chapter.OriginalText, (await session.ChapterStore.LoadAsync(path)).Chapter.OriginalText); };
                save = () => session.ChapterStore.SaveAsync(chapter); break;
            case "traditional":
                path = Path.Combine(_root, "preferences.json"); var traditionalStore = new TraditionalEditorPreferencesStore(path);
                var preferences = new TraditionalEditorPreferences { DataDirectory = "SyntheticDictionaryPath" };
                load = async () => { Assert.Equal(preferences.DataDirectory, (await traditionalStore.LoadAsync()).DataDirectory); };
                save = () => traditionalStore.SaveAsync(preferences); break;
            default:
                path = Path.Combine(_root, "app", "library.json"); var libraryStore = new ProjectLibraryStore(path);
                var library = new ProjectLibrary { DefaultProjectsDirectory = "Synthetic library directory" };
                load = async () => { Assert.Equal(library.DefaultProjectsDirectory, (await libraryStore.LoadAsync()).DefaultProjectsDirectory); };
                save = () => libraryStore.SaveAsync(library); break;
        }
        await save(); await save(); return new(path, load, save);
    }
    private sealed record StoreCase(string Path, Func<Task> Load, Func<Task> Save);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
