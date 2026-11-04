using System.Collections.Concurrent;
using System.IO;
using QuickTranslator.AiEditor.Core.AutoTranslate;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class AutoBookPipelineTests
{
    private static readonly AutoTranslateSettings Settings = new(new CodexSurveyOptions(), TimeSpan.Zero);

    [Fact]
    public async Task Next_book_downloads_while_codex_surveys_this_one()
    {
        var stages = new FakeStages();
        stages.SurveyGate["a"] = new();
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a"), b = Row("b");
        pipeline.TryEnqueue(a);
        pipeline.TryEnqueue(b);
        Task run = pipeline.RunAsync();

        await stages.WaitForAsync("download:b");
        Assert.DoesNotContain("translate:a", stages.Log);
        stages.SurveyGate["a"].SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        // Each book goes through its stages in order.
        Assert.Equal(["find:a", "download:a", "survey:a", "translate:a"], stages.Log.Where(x => x.EndsWith(":a")));
        Assert.Equal(["find:b", "download:b", "survey:b", "translate:b"], stages.Log.Where(x => x.EndsWith(":b")));
        string[] log = [.. stages.Log];
        Assert.True(Array.IndexOf(log, "download:b") < Array.IndexOf(log, "translate:a"));
        Assert.True(a.Finished && b.Finished);
        Assert.False(a.CanRetry || b.CanRetry);
    }

    [Fact]
    public async Task Up_to_three_codex_surveys_run_at_once()
    {
        var stages = new FakeStages();
        string[] names = ["a", "b", "c", "d"];
        foreach (string name in names) stages.SurveyGate[name] = new();
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow[] rows = [.. names.Select(Row)];
        foreach (AutoBookRow row in rows) pipeline.TryEnqueue(row);
        Task run = pipeline.RunAsync();

        // a, b and c survey side by side while a is still running; d waits for a free Codex slot.
        await stages.WaitForAsync("survey:a");
        await stages.WaitForAsync("survey:b");
        await stages.WaitForAsync("survey:c");
        await stages.WaitForAsync("download:d");
        await Task.Delay(100);
        Assert.DoesNotContain("survey:d", stages.Log);

        stages.SurveyGate["b"].SetResult();
        await stages.WaitForAsync("survey:d");
        foreach (string name in names) stages.SurveyGate[name].TrySetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(rows, row => Assert.True(row.Finished));
    }

    [Fact]
    public async Task A_book_added_while_running_joins_the_run()
    {
        var stages = new FakeStages();
        stages.SurveyGate["a"] = new();
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a"), b = Row("b");
        pipeline.TryEnqueue(a);
        Task run = pipeline.RunAsync();
        await stages.WaitForAsync("survey:a");

        Assert.True(pipeline.TryEnqueue(b));
        await stages.WaitForAsync("download:b");
        stages.SurveyGate["a"].SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(b.Finished);
        Assert.False(pipeline.TryEnqueue(Row("c")));
    }

    [Fact]
    public async Task Retry_resumes_at_the_stage_that_failed()
    {
        var stages = new FakeStages { FailTranslate = { "a" }, PauseDownload = { "b" } };
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a"), b = Row("b");
        pipeline.TryEnqueue(a);
        pipeline.TryEnqueue(b);
        await pipeline.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(a.CanRetry && b.CanRetry);
        Assert.Equal("Lỗi dịch", a.Stage);

        stages.FailTranslate.Clear();
        stages.PauseDownload.Clear();
        stages.Log.Clear();
        var retry = new AutoBookPipeline(stages, Settings);
        retry.TryEnqueue(a);
        retry.TryEnqueue(b);
        await retry.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // a only translates again (its project exists); b downloads again and goes on.
        // b was looked up in the library on its first run: retrying goes straight to downloading.
        Assert.Equal(["translate:a", "download:b", "survey:b", "translate:b"], stages.Log.OrderBy(x => x.EndsWith('a') ? 0 : 1));
        Assert.True(a.Finished && b.Finished);
    }

    [Fact]
    public async Task Removing_a_book_cancels_only_that_book()
    {
        var stages = new FakeStages();
        stages.DownloadGate["a"] = new();
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a"), b = Row("b");
        pipeline.TryEnqueue(a);
        pipeline.TryEnqueue(b);
        Task run = pipeline.RunAsync();
        await stages.WaitForAsync("download:a");

        AutoBookPipeline.Remove(a);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("survey:a", stages.Log);
        Assert.True(b.Finished);
        Assert.False(a.CanRetry);
    }

    [Fact]
    public async Task Stop_leaves_every_unfinished_book_retryable()
    {
        var stages = new FakeStages();
        stages.SurveyGate["a"] = new();
        stages.SurveyGate["b"] = new();
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a"), b = Row("b");
        pipeline.TryEnqueue(a);
        pipeline.TryEnqueue(b);
        Task run = pipeline.RunAsync();
        await stages.WaitForAsync("survey:a");
        await stages.WaitForAsync("survey:b");

        pipeline.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(a.CanRetry && b.CanRetry);
        Assert.Equal(AutoBookStep.Survey, a.Step);
    }

    [Fact]
    public async Task A_link_with_a_complete_project_only_translates_it()
    {
        var stages = new FakeStages();
        stages.Existing["a"] = new("a-old.qtproj", "A", Chapters: 90, SourceChapters: 90, Surveyed: true);
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a");
        pipeline.TryEnqueue(a);
        await pipeline.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["find:a", "translate:a"], stages.Log);
        Assert.Equal("a-old.qtproj", a.ManifestPath);
        Assert.True(a.Finished);
    }

    [Theory]
    [InlineData(19, true)]
    [InlineData(90, false)]
    public async Task An_incomplete_project_waits_for_Retry_which_makes_a_new_one(int chapters, bool surveyed)
    {
        var stages = new FakeStages();
        stages.Existing["a"] = new("a-old.qtproj", "A", chapters, SourceChapters: 90, surveyed);
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a");
        pipeline.TryEnqueue(a);
        await pipeline.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(["find:a"], stages.Log);
        Assert.True(a.CanRetry);
        Assert.Equal("Đã có dự án chưa đủ", a.Stage);

        stages.Log.Clear();
        var retry = new AutoBookPipeline(stages, Settings);
        retry.TryEnqueue(a);
        await retry.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["download:a", "survey:a", "translate:a"], stages.Log);
        Assert.Equal("a.qtproj", a.ManifestPath);
    }

    [Fact]
    public async Task A_throttled_download_continues_on_its_own_after_the_wait()
    {
        var stages = new FakeStages { ThrottleTimes = { ["a"] = 2 } };
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a"), b = Row("b");
        pipeline.TryEnqueue(a);
        pipeline.TryEnqueue(b);
        await pipeline.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Two throttled attempts, then the third download goes through; b is not held up meanwhile.
        Assert.Equal(["find:a", "download:a", "download:a", "download:a", "survey:a", "translate:a"], stages.Log.Where(x => x.EndsWith(":a")));
        Assert.True(a.Finished && b.Finished);
    }

    [Fact]
    public async Task Removing_a_book_while_it_waits_to_retry_ends_the_run()
    {
        var stages = new FakeStages { ThrottleTimes = { ["a"] = 100 }, ThrottleWait = TimeSpan.FromSeconds(30) };
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a");
        pipeline.TryEnqueue(a);
        Task run = pipeline.RunAsync();
        await stages.WaitForAsync("download:a");
        await Task.Delay(100);

        AutoBookPipeline.Remove(a);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(stages.Log, x => x == "download:a");
    }

    [Fact]
    public async Task A_txt_book_is_not_held_back_by_a_web_book_downloading()
    {
        var stages = new FakeStages();
        stages.DownloadGate["web"] = new();
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow web = Row("web"), txt = new(new Uri("file:///C:/books/txt"));
        pipeline.TryEnqueue(web);
        pipeline.TryEnqueue(txt);
        Assert.Equal("Chờ đọc file", txt.Stage);
        Task run = pipeline.RunAsync();

        await stages.WaitForAsync("translate:C:/books/txt");
        Assert.DoesNotContain("survey:web", stages.Log);
        stages.DownloadGate["web"].SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(web.Finished && txt.Finished);
    }

    [Fact]
    public void Pasted_txt_paths_become_file_links_and_other_files_are_refused()
    {
        string folder = Path.Combine(Path.GetTempPath(), "qt-txt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string txt = Path.Combine(folder, "书.txt"), epub = Path.Combine(folder, "书.epub");
            File.WriteAllText(txt, "第1章");
            File.WriteAllText(epub, "");
            IReadOnlyList<Uri> links = AutoTranslateWindow.ParseLinks($"\"{txt}\"\nhttps://example.com/book");
            Assert.True(links[0].IsFile);
            Assert.Equal(txt, links[0].LocalPath);
            Assert.Equal("https://example.com/book", links[1].AbsoluteUri);
            Assert.Throws<InvalidOperationException>(() => AutoTranslateWindow.ParseLinks(epub));
            Assert.Throws<InvalidOperationException>(() => AutoTranslateWindow.ParseLinks(Path.Combine(folder, "missing.txt")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    private static AutoBookRow Row(string name) => new(new Uri("https://example.com/" + name));

    private sealed class FakeStages : IAutoBookStages
    {
        public ConcurrentQueue<string> Log { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource> DownloadGate { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource> SurveyGate { get; } = new();
        public HashSet<string> FailTranslate { get; } = [];
        public HashSet<string> PauseDownload { get; } = [];
        public ConcurrentDictionary<string, int> ThrottleTimes { get; } = new();
        public TimeSpan ThrottleWait { get; init; } = TimeSpan.FromMilliseconds(20);
        public ConcurrentDictionary<string, AutoBookExisting> Existing { get; } = new();

        public Task<AutoBookExisting?> FindExistingAsync(AutoBookRow row, AutoTranslateSettings settings, CancellationToken cancellationToken)
        {
            Log.Enqueue("find:" + Name(row));
            return Task.FromResult(Existing.TryGetValue(Name(row), out var found) ? found : null);
        }

        public async Task<AutoBookDownload?> DownloadAsync(AutoBookRow row, AutoTranslateSettings settings, CancellationToken cancellationToken)
        {
            string name = Name(row);
            Log.Enqueue("download:" + name);
            if (DownloadGate.TryGetValue(name, out var gate)) await gate.Task.WaitAsync(cancellationToken);
            await Task.Yield();
            if (ThrottleTimes.TryGetValue(name, out int left) && left > 0)
            {
                ThrottleTimes[name] = left - 1;
                row.RetryAfter = ThrottleWait;
                return null;
            }
            return PauseDownload.Contains(name) ? null : new AutoBookDownload(row.Url, name, "", "");
        }

        public async Task<string?> PrepareAsync(AutoBookRow row, AutoBookDownload download, AutoTranslateSettings settings,
            CancellationToken cancellationToken)
        {
            string name = Name(row);
            Log.Enqueue("survey:" + name);
            if (SurveyGate.TryGetValue(name, out var gate)) await gate.Task.WaitAsync(cancellationToken);
            await Task.Yield();
            return name + ".qtproj";
        }

        public async Task<bool> TranslateAsync(AutoBookRow row, string manifestPath, CancellationToken cancellationToken)
        {
            string name = Name(row);
            Log.Enqueue("translate:" + name);
            await Task.Yield();
            if (!FailTranslate.Contains(name)) return true;
            row.Stage = "Lỗi dịch";
            return false;
        }

        public async Task WaitForAsync(string entry)
        {
            for (int i = 0; i < 500 && !Log.Contains(entry); i++) await Task.Delay(10);
            Assert.Contains(entry, Log);
        }

        private static string Name(AutoBookRow row) => row.Url.AbsolutePath.Trim('/');
    }
}
