using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
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

    [Fact]
    public Task F07_ActualRecoveryDialogRollsBackPendingTransactionAndExportsRescue() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Chương tổng hợp", 1);
        var seam = typeof(ProjectRepository).GetProperty("FaultPoint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var interruptedType = typeof(ProjectRepository).Assembly.GetType("QuickTranslator.AiEditor.Core.Projects.ProjectOperationInterruptedException")!;
        seam.SetValue(fixture.Repository, (Action<string>)(point =>
        {
            if (point == "BeforeCommit") throw (Exception)Activator.CreateInstance(interruptedType, point)!;
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Repository.SoftDeleteChapterAsync(fixture.Project, chapter.Id));
        Assert.True(fixture.Project.RecoveryReport.RequiresDecision);
        fixture.Window.ShowInTaskbar = false; fixture.Window.Left = -30000; fixture.Window.Show();
        Task? review = null;
        var launch = fixture.Window.Dispatcher.InvokeAsync(() => { review = fixture.TaskCall("ReviewProjectRecoveryAsync", fixture.Project); }).Task;
        await UntilAsync(() => fixture.Window.OwnedWindows.Count > 0);
        var dialog = fixture.Window.OwnedWindows.Cast<Window>().Single(); dialog.Left = -30000;
        try
        {
            await UntilAsync(() => Descendants<Button>(dialog).Any(x => Equals(x.Content, "Rollback transaction đã chọn")));
            Descendants<Button>(dialog).Single(x => Equals(x.Content, "Rollback transaction đã chọn")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => !fixture.Project.RecoveryReport.RequiresDecision && Descendants<Button>(dialog).All(x => x.IsEnabled));
            Descendants<Button>(dialog).Single(x => Equals(x.Content, "Tiếp tục / Đóng")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await launch; await review!;
            Assert.Null(fixture.Project.Manifest.Chapters.Single().DeletedAt);
            Assert.True(File.Exists(fixture.Project.ChapterStore.GetPath(chapter.Id)));
            Assert.NotEmpty(Directory.GetFiles(fixture.Project.Resolve("Backups"), "transaction-rescue-*.zip"));
            Assert.False((await fixture.Repository.OpenAsync(fixture.Project.ManifestPath)).Session.RecoveryReport.RequiresDecision);
        }
        finally { dialog.Close(); }
    });

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("WPF recovery did not finish."); await Task.Delay(10); }
    }
}
