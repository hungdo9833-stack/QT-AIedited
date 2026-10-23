using System.Diagnostics;
using System.Net;
using System.Text;
using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Tests.WebImport;

public sealed class WebSampleCheckTests
{
    private static readonly Uri Origin = new("https://samples.invalid/");
    private static readonly WebImportOptions Options = new() { DelayBetweenRequests = TimeSpan.Zero, MaxRetries = 0 };
    private static string Prose(int chapter, int length = 160) => new string((char)('春' + chapter), length) + "。";
    private static string Story(int chapter, int length = 160) => $"<article id='story'><p>{Prose(chapter, length)}</p></article>";
    private static string Catalog(int count) => "<h1>样本书名</h1>" + string.Concat(Enumerable.Range(1, count)
        .Select(i => $"<a href='/read_{i}.html?token=secret'>第{i}章 标题</a>"));

    [Theory]
    [InlineData(3, 2)]
    [InlineData(6, 4)]
    public async Task Consistent_samples_pass_and_are_reused_without_disk_cache(int count, int second)
    {
        using var handler = new Handler(Catalog(count), i => Story(i));
        using var importer = new WebNovelImporter(Options with { DelayBetweenRequests = TimeSpan.FromMilliseconds(25) }, handler);
        var analysis = await importer.AnalyzeAsync(new Uri(Origin, "book"));
        Assert.True(analysis.SampleCheck!.Passed);
        Assert.Empty(analysis.Warnings);
        Assert.Equal(new[] { 1, second }, analysis.SampleCheck.ChapterIndexes);
        Assert.All(analysis.Chapters, c => Assert.Equal(analysis.SampleCheck.Signature, c.ExpectedBlockSignature));
        Assert.Equal(new[] { "/book", "/read_1.html", $"/read_{second}.html" }, handler.Paths);
        var samples = handler.RequestTimes.ToArray();
        Assert.All(samples.Zip(samples.Skip(1)), pair =>
            Assert.True(Stopwatch.GetElapsedTime(pair.First, pair.Second) >= TimeSpan.FromMilliseconds(20)));
        await foreach (var _ in importer.FetchChaptersAsync(analysis.Chapters)) { }
        Assert.Equal(count + 1, handler.Paths.Count);
        Assert.Equal(handler.Paths.Count, handler.Paths.Distinct().Count());
    }

    [Fact]
    public void Signature_ignores_class_order_and_unique_ids_and_preserves_extract_text()
    {
        static WebPage Page(string parentId, string id, string classes) => WebPage.Parse(Origin,
            Encoding.UTF8.GetBytes($"<main id='{parentId}' class='reader'><article id='{id}' class='{classes}'>{Prose(1)}</article></main>"), "utf-8");
        var a = Page("reader-123", "post-123", "body text");
        var b = Page("reader-456", "post-456", "text body");
        var c = Page("abcdefabcdef", "fedcbafedcba", "body text");
        var first = ChapterContentExtractor.ExtractWithSignature(a);
        Assert.Equal(first.Signature, ChapterContentExtractor.ExtractWithSignature(b).Signature);
        Assert.Equal(first.Signature, ChapterContentExtractor.ExtractWithSignature(c).Signature);
        Assert.Equal(Prose(1), first.Text);
        Assert.Equal(ChapterContentExtractor.Extract(a), first.Text);
        Assert.Contains("article", first.Signature);
        Assert.Contains("reader", first.Signature);
        Assert.DoesNotContain("123", first.Signature);
    }

    [Fact]
    public void Signature_distinguishes_parent_structure_and_numeric_class_tokens()
    {
        static string Signature(string parent, string classes) => ChapterContentExtractor.ExtractWithSignature(WebPage.Parse(Origin,
            Encoding.UTF8.GetBytes($"<main id='{parent}'><article class='{classes}'>{Prose(1)}</article></main>"), "utf-8")).Signature;
        Assert.NotEqual(Signature("reader", "body"), Signature("sidebar", "body"));
        Assert.NotEqual(Signature("reader", "body1"), Signature("reader", "body2"));
    }

