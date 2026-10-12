using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ChapterMetadataTests
{
    [Fact]
    public async Task RenameWaitsForOlderAutosaveAndLeavesNewestMetadataOnDisk()
    {
        var chapter = TranslationService.CreateChapter("Cũ", "你好。");
        var store = new MemoryStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool first = true;
        store.BeforeSave = async (_, _) => { if (first) { first = false; entered.SetResult(); await release.Task; } };
        var service = new TranslationService(chapter, new FakeClient(), store);
        service.UpdateDraft(chapter.Segments[0].Id, "Nháp");
        var autosave = service.SaveAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rename = service.RenameAsync("Mới", "新名");
        Assert.False(rename.IsCompleted);
        release.SetResult();
        await Task.WhenAll(autosave, rename);
        Assert.Equal("Mới", store.LastSaved!.Title);
        Assert.Equal("新名", store.LastSaved.ChineseTitle);
        Assert.Equal("Nháp", store.LastSaved.Segments[0].UserDraft);
        Assert.False(service.HasUnsavedChanges);
    }

    [Fact]
    public async Task InvalidRenameDoesNotMutateLiveDocument()
    {
        var chapter = TranslationService.CreateChapter("Cũ", "你好。");
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameAsync("  ", ""));
        Assert.Equal("Cũ", service.GetSnapshot().Title);
    }
}
