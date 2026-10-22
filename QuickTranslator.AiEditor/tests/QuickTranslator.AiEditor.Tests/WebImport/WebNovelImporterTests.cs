using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Tests.WebImport;

public sealed class WebNovelImporterTests
{
    private static readonly Uri Origin = new("https://fixtures.invalid/");
    private const string Placeholder = "测试占位文字测试占位文字测试占位文字测试占位文字。";
    private static WebImportOptions Options => new() { DelayBetweenRequests = TimeSpan.Zero, MaxRetries = 0 };
    private static Uri Url(string path) => new(Origin, path);
    private static WebChapterLink Link(string path = "book/read_5.html", string title = "第五章 占位") => new(1, title, Url(path));

    [Fact]
    public async Task Cool18_OrdersOnlyBookSiblingsAndExtractsFirstPostWithHeadings()
    {
        var post = $"<h2>第十一章 占位</h2><p>{Placeholder}</p><p>{Placeholder}</p><h2>第十二章 占位</h2><p>{Placeholder}</p>";
        var siblings = string.Concat(Enumerable.Range(0, 6).Reverse().Select(i =>
            $"<li><a href='index.php?app=forum&amp;act=threadview&amp;tid={100 + i}'>【测试书名】（{i * 10 + 1}-{i * 10 + 10}）</a></li>"));
        var html = $"<h1>【测试书名】（11-20）</h1><div class='content-section'>{post}</div>" +
                   $"<ul class='thread-list'>{siblings}<li><a href='index.php?app=forum&amp;act=threadview&amp;tid=999'>【别的书名】（1-10）</a></li></ul>" +
                   $"<div class='reply-list'><p>{string.Concat(Enumerable.Repeat("回复占位", 300))}</p></div><div class='content-section'>回复占位</div>";
        var path = "index.php?app=forum&act=threadview&tid=101";
        using var handler = new FakeHandler(_ => Html(html));
        using var importer = new WebNovelImporter(Options, handler);

        var analysis = await importer.AnalyzeAsync(Url(path));
        Assert.Equal("测试书名", analysis.BookTitle);
        Assert.Equal(6, analysis.Chapters.Count);
        Assert.Equal(Enumerable.Range(100, 6).Select(i => i.ToString()), analysis.Chapters.Select(c => c.Url.Query.Split('=').Last()));
        Assert.Equal(Enumerable.Range(1, 6), analysis.Chapters.Select(c => c.Index));
        var content = await importer.FetchChapterAsync(analysis.Chapters[1]);
        Assert.Equal($"第十一章 占位\n{Placeholder}\n{Placeholder}\n第十二章 占位\n{Placeholder}", content.Text);
        Assert.Equal(content.Text, importer.ComposeImportText([content]));
        Assert.DoesNotContain("回复", content.Text);
    }

