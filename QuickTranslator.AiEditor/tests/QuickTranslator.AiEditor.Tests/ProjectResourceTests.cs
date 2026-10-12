using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectResourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-resource-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LegacyProjectStoresImportWithoutModifyingTheirBytesOrChapterSchema()
    {
        var project = await new ProjectRepository().CreateAsync(_root, "Tổng hợp");
        var legacy = new ProjectSettingsStore(project);
        await legacy.SaveGlossaryAsync(new() { Terms = [new("名字", "Tên mới")] });
        await legacy.SaveContextNotesAsync("Ghi chú");
        var before = await File.ReadAllBytesAsync(project.Resolve(project.Manifest.Files.Glossary));
        var store = new ProjectResourceStore(project);
        var resources = await store.LoadAsync();
        var saved = await store.SaveAsync(resources);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(saved.Revision, (await store.SaveAsync(saved)).Revision);
        Assert.Equal(before, await File.ReadAllBytesAsync(project.Resolve(project.Manifest.Files.Glossary)));
        Assert.Equal(1, project.Manifest.SchemaVersion);
        Assert.Equal(3, TranslationService.CreateChapter("Thử", "你好。").SchemaVersion);
    }

    [Fact]
    public async Task StaleResourceRevisionCannotOverwriteNewerProjectState()
    {
        var project = await new ProjectRepository().CreateAsync(_root, "Tổng hợp");
        var store = new ProjectResourceStore(project);
        var stale = await store.LoadAsync();
        var edit = stale.Clone(); edit.Context = "Mới";
        var current = await store.SaveAsync(edit);
        stale.Context = "Cũ";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(stale));
        var loaded = await store.LoadAsync();
        Assert.Equal(current.Revision, loaded.Revision);
        Assert.Equal("Mới", loaded.Context);
    }

    [Fact]
    public async Task ResourceRecoveryPreservesLastVerifiedBackup()
    {
        var project = await new ProjectRepository().CreateAsync(_root, "Tổng hợp");
        var store = new ProjectResourceStore(project);
        var first = await store.SaveAsync(await store.LoadAsync());
        var second = first.Clone(); second.Context = "Mới";
        await store.SaveAsync(second);
        var backupBytes = await File.ReadAllBytesAsync(store.PathName + ".bak");
        await File.WriteAllTextAsync(store.PathName, "{broken");
        var recovered = await store.LoadAsync();
        Assert.Equal(first.Revision, recovered.Revision);
        await store.SaveAsync(recovered);
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(store.PathName + ".bak"));
    }

    [Fact]
    public async Task TranslationUsesCurrentProjectResourcesAndKeepsOriginalHistoryAndDraft()
    {
        var project = await new ProjectRepository().CreateAsync(_root, "Tổng hợp");
        var chapter = TranslationService.CreateChapter("Thử", "王秀兰来了。");
        chapter.ProjectId = project.Manifest.ProjectId;
        chapter.CharacterProject.Id = project.Manifest.ProjectId;
        chapter.ConfirmedTerms = [new("王秀兰", "Tên cũ")];
        var segment = chapter.Segments[0];
        segment.UserDraft = "Bản sửa đã giữ";
        segment.DraftRevision = 5;
        segment.Attempts.Add(new() { Outcome = AttemptOutcome.Completed, Prompt = "Prompt lịch sử", AiText = "AI cũ" });
        var client = new FakeClient();
        var service = new TranslationService(chapter, client, new MemoryStore());
        var resources = new ProjectResources { ProjectId = project.Manifest.ProjectId,
            Characters = new() { Id = project.Manifest.ProjectId, Name = "Tổng hợp" },
            Terms = [new("王秀兰", "Tên mới")], Context = "Ngữ cảnh mới", Revision = 4 };
        service.UseProjectResources(resources);
        Assert.Equal("Tên cũ", service.GetSnapshot().ConfirmedTerms.Single().Vietnamese);
        await service.TranslateAsync(segment.Id);
        var result = service.GetSnapshot();
        Assert.Contains("Tên mới", client.Prompts.Single());
        Assert.Contains("Ngữ cảnh mới", client.Prompts.Single());
        Assert.Equal("Prompt lịch sử", result.Segments[0].Attempts[0].Prompt);
        Assert.Equal("Bản sửa đã giữ", result.Segments[0].UserDraft);
        Assert.Equal(5, result.Segments[0].DraftRevision);
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("qtaie-resource-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid synthetic test root.");
        if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
    }
}
