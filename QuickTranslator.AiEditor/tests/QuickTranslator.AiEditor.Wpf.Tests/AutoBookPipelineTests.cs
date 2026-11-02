using System.Collections.Concurrent;
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

        // Each book goes through its stages in order; b's survey waits for a's (one Codex run at a time).
        Assert.Equal(["download:a", "survey:a", "translate:a"], stages.Log.Where(x => x.EndsWith(":a")));
        Assert.Equal(["download:b", "survey:b", "translate:b"], stages.Log.Where(x => x.EndsWith(":b")));
        string[] log = [.. stages.Log];
        Assert.True(Array.IndexOf(log, "download:b") < Array.IndexOf(log, "translate:a"));
        Assert.True(Array.IndexOf(log, "survey:a") < Array.IndexOf(log, "survey:b"));
        Assert.True(a.Finished && b.Finished);
        Assert.False(a.CanRetry || b.CanRetry);
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
        var pipeline = new AutoBookPipeline(stages, Settings);
        AutoBookRow a = Row("a"), b = Row("b");
        pipeline.TryEnqueue(a);
        pipeline.TryEnqueue(b);
        Task run = pipeline.RunAsync();
        await stages.WaitForAsync("survey:a");

        pipeline.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(a.CanRetry && b.CanRetry);
        Assert.Equal(AutoBookStep.Survey, a.Step);
    }

    private static AutoBookRow Row(string name) => new(new Uri("https://example.com/" + name));

    private sealed class FakeStages : IAutoBookStages
    {
        public ConcurrentQueue<string> Log { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource> DownloadGate { get; } = new();
        public ConcurrentDictionary<string, TaskCompletionSource> SurveyGate { get; } = new();
        public HashSet<string> FailTranslate { get; } = [];
        public HashSet<string> PauseDownload { get; } = [];

        public async Task<AutoBookDownload?> DownloadAsync(AutoBookRow row, AutoTranslateSettings settings, CancellationToken cancellationToken)
        {
            string name = Name(row);
            Log.Enqueue("download:" + name);
            if (DownloadGate.TryGetValue(name, out var gate)) await gate.Task.WaitAsync(cancellationToken);
            await Task.Yield();
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
