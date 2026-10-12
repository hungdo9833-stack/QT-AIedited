using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class PromptPresetTests
{
    [Fact]
    public void DeleteCustomPreset_KeepsDefaultAndSelectsRemainingPreset()
    {
        var manager = new PromptPresetManager();
        var custom = manager.Create("Tạm thời");
        manager.Delete(custom.Id);

        Assert.Equal(PromptPreset.Default.Id, manager.Selected.Id);
        Assert.Throws<InvalidOperationException>(() => manager.Delete(PromptPreset.Default.Id));
    }

    [Fact]
    public void CreateDuplicateUpdateAndSelect_PreserveVersionedTemplates()
    {
        var manager = new PromptPresetManager();
        var first = manager.Create("Cổ trang", "VI: {{source_text}}");
        var copy = manager.Duplicate(first.Id, "Hiện đại");
        Assert.NotEqual(first.Id, copy.Id);
        Assert.Equal(first.Template, copy.Template);
        var updated = manager.Update(copy.Id, "Hiện đại 2", "Translate: {{source_text}}");
        Assert.Equal(2, updated.Revision);
        Assert.Equal("VI: {{source_text}}", manager.Presets.Single(p => p.Id == first.Id).Template);
        manager.Select(first.Id);
        Assert.Equal(first.Id, manager.Selected.Id);
    }

    [Theory]
    [InlineData("Translate nothing")]
    [InlineData("{{source_text}}{{source_text}}")]
    [InlineData("{{source_text}} {{unknown}}")]
    public void InvalidTemplate_IsRejected(string template) =>
        Assert.Throws<ArgumentException>(() => new PromptPreset { Template = template }.Validate());

    [Fact]
    public void PlaceholderTextInsideSource_IsNotInterpretedAgain()
    {
        string source = "甲乙。 {{context}} {{source_text}}";
        string prompt = new TranslationPromptBuilder().Build(source, [], "Reference", new() { Template = "{{source_text}}\n{{context}}" });
        Assert.Equal(source + Environment.NewLine + "Context notes (reference only, do not translate these notes):\nReference", prompt);
    }

    [Fact]
    public async Task SwitchingPresets_KeepsSnapshotsOfPreviousAttempts()
    {
        var chapter = TranslationService.CreateChapter("Thử preset", "你好。");
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        var first = new PromptPreset { Name = "Một", Template = "ONE {{source_text}}" };
        var second = new PromptPreset { Name = "Hai", Template = "TWO {{source_text}}" };
        await service.ConfigureAsync(new(), first);
        await service.TranslateAsync(chapter.Segments[0].Id);
        await service.ConfigureAsync(new(), second);
        await service.TranslateAsync(chapter.Segments[0].Id);
        var attempts = service.GetSnapshot().Segments[0].Attempts;
        Assert.Equal(first, attempts[0].PresetSnapshot);
        Assert.Equal(second, attempts[1].PresetSnapshot);
        Assert.StartsWith("ONE", attempts[0].Prompt);
        Assert.StartsWith("TWO", attempts[1].Prompt);
    }

    [Fact]
    public async Task ConfirmedTermsAndShortApprovedContext_AreIncluded_WithoutWholeChapter()
    {
        var chapter = TranslationService.CreateChapter("Tham khảo", "李明。\r\n张三。\r\n王五。", new() { MinimumHanCharacters = 1, TargetHanCharacters = 2, MaximumHanCharacters = 3 });
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        service.UpdateDraft(chapter.Segments[0].Id, "Đây là lời xưng hô đã duyệt.");
        await service.ApproveAsync(chapter.Segments[0].Id);
        await service.ConfigureAsync(new(), PromptPreset.Default, [new("张三", "Trương Tam")], "Xưng hô: ta — ngươi.");
        string prompt = service.PreviewPrompt(chapter.Segments[1].Id, PromptPreset.Default);
        Assert.Contains("张三 = Trương Tam", prompt);
        Assert.Contains("ta — ngươi", prompt);
        Assert.Contains("lời xưng hô đã duyệt", prompt);
        Assert.DoesNotContain("王五", prompt);
        Assert.Equal(prompt, (await service.TranslateAsync(chapter.Segments[1].Id)).Prompt);
    }

    [Fact]
    public async Task PresetStore_SavesReopens_AndRecoversBackupWithoutDestroyingIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qtaie-presets-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "presets.qtaip.json");
        try
        {
            var manager = new PromptPresetManager();
            var preset = manager.Create("Thử", "Dịch: {{source_text}}");
            var store = new PromptPresetStore(path);
            await store.SaveAsync(manager.Snapshot());
            manager.Update(preset.Id, "Thử mới", "Dịch mới: {{source_text}}");
            await store.SaveAsync(manager.Snapshot());
            Assert.Equal("Thử mới", new PromptPresetManager(await store.LoadAsync()).Selected.Name);
            byte[] backup = await File.ReadAllBytesAsync(path + ".bak");
            await File.WriteAllTextAsync(path, "synthetic damage");
            var recovered = await store.LoadAsync();
            Assert.True(store.RecoveredFromBackup);
            Assert.Equal("Thử", new PromptPresetManager(recovered).Selected.Name);
            await store.SaveAsync(recovered);
            Assert.Equal(backup, await File.ReadAllBytesAsync(path + ".bak"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
