using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.WebImport;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class WebImportWindowTests
{
    [Fact]
    public Task Analyze_shows_list_metadata_warnings_and_project_options() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var fake = new FakeImporter();
        WebImportOptions? options = null;
        fixture.Window.WebNovelImporterFactory = value => { options = value; return fake; };
        var window = fixture.Window.CreateWebImportWindow();
        try
        {
            Text(window, "UrlInput").Text = "https://example.invalid/book";
            await window.AnalyzeAsync();

            Assert.Equal(fake.Analysis.SourceUrl, fake.AnalyzedUrl);
            Assert.Equal("Truyện / thử: web?", Block(window, "BookTitleText").Text);
            Assert.Equal("3 chương", Block(window, "ChapterCountText").Text);
            Assert.Equal("Mục lục thử", Block(window, "DetectionSummaryText").Text);
            Assert.Contains("Kiểm tra chương", Block(window, "WarningsText").Text);
            Assert.Equal(["1 · 第10章 Một", "2 · 第20章 Hai", "3 · 第30章 Ba"],
                ((ListBox)window.FindName("ChapterList")).Items.Cast<string>());
            Assert.Equal("1", Text(window, "FromInput").Text);
            Assert.Equal("3", Text(window, "ToInput").Text);
            Assert.Equal(TimeSpan.FromSeconds(1.5), options!.DelayBetweenRequests);
            Assert.Equal(fixture.Project.Resolve("Cache/web-import/pages"), options.CacheDirectory);
            Assert.True(Directory.Exists(options.CacheDirectory));
            Assert.True(FindButton(window, "PreviewButton").IsEnabled);
            Assert.False(FindButton(window, "CancelButton").IsEnabled);
            Text(window, "DelayInput").Text = "0,5";
            await window.PreviewAsync();
            Assert.Equal(TimeSpan.FromSeconds(0.5), options!.DelayBetweenRequests);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public Task Keep_site_order_defaults_to_conflict_and_passes_user_choice_to_composition(bool conflict, bool selected) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var fake = new FakeImporter { NumberingConflict = conflict };
        fixture.Window.WebImportPreviewOpener = _ => Task.CompletedTask;
        var window = Create(fixture, fake);
        try
        {
            var checkbox = (CheckBox)window.FindName("KeepSiteOrderCheckBox");
            Assert.False(checkbox.IsChecked);
            await window.AnalyzeAsync();
            Assert.Equal(conflict, checkbox.IsChecked);
            Assert.True(checkbox.IsEnabled);
            checkbox.IsChecked = selected;
            await window.DownloadAsync();
            Assert.NotNull(fake.ComposedChapters);
            Assert.Equal(selected, fake.KeepSiteOrder);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Preview_fetches_first_range_position_and_shows_only_thirty_lines() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var fake = new FakeImporter { ChapterText = string.Join("\r\n", Enumerable.Range(1, 40).Select(i => $"Dòng {i}")) };
        var window = Create(fixture, fake);
        try
        {
            await window.AnalyzeAsync();
            Text(window, "FromInput").Text = "2";
            await window.PreviewAsync();

            Assert.Equal(fake.Links[1], fake.PreviewedLink);
            Assert.Equal("第20章 Hai", Block(window, "PreviewTitleText").Text);
            Assert.Contains("2 trang", Block(window, "PreviewPageCountText").Text);
            Assert.True(Text(window, "PreviewText").IsReadOnly);
            Assert.Equal(30, Text(window, "PreviewText").Text.Split(Environment.NewLine).Length);
            Assert.EndsWith("Dòng 30", Text(window, "PreviewText").Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Download_writes_composed_utf8_in_project_cache_and_hands_path_to_existing_preview() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var fake = new FakeImporter();
        string? openedPath = null;
        fixture.Window.WebImportPreviewOpener = async path =>
        {
            var preview = new ImportPreviewWindow(fixture.Project, null, path);
            try
            {
                await preview.LoadSourceAsync(path, SourceTextEncoding.Auto);
                openedPath = preview.SourcePath;
                Assert.Equal(2, preview.CurrentPlan!.ImportableChapters.Count());
            }
            finally { preview.Close(); }
        };
        var window = Create(fixture, fake);
        try
        {
            await window.AnalyzeAsync();
            Text(window, "FromInput").Text = "2";
            Text(window, "DelayInput").Text = "0,5";
            await window.DownloadAsync();

            Assert.Equal(fake.Links.Skip(1), fake.DownloadedLinks);
            Assert.Equal(2, fake.ComposedChapters!.Count);
            Assert.Equal(window.ImportPath, openedPath);
            Assert.NotNull(openedPath);
            Assert.Equal(fixture.Project.Resolve("Cache/web-import"), Path.GetDirectoryName(openedPath));
            Assert.DoesNotContain('?', Path.GetFileName(openedPath));
            Assert.Equal(fake.ComposedText, await File.ReadAllTextAsync(openedPath));
            byte[] bytes = await File.ReadAllBytesAsync(openedPath);
            Assert.False(bytes.Take(3).SequenceEqual(Encoding.UTF8.GetPreamble()));
            Assert.Equal(0, fixture.Project.Manifest.ChapterCount);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(WebImportBlockReason.RequiresBrowser, "Trang cần trình duyệt hoặc xác nhận")]
    [InlineData(WebImportBlockReason.RequiresUserConfirmation, "Trang cần trình duyệt hoặc xác nhận")]
    [InlineData(WebImportBlockReason.BotChallenge, "Trang cần trình duyệt hoặc xác nhận")]
    [InlineData(WebImportBlockReason.NotFound, "Không tìm thấy trang truyện")]
    [InlineData(WebImportBlockReason.NoChapterList, "Không tìm thấy danh sách chương")]
    public Task Blocked_reason_shows_clear_message(WebImportBlockReason reason, string expected) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var fake = new FakeImporter { AnalysisError = new WebImportBlockedException(new Uri("https://example.invalid"), reason, "raw server error") };
        var window = Create(fixture, fake);
        string? message = null;
        ThemedDialog.Responder = dialog => { message = dialog.Message; return MessageBoxResult.OK; };
        try
        {
            await window.AnalyzeAsync();
            Assert.Contains(expected, message);
            Assert.False(FindButton(window, "DownloadButton").IsEnabled);
            Assert.True(FindButton(window, "AnalyzeButton").IsEnabled);
            Assert.Null(window.ImportPath);
        }
        finally { ThemedDialog.Responder = null; window.Close(); }
    });

    [Fact]
    public Task Cancel_download_stops_requests_and_preserves_completed_chapter_in_preview_file() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var fake = new FakeImporter { PauseDownload = true };
        string? openedPath = null;
        fixture.Window.WebImportPreviewOpener = path => { openedPath = path; return Task.CompletedTask; };
        var window = Create(fixture, fake);
        try
        {
            await window.AnalyzeAsync();
            Task download = window.DownloadAsync();
            await fake.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Assert.False(FindButton(window, "AnalyzeButton").IsEnabled);
            Assert.False(FindButton(window, "DownloadButton").IsEnabled);
            Assert.False(((CheckBox)window.FindName("KeepSiteOrderCheckBox")).IsEnabled);
            Assert.Equal("1/3 · 第10章 Một", Block(window, "StatusText").Text);
            Assert.Equal(1, ((ProgressBar)window.FindName("BusyProgress")).Value);
            FindButton(window, "CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await download.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(fake.CancellationObserved);
            Assert.Single(fake.ComposedChapters!);
            Assert.Equal(window.ImportPath, openedPath);
            Assert.Equal(fake.ComposedText, await File.ReadAllTextAsync(openedPath!));
            Assert.Contains("giữ 1 chương", Block(window, "StatusText").Text);
            Assert.True(FindButton(window, "DownloadButton").IsEnabled);
            Assert.True(((CheckBox)window.FindName("KeepSiteOrderCheckBox")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Cancel_analysis_propagates_token_and_restores_commands() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var fake = new FakeImporter { PauseAnalysis = true };
        var window = Create(fixture, fake);
        try
        {
            Task analyze = window.AnalyzeAsync();
            await fake.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.Cancel();
            await analyze.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(fake.CancellationObserved);
            Assert.True(FindButton(window, "AnalyzeButton").IsEnabled);
            Assert.False(FindButton(window, "PreviewButton").IsEnabled);
            Assert.Null(window.ImportPath);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Empty_chapter_or_composed_text_never_opens_import(bool emptyChapter) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var fake = new FakeImporter { ChapterText = emptyChapter ? " \r\n" : "正文。", EmptyComposition = !emptyChapter };
        bool opened = false;
        fixture.Window.WebImportPreviewOpener = _ => { opened = true; return Task.CompletedTask; };
        var window = Create(fixture, fake);
        string? message = null;
        ThemedDialog.Responder = dialog => { message = dialog.Message; return MessageBoxResult.OK; };
        try
        {
            await window.AnalyzeAsync();
            await window.DownloadAsync();
            Assert.False(opened);
            Assert.Null(window.ImportPath);
            Assert.NotNull(message);
            Assert.Empty(Directory.GetFiles(fixture.Project.Resolve("Cache/web-import"), "*.txt"));
        }
        finally { ThemedDialog.Responder = null; window.Close(); }
    });

    [Fact]
    public Task Invalid_delay_url_and_range_do_not_call_importer_operations() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var fake = new FakeImporter();
        var window = Create(fixture, fake);
        var messages = new List<string>();
        ThemedDialog.Responder = dialog => { messages.Add(dialog.Message); return MessageBoxResult.OK; };
        try
        {
            Text(window, "UrlInput").Text = "file:///C:/book.txt";
            await window.AnalyzeAsync();
            Assert.Null(fake.AnalyzedUrl);
            Text(window, "UrlInput").Text = "https://example.invalid/book";
            Text(window, "DelayInput").Text = "0.1";
            await window.AnalyzeAsync();
            Assert.Null(fake.AnalyzedUrl);
            Text(window, "DelayInput").Text = "1.5";
            await window.AnalyzeAsync();
            Text(window, "FromInput").Text = "3";
            Text(window, "ToInput").Text = "1";
            await window.PreviewAsync();
            await window.DownloadAsync();
            Assert.Null(fake.PreviewedLink);
            Assert.Null(fake.DownloadedLinks);
            Assert.Equal(4, messages.Count);
        }
        finally { ThemedDialog.Responder = null; window.Close(); }
    });

    [Fact]
    public Task Entry_commands_require_project_and_follow_project_command_availability() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("UpdateProjectChapterCommands");
        Assert.False(((Button)fixture.Window.FindName("ProjectImportWebButton")).IsEnabled);
        Assert.False(((MenuItem)fixture.Window.FindName("ProjectImportWebMenuItem")).IsEnabled);
        Assert.Throws<InvalidOperationException>(() => fixture.Window.CreateWebImportWindow());
        await fixture.CreateProjectAsync();
        fixture.Call("UpdateProjectChapterCommands");
        Assert.True(((Button)fixture.Window.FindName("ProjectImportWebButton")).IsEnabled);
        fixture.Set("_uiOperation", true);
        fixture.Call("UpdateProjectChapterCommands");
        Assert.False(((Button)fixture.Window.FindName("ProjectImportWebButton")).IsEnabled);
        Assert.False(((MenuItem)fixture.Window.FindName("ProjectImportWebMenuItem")).IsEnabled);
    });

    private static WebImportWindow Create(WorkspaceFixture fixture, FakeImporter fake)
    {
        fixture.Window.WebNovelImporterFactory = _ => fake;
        var window = fixture.Window.CreateWebImportWindow();
        Text(window, "UrlInput").Text = "https://example.invalid/book";
        return window;
    }

    private static TextBox Text(WebImportWindow window, string name) => (TextBox)window.FindName(name);
    private static TextBlock Block(WebImportWindow window, string name) => (TextBlock)window.FindName(name);
    private static Button FindButton(WebImportWindow window, string name) => (Button)window.FindName(name);

    private sealed class FakeImporter : IWebNovelImporter
    {
        public WebChapterLink[] Links { get; } =
        [
            new(10, "第10章 Một", new Uri("https://example.invalid/10")),
            new(20, "第20章 Hai", new Uri("https://example.invalid/20")),
            new(30, "第30章 Ba", new Uri("https://example.invalid/30"))
        ];
        public WebBookAnalysis Analysis => new(new Uri("https://example.invalid/book"), "Truyện / thử: web?", Links, "Mục lục thử", ["Kiểm tra chương"], NumberingConflict);
        public bool NumberingConflict { get; init; }
        public bool KeepSiteOrder { get; private set; }
        public Uri? AnalyzedUrl { get; private set; }
        public WebChapterLink? PreviewedLink { get; private set; }
        public IReadOnlyList<WebChapterLink>? DownloadedLinks { get; private set; }
        public IReadOnlyList<WebChapterContent>? ComposedChapters { get; private set; }
        public string? ComposedText { get; private set; }
        public string ChapterText { get; init; } = "正文。";
        public Exception? AnalysisError { get; init; }
        public bool PauseAnalysis { get; init; }
        public bool PauseDownload { get; init; }
        public bool EmptyComposition { get; init; }
        public bool CancellationObserved { get; private set; }
        public TaskCompletionSource Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<WebBookAnalysis> AnalyzeAsync(Uri url, CancellationToken cancellationToken = default)
        {
            AnalyzedUrl = url;
            if (AnalysisError is not null) throw AnalysisError;
            if (PauseAnalysis) await PauseAsync(cancellationToken);
            return Analysis;
        }

        public Task<WebChapterContent> FetchChapterAsync(WebChapterLink link, CancellationToken cancellationToken = default)
        {
            PreviewedLink = link;
            return Task.FromResult(new WebChapterContent(link, link.Title, ChapterText, 2));
        }

        public async IAsyncEnumerable<WebChapterContent> FetchChaptersAsync(IReadOnlyList<WebChapterLink> links,
            IProgress<WebImportProgress>? progress = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            DownloadedLinks = links;
            for (int i = 0; i < links.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new(links[i], links[i].Title, ChapterText, 2);
                progress?.Report(new(i + 1, links.Count, links[i].Title));
                if (PauseDownload) await PauseAsync(cancellationToken);
            }
        }

        private async Task PauseAsync(CancellationToken token)
        {
            Paused.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { CancellationObserved = true; throw; }
        }

        public string ComposeImportText(IReadOnlyList<WebChapterContent> chapters, bool keepSiteOrder = false)
        {
            KeepSiteOrder = keepSiteOrder;
            ComposedChapters = chapters.ToArray();
            return ComposedText = EmptyComposition ? " " : string.Join("\n", chapters.Select(chapter => $"{chapter.Title}\n{chapter.Text}\n"));
        }
    }
}