    [Fact]
    public async Task Forum_IncludesCurrentThreadEvenWithoutSelfLink()
    {
        using var handler = new FakeHandler(_ => Html("<h1>《测试书名》（11-20）</h1>" +
            "<a href='?app=forum&amp;act=threadview&amp;tid=1'>《测试书名》（1-10）</a>" +
            "<a href='?app=forum&amp;act=threadview&amp;tid=3'>《测试书名》（21-30）</a>"));
        using var importer = new WebNovelImporter(Options, handler);
        var result = await importer.AnalyzeAsync(Url("index.php?app=forum&act=threadview&tid=2"));
        Assert.Equal(new[] { "1-10", "11-20", "21-30" }, result.Chapters.Select(c => c.Title.Split('（')[1].TrimEnd('）')));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Novelbox_ThirtyLinksAndNextFlightOnlyText(bool flightRecord)
    {
        var text = $"第一章 占位\n{Placeholder}\n{Placeholder}";
        var payload = flightRecord ? $"a:T{Encoding.UTF8.GetByteCount(text):x},{text}" : text;
        var encoded = JsonSerializer.Serialize(payload);
        var chapter = $"<div id='__next'></div><script>self.__next_f.push([1,{encoded}])</script>";
        using var handler = new FakeHandler(r => Html(r.RequestUri!.AbsolutePath == "/novel/test" ?
            "<title>测试书名 - 占位站点</title>" + Catalog(30, i => $"/novel/test/chapter/{i}") +
            "<a href='/novel/test/chapter/1#duplicate'>第1章 重复</a><a href='https://elsewhere.invalid/chapter/50'>第50章 外站</a>" : chapter));
        using var importer = new WebNovelImporter(Options, handler);
        var result = await importer.AnalyzeAsync(Url("novel/test"));
        Assert.Equal("测试书名", result.BookTitle);
        Assert.Equal(30, result.Chapters.Count);
        Assert.Equal(Enumerable.Range(1, 30), result.Chapters.Select(c => c.Index));
        Assert.Equal(text, (await importer.FetchChapterAsync(result.Chapters[0])).Text);
    }

    [Fact]
    public async Task NextFlight_DecodesJsonModelAndSplitStreamWithoutMetadata()
    {
        var text = $"第一章 占位\n{Placeholder}\n{Placeholder}";
        var record = "1:[\"$\",\"div\",null,{\"children\":" + JsonSerializer.Serialize(text) + "}]\n";
        var halfway = record.Length / 2;
        var html = "<div id='app'></div><script>self.__next_f.push([1," + JsonSerializer.Serialize(record[..halfway]) +
                   "]);self.__next_f.push([1," + JsonSerializer.Serialize(record[halfway..]) + "])</script>";
        using var handler = new FakeHandler(_ => Html(html));
        using var importer = new WebNovelImporter(Options, handler);
        Assert.Equal(text, (await importer.FetchChapterAsync(Link())).Text);
    }

    [Fact]
    public async Task Tks_FollowsDirectoryOnceAndReportsVueRequiresBrowser()
    {
        using var handler = new FakeHandler(r => Html(r.RequestUri!.AbsolutePath switch
        {
            "/book1/" => "<h1>测试书名</h1><a href='dir.html'>章节目录</a>",
            "/book1/dir.html" => Catalog(5, i => $"/book1/read_{i}.html") + "<a href='?sort=desc'>倒序</a><a href='other/dir.html'>目录</a>",
            _ => "<div id='app'><a :href='v.url'>{{ v.url }}</a>{{ chapter.content }}</div><script src='app.js'></script>"
        }));
        using var importer = new WebNovelImporter(Options, handler);
        var result = await importer.AnalyzeAsync(Url("book1/"));
        Assert.Equal(5, result.Chapters.Count);
        Assert.Equal("测试书名", result.BookTitle);
        Assert.Equal(2, handler.Calls.Count);
        var error = await Assert.ThrowsAsync<WebImportBlockedException>(() => importer.FetchChapterAsync(result.Chapters[0]));
        Assert.Equal(WebImportBlockReason.RequiresBrowser, error.Reason);
        Assert.Contains("JavaScript", error.Message);
    }

    [Theory]
    [InlineData("<form method='post'><button name='action' value='yes'>确认</button><button name='action' value='no'>取消</button></form>", WebImportBlockReason.RequiresUserConfirmation)]
    [InlineData("<title>Just a moment</title><script src='/cdn-cgi/challenge-platform/cf-chl.js'></script>", WebImportBlockReason.BotChallenge)]
    [InlineData("<div class='g-recaptcha'></div>", WebImportBlockReason.BotChallenge)]
    [InlineData("<div id='root'></div><script src='/app.js'></script>", WebImportBlockReason.RequiresBrowser)]
    [InlineData("<h1>测试书名</h1><p>没有目录</p>", WebImportBlockReason.NoChapterList)]
    public async Task BlocksHaveActionableMessages(string html, WebImportBlockReason reason)
    {
        using var handler = new FakeHandler(_ => Html(html));
        using var importer = new WebNovelImporter(Options, handler);
        var error = await Assert.ThrowsAsync<WebImportBlockedException>(() => importer.AnalyzeAsync(Url("blocked")));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(Url("blocked"), error.Url);
        Assert.Contains("Hãy", error.Message);
        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task MissingPageDoesNotRetry(int status)
    {
        using var handler = new FakeHandler(_ => new HttpResponseMessage((HttpStatusCode)status));
        using var importer = new WebNovelImporter(Options with { MaxRetries = 3 }, handler);
        var error = await Assert.ThrowsAsync<WebImportBlockedException>(() => importer.AnalyzeAsync(Url("missing")));
        Assert.Equal(WebImportBlockReason.NotFound, error.Reason);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task ChallengeOn503StopsWithoutRetry()
    {
        using var handler = new FakeHandler(_ => Html("<div>cf-chl challenge-platform</div>", HttpStatusCode.ServiceUnavailable));
        using var importer = new WebNovelImporter(Options with { MaxRetries = 3 }, handler);
        var error = await Assert.ThrowsAsync<WebImportBlockedException>(() => importer.FetchChapterAsync(Link()));
        Assert.Equal(WebImportBlockReason.BotChallenge, error.Reason);
        Assert.Single(handler.Calls);
    }

    [Theory]
    [InlineData("gbk", "header")]
    [InlineData("gb18030", "meta")]
    [InlineData("big5", "http-equiv")]
    [InlineData("utf-8", "bom")]
    public async Task DecodesEncodingsWithCorrectPrecedence(string name, string source)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var expected = "測試文字測試文字測試文字測試文字。";
        var meta = source switch
        {
            "meta" => $"<meta charset='{name}'>",
            "http-equiv" => $"<meta http-equiv='Content-Type' content='text/html; charset={name}'>",
            _ => "<meta charset='big5'>"
        };
        var encoding = Encoding.GetEncoding(name);
        var bytes = encoding.GetBytes(meta + "<article><p>" + expected + "</p></article>");
        if (source == "bom") bytes = Encoding.UTF8.GetPreamble().Concat(bytes).ToArray();
        using var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            if (source is "header" or "bom") response.Content.Headers.ContentType.CharSet = source == "bom" ? "gbk" : name;
            return response;
        });
        using var importer = new WebNovelImporter(Options, handler);
        Assert.Equal(expected, (await importer.FetchChapterAsync(Link())).Text);
    }

