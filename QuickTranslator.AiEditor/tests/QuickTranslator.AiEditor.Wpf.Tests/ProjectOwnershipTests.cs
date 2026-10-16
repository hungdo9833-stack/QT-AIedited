using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
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
