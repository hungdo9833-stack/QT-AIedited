using System.IO;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Tests;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class SafeSwitchingTests
{
    [Fact]
    public Task F04_FirstProjectOpenDoesNotTryToSaveUnusedEmptyStandaloneCharacterForm() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        var target = await fixture.Repository.CreateAsync(Path.Combine(fixture.Root, "FirstProject"), "Dự án đầu tiên");
        fixture.Text("CharacterProjectNameInput").Text = "";
        await fixture.TaskCall("OpenProjectPathAsync", target.ManifestPath);
        var opened = fixture.Get<QuickTranslator.AiEditor.Core.Projects.ProjectSession?>("_projectSession");
        Assert.True(opened is not null,
            ((System.Windows.Controls.TextBlock)fixture.Window.FindName("StatusText")).Text);
        Assert.Equal(target.Manifest.ProjectId, opened.Manifest.ProjectId);
    });

    [Fact]
    public Task F04_InvalidTargetTranslationSettingsMustNotReplaceLiveContext() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        fixture.Attach(first);
        var service = fixture.Service;
        var target = await fixture.Repository.CreateAsync(Path.Combine(fixture.Root, "BadSettings"), "Đích lỗi");
        // Inject damaged legacy bytes directly: the store now rejects invalid settings before writing.
        await File.WriteAllTextAsync(target.Resolve(target.Manifest.Files.TranslationSettings),
            System.Text.Json.JsonSerializer.Serialize(new QuickTranslator.AiEditor.Core.Projects.ProjectTranslationSettings
                { Model = new() { ContextSize = 0 } }, DocumentJson.Options));
        await fixture.TaskCall("OpenProjectPathAsync", target.ManifestPath);
        Assert.Same(service, fixture.Service);
    });

    [Fact]
    public Task F04_AcceptingSaveRegistersUnsegmentedInputBeforeSwitch() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var target = await fixture.AddChapterAsync("Đích", 1);
        const string source = "你好。Nguyên văn chưa chia.";
        fixture.Text("ChapterInput").Text = source;
        await fixture.TaskCall("LoadProjectChapterAsync", target.Id);
        Assert.Equal(target.Id, fixture.Service.GetSnapshot().Id);
        var preserved = fixture.Project.Manifest.Chapters.Single(x => x.ChapterId != target.Id);
        Assert.Equal(source, (await fixture.Repository.LoadChapterAsync(fixture.Project, preserved.ChapterId)).Chapter.OriginalText);
    });

    [Fact]
    public Task F04_InvalidTargetResourcesKeepOriginalProjectAndService() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        fixture.Attach(first);
        var service = fixture.Service;
        var target = await fixture.Repository.CreateAsync(Path.Combine(fixture.Root, "BadResources"), "Đích lỗi");
        await File.WriteAllTextAsync(target.Resolve(target.Manifest.Files.Resources), "{broken");
        await fixture.TaskCall("OpenProjectPathAsync", target.ManifestPath);
        Assert.Same(service, fixture.Service);
        Assert.Same(fixture.Project, fixture.Get<QuickTranslator.AiEditor.Core.Projects.ProjectSession>("_projectSession"));
    });

    [Fact]
    public Task F04_ProductionIndividualHandlerCompletesBeforeSwitchCommit() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        fixture.Attach(first);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Respond = async (_, _) => { entered.SetResult(); await release.Task; return FakeClient.Completion("Bản dịch một."); };
        fixture.Call("OnTranslate", fixture.Window, new System.Windows.RoutedEventArgs());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var switching = fixture.TaskCall("LoadProjectChapterAsync", second.Id);
        Assert.True(fixture.Get<bool>("_uiOperation"));
        release.SetResult();
        await switching;
        Assert.Equal(second.Id, fixture.Service.GetSnapshot().Id);
        Assert.False(fixture.Get<bool>("_busy"));
        Assert.Null(fixture.Get<Task?>("_activeWorkCompletion"));
        Assert.DoesNotContain("Lỗi:", ((System.Windows.Controls.TextBlock)fixture.Window.FindName("StatusText")).Text);
        Assert.Single((await fixture.Repository.LoadChapterAsync(fixture.Project, first.Id)).Chapter.Segments[0].Attempts);
    });

    [Fact]
    public Task F04_ProductionQueueHandlerPausesAndPreservesCheckpointBeforeSwitch() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = TranslationService.CreateChapter("Một", string.Concat(Enumerable.Repeat("你好。", 30)),
            new() { MinimumHanCharacters = 10, TargetHanCharacters = 12, MaximumHanCharacters = 16 });
        await fixture.Repository.AddChapterAsync(fixture.Project, first, 1);
        first = (await fixture.Repository.LoadChapterAsync(fixture.Project, first.Id)).Chapter;
        var second = await fixture.AddChapterAsync("Hai", 2);
        fixture.Attach(first);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Respond = async (_, _) => { entered.TrySetResult(); await release.Task; return FakeClient.Completion("Bản dịch một."); };
        fixture.Call("OnTranslateAll", fixture.Window, new System.Windows.RoutedEventArgs());
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Service.UpdateDraft(first.Segments[1].Id, "Nháp bảo vệ.");
        var switching = fixture.TaskCall("LoadProjectChapterAsync", second.Id);
        release.SetResult();
        await switching;
        var saved = (await fixture.Repository.LoadChapterAsync(fixture.Project, first.Id)).Chapter;
        Assert.Equal(QuickTranslator.AiEditor.Core.Models.QueueStatus.Paused, saved.Queue.Status);
        Assert.Equal(1, fixture.Client.Calls);
        Assert.Equal("Nháp bảo vệ.", saved.Segments[1].UserDraft);
        Assert.NotEmpty(saved.Queue.CandidateIds);
        Assert.Equal(second.Id, fixture.Service.GetSnapshot().Id);
        Assert.False(fixture.Get<bool>("_uiOperation"));
    });

    [Fact]
    public Task F04_DecliningUnsegmentedSourceSaveKeepsInputAndContext() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var target = await fixture.AddChapterAsync("Đích", 1);
        fixture.Text("ChapterInput").Text = "Nguyên văn chưa chia 你好。";
        int prompts = 0;
        fixture.Window.WorkspaceConfirmation = (_, _) => { prompts++; return false; };
        await fixture.TaskCall("LoadProjectChapterAsync", target.Id);
        Assert.Equal(1, prompts);
        Assert.Equal("Nguyên văn chưa chia 你好。", fixture.Text("ChapterInput").Text);
        Assert.Null(fixture.Get<TranslationService?>("_service"));
    });

    [Fact]
    public Task F04_InvalidTargetProjectDoesNotDetachCurrentChapter() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Hiện tại", 1);
        fixture.Attach(chapter);
        var original = fixture.Service;
        original.UpdateDraft(chapter.Segments[0].Id, "Nháp giữ lại");
        string bad = Path.Combine(fixture.Root, "bad.qtproj");
        await File.WriteAllTextAsync(bad, "{broken");
        await fixture.TaskCall("OpenProjectPathAsync", bad);
        Assert.Same(original, fixture.Get<TranslationService?>("_service"));
        Assert.Equal("Nháp giữ lại", fixture.Service.GetSnapshot().Segments[0].UserDraft);
    });

    [Fact]
    public Task F04_SwitchWaitsForIndividualRequestAndKeepsResponseInOriginalChapter() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        fixture.Attach(first);
        var original = fixture.Service;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Respond = async (_, _) => { entered.SetResult(); await release.Task; return FakeClient.Completion("Kết quả cho chương một."); };
        var request = original.TranslateAsync(first.Segments[0].Id);
        fixture.Set("_busy", true); fixture.Set("_activeRequest", request);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var switching = fixture.TaskCall("LoadProjectChapterAsync", second.Id);
        try
        {
            await Task.Delay(50);
            Assert.False(switching.IsCompleted);
            Assert.Same(original, fixture.Get<TranslationService>("_service"));
        }
        finally { release.TrySetResult(); await request; await switching; fixture.Set("_busy", false); }
        Assert.Equal(second.Id, fixture.Service.GetSnapshot().Id);
        var savedFirst = (await fixture.Repository.LoadChapterAsync(fixture.Project, first.Id)).Chapter;
        var savedSecond = (await fixture.Repository.LoadChapterAsync(fixture.Project, second.Id)).Chapter;
        Assert.Equal("Kết quả cho chương một.", savedFirst.Segments[0].Attempts.Single().AiText);
        Assert.Empty(savedSecond.Segments[0].Attempts);
    });

    [Fact]
    public Task F04_OverlappingSwitchIsRejectedWhileFirstSaveIsPending() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        var third = await fixture.AddChapterAsync("Ba", 3);
        fixture.Attach(first);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new MemoryStore { BeforeSave = async (_, _) => { entered.TrySetResult(); await release.Task; } };
        fixture.Set("_service", new TranslationService(first, fixture.Client, store));
        var switchOne = fixture.TaskCall("LoadProjectChapterAsync", second.Id);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var switchTwo = fixture.TaskCall("LoadProjectChapterAsync", third.Id);
        try
        {
            Assert.True(fixture.Get<bool>("_uiOperation"));
            Assert.True(switchTwo.IsCompleted);
        }
        finally { release.TrySetResult(); await Task.WhenAll(switchOne, switchTwo); }
        Assert.Equal(second.Id, fixture.Service.GetSnapshot().Id);
    });

    [Fact]
    public Task F04_SaveFailureRetainsLiveDraftAndBlocksTargetCommit() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var first = await fixture.AddChapterAsync("Một", 1);
        var second = await fixture.AddChapterAsync("Hai", 2);
        fixture.Attach(first);
        var store = new MemoryStore { BeforeSave = (_, _) => throw new IOException("Lỗi ghi tổng hợp") };
        var original = new TranslationService(first, fixture.Client, store);
        original.UpdateDraft(first.Segments[0].Id, "Nháp chưa lưu");
        fixture.Set("_service", original);
        await Assert.ThrowsAsync<IOException>(() => fixture.TaskCall("LoadProjectChapterAsync", second.Id));
        Assert.Same(original, fixture.Service);
        Assert.Equal("Nháp chưa lưu", fixture.Service.GetSnapshot().Segments[0].UserDraft);
    });
}