    [Fact]
    public async Task SplitChapter_JoinsTwoPagesAndStopsBeforeNextChapter()
    {
        using var handler = new FakeHandler(r => Html(r.RequestUri!.AbsolutePath.EndsWith("_2.html", StringComparison.Ordinal) ?
            $"<article><p>第二页{Placeholder}</p></article><a href='read_5.html'>上一页</a><a href='read_6.html'>下一章</a>" :
            $"<article><h2>第五章 占位</h2><p>{Placeholder}</p><p>请收藏本站</p><p>https://fixtures.invalid/</p></article>" +
            $"<nav>{Placeholder}</nav><aside>{Placeholder}</aside><a href='read_5_2.html'>下一页</a>"));
        using var importer = new WebNovelImporter(Options, handler);
        var result = await importer.FetchChapterAsync(Link());
        Assert.Equal(2, result.PageCount);
        Assert.Equal($"第五章 占位\n{Placeholder}\n\n第二页{Placeholder}", result.Text);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task QueryPaginationTerminatesOnCycle()
    {
        using var handler = new FakeHandler(r => Html($"<article>{Placeholder}</article>" +
            (r.RequestUri!.Query == "?page=1" ? "<a href='?page=2'>下一頁</a>" : "<a href='?page=1'>下一頁</a>")));
        using var importer = new WebNovelImporter(Options, handler);
        Assert.Equal(2, (await importer.FetchChapterAsync(Link("read.php?page=1"))).PageCount);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DescendingListsAreReturnedAscending(bool sortQuery)
    {
        using var handler = new FakeHandler(_ => Html(string.Concat(Enumerable.Range(1, 6).Reverse().Select(i =>
            $"<a href='/read_{i}.html'>第{i}章 占位</a>"))));
        using var importer = new WebNovelImporter(Options, handler);
        var result = await importer.AnalyzeAsync(Url("book" + (sortQuery ? "?sort=desc" : "")));
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"/read_{i}.html"), result.Chapters.Select(c => c.Url.AbsolutePath));
    }

    [Fact]
    public async Task ChineseChapterNumbersDetermineReadingOrderInsteadOfOpaqueIds()
    {
        using var handler = new FakeHandler(_ => Html("<a href='chapter/101'>第三章 占位</a>" +
            "<a href='chapter/102'>第二章 占位</a><a href='chapter/103'>第一章 占位</a>"));
        using var importer = new WebNovelImporter(Options, handler);
        var result = await importer.AnalyzeAsync(Url("book"));
        Assert.Equal(new[] { "第一章 占位", "第二章 占位", "第三章 占位" }, result.Chapters.Select(c => c.Title));
    }

