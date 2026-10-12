using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class RenameConsistencyTests
{
    [Fact]
    public Task F02_RenameUpdatesOpenChapterAndSurvivesLaterSave() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Tên cũ", 1);
        fixture.Attach(chapter);
        await fixture.Window.RenameProjectChapterAsync(chapter.Id, "Tên mới", "新名");
        Assert.Equal("Tên mới", fixture.Service.GetSnapshot().Title);
        Assert.Equal("Tên mới", fixture.Text("TitleInput").Text);
        await fixture.TaskCall("SaveCurrentAsync");
        var disk = (await fixture.Repository.LoadChapterAsync(fixture.Project, chapter.Id)).Chapter;
        Assert.Equal("Tên mới", disk.Title);
        Assert.Equal("新名", disk.ChineseTitle);
        var reopened = await fixture.Repository.OpenAsync(fixture.Project.ManifestPath);
        Assert.Equal("Tên mới", reopened.Session.Manifest.Chapters.Single().VietnameseTitle);
    });

    [Fact]
    public Task F02_RenameCommitsPendingDraftBeforeUpdatingMetadata() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Tên cũ", 1);
        fixture.Attach(chapter);
        fixture.Service.UpdateDraft(chapter.Segments[0].Id, "Nháp chưa autosave");
        await fixture.Window.RenameProjectChapterAsync(chapter.Id, "Tên mới", "");
        var disk = (await fixture.Repository.LoadChapterAsync(fixture.Project, chapter.Id)).Chapter;
        Assert.Equal("Nháp chưa autosave", disk.Segments[0].UserDraft);
        Assert.Equal(fixture.Service.GetSnapshot().Segments[0].DraftRevision, disk.Segments[0].DraftRevision);
        Assert.Equal("Tên mới", fixture.Service.GetSnapshot().Title);
    });
}