    [Fact]
    public void Agreed_block_can_be_a_forum_element_outside_the_scoring_tag_list()
    {
        WebPage Page(string html) => WebPage.Parse(Origin, Encoding.UTF8.GetBytes(html), "utf-8");
        var original = ChapterContentExtractor.ExtractWithSignature(Page($"<span class='t_f'>{Prose(1)}</span>"));
        var corrected = ChapterContentExtractor.ExtractWithSignature(
            Page($"<div class='content-section'>{Prose(20, 1600)}</div><span class='t_f'>{Prose(2)}</span>"), original.Signature);
        Assert.True(corrected.SignatureDiffered);
        Assert.True(corrected.SignatureCorrected);
        Assert.Equal(Prose(2), corrected.Text);
    }

    [Fact]
    public async Task Ratio_of_exactly_eight_is_allowed()
    {
        // Include punctuation in the length so the ratio is exactly 8, rather than just below it.
        using var handler = new Handler(Catalog(3), i => Story(i, i == 2 ? 160 * 8 + 7 : 160));
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(new Uri(Origin, "book"));
        Assert.True(analysis.SampleCheck!.Passed);
    }

    [Theory]
    [InlineData("signature", WebSampleCheckReason.SignatureMismatch)]
    [InlineData("ratio", WebSampleCheckReason.LengthRatio)]
    [InlineData("identical", WebSampleCheckReason.DuplicateContent)]
    [InlineData("title", WebSampleCheckReason.DuplicateTitles)]
    [InlineData("cjk", WebSampleCheckReason.InsufficientCjk)]
    public async Task Sample_problems_are_structured_safe_warnings(string problem, WebSampleCheckReason reason)
    {
        using var handler = new Handler(Catalog(3), i => problem switch
        {
            "signature" when i == 2 => $"<section id='other'>{Prose(i)}</section>",
            "ratio" when i == 2 => Story(i, 160 * 9),
            "identical" => Story(1),
            "title" => "<title>敏感页面标题?secret</title>" + Story(i),
            "cjk" => "<article id='story'>只剩很少文字</article>",
            _ => Story(i)
        });
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(new Uri(Origin, "book"));
        Assert.False(analysis.SampleCheck!.Passed);
        Assert.Contains(reason, analysis.SampleCheck.Reasons);
        Assert.NotEmpty(analysis.Warnings);
        Assert.All(analysis.Warnings, message =>
        {
            Assert.Contains("1, 2", message);
            Assert.DoesNotContain("secret", message);
            Assert.DoesNotContain("http", message);
            Assert.DoesNotContain("敏感", message);
            Assert.DoesNotContain(Prose(1), message);
        });
        if (problem == "signature") Assert.Null(analysis.SampleCheck.Signature);
        else Assert.NotNull(analysis.SampleCheck.Signature); // Agreement remains useful even with advisory problems.
        await foreach (var _ in importer.FetchChaptersAsync(analysis.Chapters)) { }
    }