    [Fact]
    public async Task ScrambledNumericNavigationIsNotAChapterList()
    {
        using var handler = new FakeHandler(_ => Html("<a href='item/3'>3</a><a href='item/1'>1</a><a href='item/2'>2</a>"));
        using var importer = new WebNovelImporter(Options, handler);
        var error = await Assert.ThrowsAsync<WebImportBlockedException>(() => importer.AnalyzeAsync(Url("book")));
        Assert.Equal(WebImportBlockReason.NoChapterList, error.Reason);
    }

    [Fact]
    public async Task OnlyNumberedPaginationSharingChapterStemIsJoined()
    {
        using var handler = new FakeHandler(_ => Html($"<article>{Placeholder}</article>" +
            "<a href='read_6_2.html'>下一章</a><a href='read_4_2.html'>上一页</a><a href='read_6.html'>下一页</a>"));
        using var importer = new WebNovelImporter(Options, handler);
        Assert.Equal(1, (await importer.FetchChapterAsync(Link())).PageCount);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task RepeatedShortSiteLabelsAreDroppedFromJoinedPagesButRepeatedProseSurvives()
    {
        var prose = string.Concat(Enumerable.Repeat(Placeholder, 5));
        using var handler = new FakeHandler(r => Html($"<article><p>{prose}</p><div>占位站点标签</div></article>" +
            (r.RequestUri!.AbsolutePath.EndsWith("_2.html", StringComparison.Ordinal) ? "" : "<a href='read_5_2.html'>下一页</a>")));
        using var importer = new WebNovelImporter(Options, handler);
        var content = await importer.FetchChapterAsync(Link());
        Assert.Equal(2, content.PageCount);
        Assert.Equal(prose + "\n\n" + prose, content.Text);
        var a = new WebChapterContent(Link(), "", prose + "\n占位站点标签", 1);
        var b = new WebChapterContent(Link("read_6.html", "第六章 占位"), "", prose + "\n占位站点标签", 1);
        Assert.DoesNotContain("站点标签", importer.ComposeImportText([a, b]));
        Assert.Contains(prose, importer.ComposeImportText([a, b]));
    }

    [Fact]
    public async Task CacheResumesAcrossImportersWithoutHttpCalls()
    {
        var directory = Path.Combine(Path.GetTempPath(), "qt-web-fixtures-" + Guid.NewGuid().ToString("N"));
        try
        {
            var options = Options with { CacheDirectory = directory };
            using var firstHandler = new FakeHandler(r => Html(r.RequestUri!.AbsolutePath == "/book" ?
                "<h1>测试书名</h1>" + Catalog(3, i => $"/read_{i}.html") : $"<article>{Placeholder}</article>"));
            using (var importer = new WebNovelImporter(options, firstHandler))
            {
                var analysis = await importer.AnalyzeAsync(Url("book"));
                await foreach (var _ in importer.FetchChaptersAsync(analysis.Chapters)) { }
            }
            Assert.Equal(4, firstHandler.Calls.Count);
            using var secondHandler = new FakeHandler(_ => throw new InvalidOperationException("Cache miss"));
            using var resumed = new WebNovelImporter(options, secondHandler);
            var cached = await resumed.AnalyzeAsync(Url("book"));
            await foreach (var chapter in resumed.FetchChaptersAsync(cached.Chapters)) Assert.Equal(Placeholder, chapter.Text);
            Assert.Empty(secondHandler.Calls);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RequestsAreSerializedDelayedAndHaveBrowserHeaders()
    {
        using var handler = new FakeHandler(_ => Html($"<article>{Placeholder}</article>"));
        using var importer = new WebNovelImporter(Options with { DelayBetweenRequests = TimeSpan.FromMilliseconds(35) }, handler);
        await Task.WhenAll(Enumerable.Range(1, 3).Select(i => importer.FetchChapterAsync(Link($"read_{i}.html"))));
        Assert.Equal(1, handler.MaximumActive);
        var calls = handler.Calls.ToArray();
        Assert.All(calls.Zip(calls.Skip(1)), pair => Assert.True(Stopwatch.GetElapsedTime(pair.First.Time, pair.Second.Time) >= TimeSpan.FromMilliseconds(30)));
        Assert.All(calls, c => { Assert.Contains("Mozilla", c.UserAgent); Assert.Contains("zh-CN", c.Language); Assert.Contains("zh-TW", c.Language); });
    }

    [Fact]
    public async Task RetriesTimeoutThen429Then5xxAndReportsProgress()
    {
        var calls = 0;
        using var handler = new FakeHandler(_ =>
        {
            switch (++calls)
            {
                case 1: throw new TaskCanceledException("Synthetic timeout");
                case 2:
                    var limited = Html("rate limit", HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(300));
                    return limited;
                case 3: return Html("temporary failure", HttpStatusCode.ServiceUnavailable);
                default: return Html($"<article>{Placeholder}</article>");
            }
        });
        using var importer = new WebNovelImporter(Options with { MaxRetries = 3 }, handler);
        var progress = new List<WebImportProgress>();
        var chapters = new List<WebChapterContent>();
        await foreach (var chapter in importer.FetchChaptersAsync([Link()], new InlineProgress(progress))) chapters.Add(chapter);
        Assert.Equal(4, handler.Calls.Count);
        Assert.Single(chapters);
        Assert.Equal(new WebImportProgress(1, 1, Link().Title), Assert.Single(progress));
        var timestamps = handler.Calls.ToArray();
        Assert.True(Stopwatch.GetElapsedTime(timestamps[1].Time, timestamps[2].Time) >= TimeSpan.FromMilliseconds(450));
    }

    [Fact]
    public async Task RetryAfterIsMinimumWaitAndRetriesAreBounded()
    {
        using var handler = new FakeHandler(_ =>
        {
            var response = Html("limited", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMilliseconds(350));
            return response;
        });
        using var importer = new WebNovelImporter(Options with { MaxRetries = 1 }, handler);
        await Assert.ThrowsAsync<HttpRequestException>(() => importer.FetchChapterAsync(Link()));
        var calls = handler.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.True(Stopwatch.GetElapsedTime(calls[0].Time, calls[1].Time) >= TimeSpan.FromMilliseconds(340));
    }

    [Fact]
    public async Task CancellationWhileThrottledDoesNotSendAnotherRequest()
    {
        using var handler = new FakeHandler(_ => Html($"<article>{Placeholder}</article>"));
        using var importer = new WebNovelImporter(Options with { DelayBetweenRequests = TimeSpan.FromSeconds(10) }, handler);
        await importer.FetchChapterAsync(Link());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.FetchChapterAsync(Link("read_6.html"), cancellation.Token));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public void ComposeImportTextPreservesHeadingsAndNormalizesWhitespace()
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No HTTP expected"));
        using var importer = new WebNovelImporter(Options, handler);
        var ownHeading = new WebChapterContent(Link(), "unused", "  \r\n第五章 占位  \r\n测试文字 \r\n", 1);
        var needsHeading = new WebChapterContent(Link("read_6.html", "第六章 占位"), "unused", "测试文字 \r占位内容  ", 1);
        Assert.Equal("第五章 占位\n测试文字\n\n第六章 占位\n测试文字\n占位内容", importer.ComposeImportText([ownHeading, needsHeading]));
        Assert.Equal("", importer.ComposeImportText([]));
        Assert.Empty(handler.Calls);
    }

    private static string Catalog(int count, Func<int, string> path) => string.Concat(Enumerable.Range(1, count)
        .Select(i => $"<a href='{path(i)}'>第{i}章 占位</a>"));

    private static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private sealed class InlineProgress(List<WebImportProgress> values) : IProgress<WebImportProgress>
    { public void Report(WebImportProgress value) => values.Add(value); }

    private sealed record RequestLog(Uri Url, long Time, string UserAgent, string Language);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        internal ConcurrentQueue<RequestLog> Calls { get; } = new();
        private int _active;
        internal int MaximumActive { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Enqueue(new(request.RequestUri!, Stopwatch.GetTimestamp(), request.Headers.UserAgent.ToString(), request.Headers.AcceptLanguage.ToString()));
            var active = Interlocked.Increment(ref _active);
            MaximumActive = Math.Max(MaximumActive, active);
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                return response(request);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }
}
