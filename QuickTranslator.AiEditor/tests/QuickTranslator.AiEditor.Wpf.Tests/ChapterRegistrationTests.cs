using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ChapterRegistrationTests
{
    [Fact]
    public Task F03_GlossaryCreationRegistersChapterBeforeAnySave() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Text("ChapterInput").Text = "你好。新词出现了。";
        fixture.Call("SetGlossaryTerms", (object)new[] { new ConfirmedTerm("新词", "Từ mới") });
        await fixture.TaskCall("PersistCurrentGlossaryAsync");
        var chapter = fixture.Service.GetSnapshot();
        Assert.Equal(fixture.Project.Manifest.ProjectId, chapter.ProjectId);
        Assert.Equal(chapter.Id, fixture.Project.Manifest.Chapters.Single().ChapterId);
        var reopened = await fixture.Repository.OpenAsync(fixture.Project.ManifestPath);
        var loaded = await fixture.Repository.LoadChapterAsync(reopened.Session, chapter.Id);
        Assert.Equal("Từ mới", loaded.Chapter.ConfirmedTerms.Single(t => t.Chinese == "新词").Vietnamese);
    });

    [Fact]
    public Task F03_EditorCannotAttachUnregisteredProjectChapter() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var unregistered = TranslationService.CreateChapter("Mồ côi", "你好。");
        Assert.Throws<InvalidOperationException>(() => fixture.Call("AttachChapter", unregistered));
        Assert.Empty(fixture.Project.ChapterStore.EnumerateChapterPaths());
    });
}
