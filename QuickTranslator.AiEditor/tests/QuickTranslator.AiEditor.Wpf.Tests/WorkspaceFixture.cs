using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Tests;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace QuickTranslator.AiEditor.Wpf.Tests;

internal static class StaHost
{
    private static readonly Task<Dispatcher> Ready = Start();
    private static Task<Dispatcher> Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
                App.SuppressStartup = true; // Never run real startup: it opens the user's real stores/Project.
                var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent(); // No Startup/Run: never opens a real project or window.
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true, Name = "Isolated WPF regression tests" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    public static async Task Run(Func<Task> test)
    {
        var dispatcher = await Ready.WaitAsync(TimeSpan.FromSeconds(15));
        await dispatcher.InvokeAsync(test).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30));
    }
}

internal sealed class WorkspaceFixture : IDisposable
{
    private readonly bool _deleteRoot;
    public string Root { get; }
    public MainWindow Window { get; }
    public GpuBackendManager BackendManager { get; }
    public ProjectRepository Repository { get; } = new();
    public FakeClient Client { get; } = new();
    public ProjectSession Project { get; private set; } = null!;
    public TranslationService Service => Get<TranslationService>("_service");

    public WorkspaceFixture(string? root = null, GpuBackendManager? backendManager = null,
        GpuConnectionManager? gpuConnections = null, bool deleteRoot = true)
    {
        Root = root ?? Path.Combine(Path.GetTempPath(), "qtaie-safety-wpf-" + Guid.NewGuid().ToString("N"));
        _deleteRoot = deleteRoot;
        Directory.CreateDirectory(Root);
        BackendManager = backendManager ?? new GpuBackendManager(
            new GpuProfileStore(Path.Combine(Root, "app", "ollama-profiles.json"), new TestProtector()));
        Window = new MainWindow(null, initializeApplication: false, BackendManager, gpuConnections)
            { WorkspaceConfirmation = (_, _) => true };
        Set("_store", new ChapterFileStore(Path.Combine(Root, "Standalone")));
        Set("_presetStore", new PromptPresetStore(Path.Combine(Root, "app", "presets.qtaip.json")));
        Set("_readerStore", new ReaderPreferencesStore(Path.Combine(Root, "app", "reader.json")));
        Set("_characterStore", new CharacterProjectStore(Path.Combine(Root, "app", "Characters")));
        Set("_projectLibraryStore", new ProjectLibraryStore(Path.Combine(Root, "app", "library.json")));
        Set("_presetsReady", true);
        Set("_translationClient", Client);
        Call("RefreshPresetEditor");
        Call("LoadCharacterProject", new CharacterProject { Name = "Hồ sơ thử" });
    }

    public async Task CreateProjectAsync()
    {
        Project = await Repository.CreateAsync(Path.Combine(Root, "Novel"), "Truyện thử tổng hợp");
        await AttachProjectAsync();
    }

    public async Task OpenProjectAsync()
    {
        string manifestPath = Directory.GetFiles(Path.Combine(Root, "Novel"), "*.qtproj",
            SearchOption.TopDirectoryOnly).Single();
        Project = (await Repository.OpenAsync(manifestPath)).Session;
        await AttachProjectAsync();
    }

    private async Task AttachProjectAsync()
    {
        Set("_projectSession", Project);
        Set("_store", Project.ChapterStore);
        // Keep the isolated app-level preset store across project switches.
        Set("_readerStore", new ReaderPreferencesStore(Project.Resolve(Project.Manifest.Files.ReaderSettings)));
        Set("_characterStore", new CharacterProjectStore(Path.Combine(Project.RootDirectory, "Characters")));
        await TaskCall("LoadProjectAssetsAsync");
        Set("_readerReady", false); // Reader visual layout is outside these data tests.
    }

