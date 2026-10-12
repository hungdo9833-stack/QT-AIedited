using System.IO;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Reading;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class RecoveryUiTests
{
    [Fact]
    public Task F08_FailedReaderInitializationCannotSaveDefaults() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        string path = Path.Combine(fixture.Root, "reader.json");
        File.WriteAllText(path, "{bad"); File.WriteAllText(path + ".bak", "{bad backup");
        fixture.Set("_readerStore", new ReaderPreferencesStore(path));
        fixture.Call("InitializeReader");
        await UntilAsync(() => ((TextBlock)fixture.Window.FindName("StatusText")).Text.StartsWith("Lỗi:"));
        Assert.False(fixture.Get<bool>("_readerReady"));
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.TaskCall("SaveReaderPreferencesAsync"));
        Assert.Equal("{bad", File.ReadAllText(path)); Assert.Equal("{bad backup", File.ReadAllText(path + ".bak"));
    });

    [Theory]
    [InlineData("manifest")]
    [InlineData("reader")]
    public Task F08_DeclinedRecoveryDoesNotReplaceTargetOrLiveContext(string kind) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Attach(await fixture.AddChapterAsync("Nguồn", 1));
        var source = fixture.Service;
        var target = await fixture.Repository.CreateAsync(Path.Combine(fixture.Root, "Target"), "Đích cần đối chiếu");
        string path;
        if (kind == "manifest") { await fixture.Repository.SaveManifestAsync(target); path = target.ManifestPath; }
        else
        {
            path = target.Resolve(target.Manifest.Files.ReaderSettings);
            var store = new ReaderPreferencesStore(path);
            await store.SaveAsync(new ReaderPreferences()); await store.SaveAsync(new ReaderPreferences { FontFamily = "Reader tổng hợp" });
        }
        byte[] backup = File.ReadAllBytes(path + ".bak");
        File.WriteAllText(path, "{damaged target");
        fixture.Window.WorkspaceConfirmation = (_, _) => false;
        await fixture.TaskCall("OpenProjectPathAsync", target.ManifestPath);
        Assert.Same(source, fixture.Service);
        Assert.Equal("{damaged target", File.ReadAllText(path));
        Assert.Equal(backup, File.ReadAllBytes(path + ".bak"));
    });

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("WPF recovery did not finish."); await Task.Delay(10); }
    }
}
