using QuickTranslator.AiEditor.Core.AutoTranslate;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ContentFlagTests
{
    private const string Characters = """{ "characters": [ { "chineseName": "林凡", "vietnameseName": "Lâm Phàm", "gender": "Male" } ] }""";

    [Theory]
    [InlineData("""{"flags":[{"type":"minorRape","chapter":"第3章","characters":["甲"]}]}""", true)]
    [InlineData("""{"flags":[{"type":"minor","chapter":"第3章","characters":["甲"]},{"type":"rape","chapter":"第3章","characters":["甲","乙"]}]}""", true)]
    [InlineData("""{"flags":[{"type":"minor","chapter":"第3章","characters":["甲"]},{"type":"rape","chapter":"第9章","characters":["甲"]}]}""", false)]
    [InlineData("""{"flags":[{"type":"rape","chapter":"第9章","characters":["乙"]}]}""", false)]
    [InlineData("""{"flags":[]}""", false)]
    public void MustStopOnlyForRapeOfAMinor(string json, bool stop)
    {
        ContentFlagReport report = Assert.IsType<ContentFlagReport>(ContentFlagFile.TryParse(json));
        Assert.Equal(stop, report.MustStop);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("""{"flags":[{"type":"minor","chap""")]
    public void IncompleteFileReadsAsNothingYet(string? json) => Assert.Null(ContentFlagFile.TryParse(json));

    [Fact]
    public void UnknownTypesAreIgnoredAndDetailsDescribed()
    {
        ContentFlagReport report = ContentFlagFile.TryParse(
            """{"flags":[{"type":"violence","chapter":"x"},{"type":"Minor","chapter":"第2章 开学","characters":["小丽"],"ageEvidence":"她才十五岁","description":"Nhân vật 15 tuổi trong cảnh tình dục."}]}""")!;
        ContentFlag flag = Assert.Single(report.Flags);
        Assert.Equal(ContentFlagType.MinorSexual, flag.Type);
        Assert.Contains("第2章 开学", report.Describe());
        Assert.Contains("她才十五岁", report.Describe());
    }

    [Fact]
    public async Task RunnerStopsCodexAtOnceWhenTheScanFindsRapeOfAMinor()
    {
        string work = NewWork();
        var codex = new ScriptedCodex(async (request, token) =>
        {
            await File.WriteAllTextAsync(Path.Combine(request.WorkDirectory, ContentFlagFile.FileName),
                """{"flags":[{"type":"minorRape","chapter":"第3章","characters":["甲"],"description":"Cảnh cưỡng hiếp nhân vật 16 tuổi."}]}""", token);
            await Task.Delay(TimeSpan.FromMinutes(5), token); // The survey would go on for minutes.
            return 0;
        });
        var reports = new List<ContentFlagReport>();
        try
        {
            var runner = new CodexSurveyRunner(new CodexSurveyOptions(), codex) { ContentPollInterval = TimeSpan.FromMilliseconds(20) };
            var started = DateTime.UtcNow;
            var stop = await Assert.ThrowsAsync<ContentStopException>(() => runner.RunAsync("raw", work, Guid.NewGuid(), "x",
                contentFlags: new SyncProgress<ContentFlagReport>(reports.Add)));
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30));
            Assert.True(codex.Cancelled);
            Assert.True(stop.Report.MustStop);
            Assert.Single(reports);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    [Fact]
    public async Task RunnerWarnsWhileTheSurveyGoesOnAndKeepsTheFlags()
    {
        string work = NewWork();
        var codex = new ScriptedCodex(async (request, token) =>
        {
            await File.WriteAllTextAsync(Path.Combine(request.WorkDirectory, ContentFlagFile.FileName),
                """{"flags":[{"type":"rape","chapter":"第9章","characters":["乙"],"description":"Cưỡng hiếp có bạo lực."}]}""", token);
            await Task.Delay(200, token);
            await File.WriteAllTextAsync(Path.Combine(request.WorkDirectory, CodexSurveyRunner.CharactersFile), Characters, token);
            await File.WriteAllTextAsync(Path.Combine(request.WorkDirectory, CodexSurveyRunner.GlossaryFile), "[]", token);
            return 0;
        });
        var reports = new List<ContentFlagReport>();
        try
        {
            var runner = new CodexSurveyRunner(new CodexSurveyOptions(), codex) { ContentPollInterval = TimeSpan.FromMilliseconds(20) };
            CodexSurveyImportResult result = await runner.RunAsync("raw", work, Guid.NewGuid(), "x",
                contentFlags: new SyncProgress<ContentFlagReport>(reports.Add));
            Assert.False(codex.Cancelled);
            Assert.Equal(ContentFlagType.SevereRape, Assert.Single(result.ContentFlags.Flags).Type);
            Assert.Single(reports);
            Assert.Single(result.Characters.Profiles);
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static string NewWork()
    {
        string work = Path.Combine(Path.GetTempPath(), "qt-content-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ScriptedCodex(Func<CodexRunRequest, CancellationToken, Task<int>> script) : ICodexProcessRunner
    {
        public bool Cancelled { get; private set; }

        public async Task<int> RunAsync(CodexRunRequest request, IProgress<string>? output, CancellationToken cancellationToken)
        {
            try { return await script(request, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
    }
}