    [Fact]
    public async Task Matching_sample_signature_corrects_different_best_block_and_survives_new_importer()
    {
        using var handler = new Handler(Catalog(6), i => Story(i) + (i == 2 ? $"<section>{Prose(20, 1600)}</section>" : ""));
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(new Uri(Origin, "book"));
        using var recreated = new WebNovelImporter(Options, handler);
        var chapter = await recreated.FetchChapterAsync(analysis.Chapters[1]);
        Assert.Equal(Prose(2), chapter.Text);
        Assert.Equal(analysis.SampleCheck!.Signature, chapter.BlockSignature);
        Assert.True(chapter.BlockSignatureDiffered);
        Assert.True(chapter.BlockSignatureCorrected);
        var issue = Assert.Single(WebImportQualityReport.Analyze([chapter]));
        Assert.Equal(WebImportQualityIssueKind.BlockSignatureMismatch, issue.Kind);
        Assert.Equal(1, issue.CorrectedChapterCount);
        Assert.Equal(new[] { 2 }, issue.ChapterIndexes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_insubstantial_expected_block_keeps_best_text_and_reports_uncorrected(bool tiny)
    {
        using var handler = new Handler(Catalog(6), i => i == 2
            ? (tiny ? "<article id='story'>太短</article>" : "") + $"<section>{Prose(20, 1600)}</section>"
            : Story(i));
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(new Uri(Origin, "book"));
        var chapter = await importer.FetchChapterAsync(analysis.Chapters[1]);
        Assert.Contains(Prose(20, 1600), chapter.Text);
        Assert.True(chapter.BlockSignatureDiffered);
        Assert.False(chapter.BlockSignatureCorrected);
        Assert.Equal(0, Assert.Single(WebImportQualityReport.Analyze([chapter])).CorrectedChapterCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signature_corrections_include_subpages_and_count_only_fully_corrected_chapters(bool missingOnSecondPage)
    {
        using var handler = new Handler(Catalog(6), i => i == 2
            ? Story(i) + $"<section>{Prose(20, 1600)}</section><a href='read_2_2.html'>下一页</a>"
            : Story(i));
        handler.Subpage = (missingOnSecondPage ? "" : Story(3)) + $"<section>{Prose(21, 1600)}</section>";
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(new Uri(Origin, "book"));
        var chapter = await importer.FetchChapterAsync(analysis.Chapters[1]);
        Assert.Equal(2, chapter.PageCount);
        Assert.True(chapter.BlockSignatureDiffered);
        Assert.Equal(!missingOnSecondPage, chapter.BlockSignatureCorrected);
        Assert.Contains(Prose(2), chapter.Text);
    }

    [Theory]
    [InlineData("<div>cf-chl challenge-platform</div>", WebImportBlockReason.BotChallenge)]
    [InlineData("<div id='app'></div><script src='app.js'></script>", WebImportBlockReason.RequiresBrowser)]
    [InlineData("<form><button>确认</button><button>取消</button></form>", WebImportBlockReason.RequiresUserConfirmation)]
    public async Task Blocked_sample_preserves_existing_block_behavior(string html, WebImportBlockReason reason)
    {
        using var handler = new Handler(Catalog(6), i => i == 4 ? html : Story(i));
        using var importer = new WebNovelImporter(Options, handler);
        var error = await Assert.ThrowsAsync<WebImportBlockedException>(() => importer.AnalyzeAsync(new Uri(Origin, "book")));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(3, handler.Paths.Count);
    }

    [Fact]
    public async Task Transport_failure_warns_without_exposing_server_details_or_preventing_analysis()
    {
        using var handler = new Handler(Catalog(3), i => i == 1
            ? throw new HttpRequestException("https://private.invalid/?secret=secret") : Story(i));
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(new Uri(Origin, "book"));
        Assert.Contains(WebSampleCheckReason.FetchFailed, analysis.SampleCheck!.Reasons);
        Assert.False(analysis.SampleCheck.Passed);
        Assert.Null(analysis.SampleCheck.Signature);
        Assert.DoesNotContain("secret", string.Join("\n", analysis.Warnings));
    }

    [Fact]
    public async Task Empty_server_rendered_sample_warns_and_remains_downloadable()
    {
        using var handler = new Handler(Catalog(3), _ => "<article></article>");
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(new Uri(Origin, "book"));
        Assert.Contains(WebSampleCheckReason.InsufficientCjk, analysis.SampleCheck!.Reasons);
        Assert.Equal("", (await importer.FetchChapterAsync(analysis.Chapters[0])).Text);
    }

    [Fact]
    public async Task Cancellation_during_samples_propagates()
    {
        using var cancelled = new CancellationTokenSource();
        using var handler = new Handler(Catalog(3), _ => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); });
        using var importer = new WebNovelImporter(Options, handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.AnalyzeAsync(new Uri(Origin, "book"), cancelled.Token));
    }

    private sealed class Handler(string catalog, Func<int, string> chapter) : HttpMessageHandler
    {
        internal List<string> Paths { get; } = [];
        internal List<long> RequestTimes { get; } = [];
        internal string Subpage { get; set; } = "";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            RequestTimes.Add(Stopwatch.GetTimestamp());
            string html = path == "/book" ? catalog : path == "/read_2_2.html" ? Subpage
                : chapter(int.Parse(System.Text.RegularExpressions.Regex.Match(path, @"\d+").Value));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") });
        }
    }
}
