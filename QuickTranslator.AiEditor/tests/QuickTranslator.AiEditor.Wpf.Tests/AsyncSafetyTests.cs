using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Tests;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class AsyncSafetyTests
{
    [Fact]
    public Task F12_ReaderSaveFailureMustReachCallerSoContextCannotClaimSuccess() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        BlockReaderStore(fixture);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.TaskCall("SaveReaderPreferencesAsync"));
    });

    [Fact]
    public Task F12_ProjectAssetsSaveMustAbortOnReaderWriteFailure() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        BlockReaderStore(fixture);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.TaskCall("SaveProjectAssetsAsync"));
        Assert.False(fixture.Get<bool>("_allowClose"));
    });

    [Fact]
    public Task F12_FailedAssetSaveBlocksChapterSwitchAndReleasesOperationForRetry() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        fixture.Attach(first);
        fixture.Service.UpdateDraft(first.Segments[0].Id, "Nháp chưa lưu khi chuyển.");
        BlockReaderStore(fixture);
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.TaskCall("LoadProjectChapterAsync", second.Id));
        Assert.Equal(first.Id, fixture.Service.GetSnapshot().Id);
        Assert.Equal("Nháp chưa lưu khi chuyển.", fixture.Service.GetSnapshot().Segments[0].UserDraft);
        Assert.False(fixture.Get<bool>("_uiOperation"));
        fixture.Set("_readerStore", new ReaderPreferencesStore(Path.Combine(fixture.Root, "Retry", "reader.json")));
        await fixture.TaskCall("LoadProjectChapterAsync", second.Id);
        Assert.Equal(second.Id, fixture.Service.GetSnapshot().Id);
    });

    [Fact]
    public Task F12_BatchKeepHandlerCatchesStoreFailureAtSourceAndPreventsDuplicateRequests() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        var chapter = TranslationService.CreateChapter("Batch tổng hợp", "王秀兰来了。");
        chapter.Segments[0].Attempts.Add(new() { Outcome = AttemptOutcome.Completed, AiText = "王秀兰 đến.", Prompt = "Synthetic", PromptVersion = "test" });
        chapter.Segments[0].Status = SegmentStatus.AwaitingReview;
        fixture.Attach(chapter);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        var store = new MemoryStore { BeforeSave = async (_, _) =>
        {
            writes++; entered.TrySetResult(); await release.Task;
            throw new IOException("Injected keep failure");
        } };
        fixture.Set("_service", new TranslationService(chapter, fixture.Client, store));
        var unhandled = new List<Exception>();
        DispatcherUnhandledExceptionEventHandler trap = (_, args) => { unhandled.Add(args.Exception); args.Handled = true; };
        fixture.Window.Dispatcher.UnhandledException += trap;
        Window? dialog = null;
        Task? showing = null;
        try
        {
            fixture.Window.ShowInTaskbar = false; fixture.Window.Left = -30000;
            fixture.Window.Show(); // Actual WPF window, all stores redirected to synthetic roots.
            showing = fixture.Window.Dispatcher.InvokeAsync(() => fixture.Call("ShowTextCleanerWindow", true)).Task;
            await UntilAsync(() => fixture.Window.OwnedWindows.Count > 0);
            dialog = fixture.Window.OwnedWindows.Cast<Window>().Single();
            await UntilAsync(() => Descendants<DataGrid>(dialog).Any(x => x.IsReadOnly));
            var chinese = Descendants<DataGrid>(dialog).Single(x => x.IsReadOnly);
            await UntilAsync(() => chinese.Items.Count > 0);
            chinese.SelectedIndex = 0;
            var keep = Descendants<Button>(dialog).Single(x => Equals(x.Content, "Đánh dấu giữ nguyên"));
            keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // Dispatch again even if disabled.
            release.TrySetResult();
            await UntilAsync(() => unhandled.Count > 0 || Descendants<TextBlock>(dialog).Any(x => x.Text.Contains("Injected keep failure")));
            Assert.Empty(unhandled);
            Assert.Equal(1, writes);
            Assert.True(keep.IsEnabled);
            Assert.True(fixture.Service.HasUnsavedChanges);
        }
        finally
        {
            release.TrySetResult();
            if (dialog is not null) dialog.Close();
            if (showing is not null) await showing;
            fixture.Window.Dispatcher.UnhandledException -= trap;
        }
    });

    [Theory]
    [InlineData("OnPreviousProjectChapter")]
    [InlineData("OnNextProjectChapter")]
    public Task F12_NavigationEventReportsSaveFailureAndRetainsDraft(string handler) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        var chapter = handler.Contains("Previous") ? second : first;
        fixture.Attach(chapter);
        await fixture.TaskCall("SaveCurrentAsync");
        var store = new MemoryStore { BeforeSave = (_, _) => throw new IOException("Injected navigation save failure") };
        fixture.Set("_service", new TranslationService(chapter, fixture.Client, store));
        fixture.Service.UpdateDraft(chapter.Segments[0].Id, "Nháp điều hướng chưa lưu.");
        fixture.Call(handler, fixture.Window, new RoutedEventArgs());
        await UntilAsync(() => Status(fixture).Contains("Injected navigation save failure") && !fixture.Get<bool>("_uiOperation"));
        Assert.Equal(chapter.Id, fixture.Service.GetSnapshot().Id);
        Assert.Equal("Nháp điều hướng chưa lưu.", fixture.Service.GetSnapshot().Segments[0].UserDraft);
        Assert.True(fixture.Service.HasUnsavedChanges);
        Assert.StartsWith("Lỗi:", Status(fixture));
    });

    [Theory]
    [InlineData("SaveCharacterProjectAsync")]
    [InlineData("PersistCurrentGlossaryAsync")]
    public Task F12_ResourceWriteFailureRetainsEditorInputsAndCanonicalRevisionForRetry(string method) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Một", 1);
        fixture.Attach(chapter);
        await fixture.TaskCall("SaveProjectResourcesAsync");
        var resources = fixture.Get<QuickTranslator.AiEditor.Core.Projects.ProjectResources>("_projectResources");
        fixture.Get<CharacterProject>("_characterProject").Profiles.Add(new()
        {
            ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan", NormalizationEnabled = false
        });
        fixture.Call("SetGlossaryTerms", (object)new ConfirmedTerm[] { new("王秀兰", "Vương Tú Lan") });
        string path = Path.Combine(fixture.Project.RootDirectory, "Settings", "resources.json");
        string before = File.ReadAllText(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            await Assert.ThrowsAnyAsync<IOException>(() => fixture.TaskCall(method));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(resources.Revision, fixture.Get<QuickTranslator.AiEditor.Core.Projects.ProjectResources>("_projectResources").Revision);
        Assert.Contains(fixture.Get<CharacterProject>("_characterProject").Profiles, x => x.VietnameseName == "Vương Tú Lan");
        await fixture.TaskCall(method);
        var saved = await new QuickTranslator.AiEditor.Core.Projects.ProjectResourceStore(fixture.Project).LoadAsync();
        Assert.Contains(saved.Terms, x => x.Vietnamese == "Vương Tú Lan");
        Assert.Contains(saved.Characters.Profiles, x => x.ChineseName == "王秀兰");
    });

    [Fact]
    public Task F12_ProjectPropertiesEventReportsManifestFailureAndAllowsRetry() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Window.ShowInTaskbar = false; fixture.Window.Left = -30000;
        fixture.Window.Show();
        string before = File.ReadAllText(fixture.Project.ManifestPath);
        const string title = "Thuộc tính tổng hợp chờ lưu";
        using (var locked = new FileStream(fixture.Project.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await SubmitPropertiesAsync(fixture, title);
            await UntilAsync(() => Status(fixture).StartsWith("Lỗi:") && !fixture.Get<bool>("_uiOperation"));
            Assert.Equal(before, File.ReadAllText(fixture.Project.ManifestPath));
            Assert.False(fixture.Window.Title.StartsWith(title));
            Assert.Equal(title, fixture.Project.Manifest.VietnameseTitle); // Pending input retained.
        }
        await SubmitPropertiesAsync(fixture, title);
        await UntilAsync(() => fixture.Window.Title.StartsWith(title) && !fixture.Get<bool>("_uiOperation"));
        Assert.Equal(title, (await fixture.Repository.OpenAsync(fixture.Project.ManifestPath)).Session.Manifest.VietnameseTitle);
    });

    [Theory]
    [InlineData("AddQuickCharacterAsync", "Thêm nhân vật")]
    [InlineData("AddSelectedGlossaryAsync", "Lưu")]
    public Task F12_QuickActionsKeepConfirmedInputsAfterWriteFailure(string method, string button) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Một", 1);
        fixture.Attach(chapter);
        fixture.Call("SetGlossaryTerms", (object)Array.Empty<ConfirmedTerm>());
        await fixture.TaskCall("SaveProjectResourcesAsync");
        fixture.Window.ShowInTaskbar = false; fixture.Window.Left = -30000; fixture.Window.Show();
        var source = fixture.Text("ChapterInput");
        source.Text = "王秀兰"; source.SelectAll();
        Task? action = null;
        var launch = fixture.Window.Dispatcher.InvokeAsync(() => { action = fixture.TaskCall(method, source); }).Task;
        await UntilAsync(() => fixture.Window.OwnedWindows.Count > 0);
        var dialog = fixture.Window.OwnedWindows.Cast<Window>().Single();
        await UntilAsync(() => Descendants<Button>(dialog).Any(x => Equals(x.Content, button)));
        // Both editors place Chinese then Vietnamese before other fields.
        Descendants<TextBox>(dialog).Skip(1).First().Text = "Vương Tú Lan";
        foreach (var check in Descendants<CheckBox>(dialog)) check.IsChecked = false;
        string path = Path.Combine(fixture.Project.RootDirectory, "Settings", "resources.json");
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Descendants<Button>(dialog).Single(x => Equals(x.Content, button)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await launch;
            await action!;
        }
        Assert.StartsWith("Lỗi:", Status(fixture));
        var terms = (IReadOnlyList<ConfirmedTerm>)fixture.Call("ReadTerms")!;
        Assert.Contains(terms, x => x.Vietnamese == "Vương Tú Lan");
        if (method == "AddQuickCharacterAsync")
            Assert.Contains(fixture.Get<CharacterProject>("_characterProject").Profiles, x => x.VietnameseName == "Vương Tú Lan");
        await fixture.TaskCall("SaveCharacterProjectAsync");
        Assert.Contains((await new QuickTranslator.AiEditor.Core.Projects.ProjectResourceStore(fixture.Project).LoadAsync()).Terms,
            x => x.Vietnamese == "Vương Tú Lan");
    });

    private static async Task SubmitPropertiesAsync(WorkspaceFixture fixture, string title)
    {
        var showing = fixture.Window.Dispatcher.InvokeAsync(() => fixture.Call("OnEditProjectProperties", fixture.Window, new RoutedEventArgs())).Task;
        await UntilAsync(() => fixture.Window.OwnedWindows.Count > 0);
        var dialog = fixture.Window.OwnedWindows.Cast<Window>().Single();
        await UntilAsync(() => Descendants<Button>(dialog).Any(x => Equals(x.Content, "Lưu")));
        Descendants<TextBox>(dialog).First().Text = title;
        Descendants<Button>(dialog).Single(x => Equals(x.Content, "Lưu")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await showing;
    }

    private static string Status(WorkspaceFixture fixture) => ((TextBlock)fixture.Window.FindName("StatusText")).Text;

    private static void BlockReaderStore(WorkspaceFixture fixture)
    {
        string blocker = Path.Combine(fixture.Root, "NotADirectory");
        File.WriteAllText(blocker, "Synthetic write obstruction");
        fixture.Set("_readerStore", new ReaderPreferencesStore(Path.Combine(blocker, "reader.json")));
        fixture.Set("_readerReady", true);
    }

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
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Synthetic WPF action did not finish.");
            await Task.Delay(15);
        }
    }
}
