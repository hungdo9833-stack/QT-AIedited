using System.IO;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ProjectOwnershipTests
{
    [Fact]
    public Task F01_AttachingOldChapterMustNotReplaceCurrentProjectCharacters() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var old = await fixture.AddChapterAsync("Chương cũ", 1);
        await SeedCurrentResources(fixture);
        await fixture.TaskCall("LoadProjectAssetsAsync");
        fixture.Attach(old);
        Assert.Equal("Vương Tú Lan mới", fixture.Get<CharacterProject>("_characterProject").Profiles.Single().VietnameseName);
    });

    [Fact]
    public Task F01_LoadThenSaveOldChapterMustNotDowngradeProjectGlossaryAndContext() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var old = await fixture.AddChapterAsync("Chương cũ", 1);
        await SeedCurrentResources(fixture);
        await fixture.TaskCall("LoadProjectAssetsAsync");
        fixture.Attach(old);
        Assert.Contains("Ánh xạ mới", fixture.Text("TermsInput").Text);
        Assert.Equal("Ngữ cảnh mới", fixture.Text("ContextNotesInput").Text);
        await fixture.TaskCall("SaveProjectAssetsAsync");
        await fixture.TaskCall("LoadProjectAssetsAsync");
        Assert.Contains("Ánh xạ mới", fixture.Text("TermsInput").Text);
        Assert.Equal("Ngữ cảnh mới", fixture.Text("ContextNotesInput").Text);
        // Opening must not rewrite historical chapter snapshots.
        var disk = (await fixture.Repository.LoadChapterAsync(fixture.Project, old.Id)).Chapter;
        Assert.Equal("Ngữ cảnh cũ", disk.ConfirmedContextNotes);
    });

    [Fact]
    public Task F01_UnsavedResourceEditsSurviveChapterSwitch() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var next = await fixture.AddChapterAsync("Hai", 2);
        fixture.Attach(first);
        fixture.Call("SetGlossaryTerms", (object)new[] { new ConfirmedTerm("新词", "Từ vừa sửa") });
        fixture.Text("ContextNotesInput").Text = "Ghi chú vừa sửa";
        await fixture.TaskCall("LoadProjectChapterAsync", next.Id);
        Assert.Contains("Từ vừa sửa", fixture.Text("TermsInput").Text);
        Assert.Equal("Ghi chú vừa sửa", fixture.Text("ContextNotesInput").Text);
        await fixture.TaskCall("LoadProjectAssetsAsync");
        Assert.Equal("Ghi chú vừa sửa", fixture.Text("ContextNotesInput").Text);
    });

    [Fact]
    public Task Prompt_presets_are_shared_across_projects_but_each_project_keeps_its_selection() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ProjectSession first = fixture.Project;
        var manager = fixture.Get<PromptPresetManager>("_presetManager");
        PromptPreset custom = manager.Create("Dùng chung cho mọi truyện", "Dịch đúng: {{source_text}}");
        fixture.Call("RefreshPresetEditor");
        await fixture.TaskCall("SavePresetEditorAsync");
        await fixture.TaskCall("SaveProjectPresetSelectionAsync");

        ProjectSession second = await fixture.Repository.CreateAsync(
            Path.Combine(fixture.Root, "Novel-B"), "Truyện B");
        fixture.Set("_projectSession", second);
        await fixture.TaskCall("LoadProjectAssetsAsync");
        var secondManager = fixture.Get<PromptPresetManager>("_presetManager");
        Assert.Contains(secondManager.Presets, p => p.Id == custom.Id);
        Assert.Equal(PromptPreset.Default.Id, secondManager.Selected.Id);
        await fixture.TaskCall("SaveProjectPresetSelectionAsync");

        fixture.Set("_projectSession", first);
        await fixture.TaskCall("LoadProjectAssetsAsync");
        var firstManager = fixture.Get<PromptPresetManager>("_presetManager");
        Assert.Equal(custom.Id, firstManager.Selected.Id);
        fixture.Call("SelectChapterPreset", PromptPreset.Default);
        Assert.Equal(custom.Id, fixture.Get<PromptPresetManager>("_presetManager").Selected.Id);
        var environment = await fixture.TaskCall<ProjectTranslationEnvironment>(
            "LoadProjectTranslationEnvironmentAsync", first, CancellationToken.None);
        Assert.Equal(custom.Id, environment.PromptPreset.Id);

        fixture.Set("_projectSession", second);
        await fixture.TaskCall("LoadProjectAssetsAsync");
        Assert.Equal(PromptPreset.Default.Id, fixture.Get<PromptPresetManager>("_presetManager").Selected.Id);
        Assert.False(File.Exists(Path.Combine(first.RootDirectory, "Settings", "prompts", "presets.qtaip.json")));
        Assert.False(File.Exists(Path.Combine(second.RootDirectory, "Settings", "prompts", "presets.qtaip.json")));
    });

    private static async Task SeedCurrentResources(WorkspaceFixture fixture)
    {
        var characters = new CharacterProject { Id = fixture.Project.Manifest.ProjectId, Name = "Hồ sơ mới",
            Profiles = [new() { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan mới", NormalizationEnabled = false }] };
        var store = new ProjectResourceStore(fixture.Project);
        ProjectResources resources = await store.LoadAsync();
        resources.Characters = characters;
        resources.Terms.Add(new("新词", "Ánh xạ mới"));
        resources.Context = "Ngữ cảnh mới";
        await store.SaveAsync(resources);
    }
}