    public async Task<ChapterDocument> AddChapterAsync(string title, int number)
    {
        var chapter = TranslationService.CreateChapter(title, "你好。王秀兰来了。");
        chapter.CharacterProject = new() { Id = Project.Manifest.ProjectId, Name = "Hồ sơ cũ" };
        chapter.ConfirmedTerms = [new("王秀兰", "Tên cũ")];
        chapter.ConfirmedContextNotes = "Ngữ cảnh cũ";
        await Repository.AddChapterAsync(Project, chapter, number);
        return (await Repository.LoadChapterAsync(Project, chapter.Id)).Chapter;
    }

    public void Attach(ChapterDocument chapter)
    {
        Call("AttachChapter", chapter);
        Set("_selectedId", chapter.SelectedSegmentId);
        Call("ApplyInputs", chapter);
    }

    public T Get<T>(string field) => (T)typeof(MainWindow).GetField(field,
        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Window)!;
    public void Set(string field, object? value) => typeof(MainWindow).GetField(field,
        BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Window, value);
    public TextBox Text(string name) => (TextBox)Window.FindName(name);
    public object? Call(string method, params object?[] arguments)
    {
        try { return typeof(MainWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Window, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw(); throw; }
    }
    public Task TaskCall(string method, params object?[] arguments) => (Task)Call(method, arguments)!;
    public async Task<T> TaskCall<T>(string method, params object?[] arguments) =>
        await (Task<T>)Call(method, arguments)!;

    public void Dispose()
    {
        Get<DispatcherTimer>("_autosave").Stop();
        Get<DispatcherTimer>("_progressTimer").Stop();
        Get<DispatcherTimer>("_readerSaveTimer").Stop();
        Set("_allowClose", true);
        Window.Close();
        string resolved = Path.GetFullPath(Root);
        if (!resolved.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(resolved).StartsWith("qtaie-safety-wpf-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean outside the synthetic test root.");
        if (_deleteRoot) Directory.Delete(resolved, recursive: true);
    }

    private sealed class TestProtector : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }
}

public sealed class WorkspaceHarnessTests
{
    [Fact]
    public Task ConstructsControlsAndSyntheticProjectWithoutOpeningUserData() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var chapter = await fixture.AddChapterAsync("Một", 1);
        fixture.Attach(chapter);
        fixture.Service.UpdateDraft(chapter.Segments[0].Id, "Nháp tổng hợp.");
        await fixture.TaskCall("SaveCurrentAsync");
        var loaded = await fixture.Repository.LoadChapterAsync(fixture.Project, chapter.Id);
        Assert.Equal("Nháp tổng hợp.", loaded.Chapter.Segments[0].UserDraft);
        Assert.Equal(0, fixture.Client.Calls);
    });

    [Fact]
    public Task Opening_projects_and_switching_chapters_does_not_overwrite_runtime_inputs() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Text("TemperatureInput").Text = "0.27";
        fixture.Text("ContextInput").Text = "8192";
        fixture.Text("OutputTokensInput").Text = "2048";
        fixture.Text("TimeoutInput").Text = "420";
        fixture.Text("MinimumInput").Text = "350";
        fixture.Text("TargetInput").Text = "550";
        fixture.Text("MaximumInput").Text = "750";
        await fixture.CreateProjectAsync();

        for (int number = 1; number <= 50; number++)
        {
            ChapterDocument chapter = await fixture.AddChapterAsync("Chương " + number, number);
            chapter.DefaultModelOptions = new ModelOptions { Model = "historical-" + number,
                ContextSize = 4096, MaximumOutputTokens = 1024 };
            chapter.SegmentationOptions = new SegmenterOptions { MinimumHanCharacters = 10,
                TargetHanCharacters = 20, MaximumHanCharacters = 30, MaximumTextElements = 2000 };
            fixture.Attach(chapter);
        }

        Assert.Equal("0.27", fixture.Text("TemperatureInput").Text);
        Assert.Equal("8192", fixture.Text("ContextInput").Text);
        Assert.Equal("2048", fixture.Text("OutputTokensInput").Text);
        Assert.Equal("420", fixture.Text("TimeoutInput").Text);
        Assert.Equal("350", fixture.Text("MinimumInput").Text);
        Assert.Equal("550", fixture.Text("TargetInput").Text);
        Assert.Equal("750", fixture.Text("MaximumInput").Text);
    });
}
