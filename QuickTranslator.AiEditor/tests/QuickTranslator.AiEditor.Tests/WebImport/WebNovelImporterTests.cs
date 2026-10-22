using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Importing;
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
    public async Task NextFlight_HtmlTextRecordKeepsParagraphsAndBeatsALongerChapterTitleList()
    {
        // Seen live: the chapter is an HTML T-record with <p> paragraphs, and a model record listing chapter titles
        // has slightly more CJK characters but almost no sentence ends.
        string body = string.Concat(Enumerable.Range(1, 4).Select(_ => $"<p>{Placeholder}</p>"));
        int bytes = Encoding.UTF8.GetByteCount(body);
        string titles = string.Concat(Enumerable.Range(1, 40).Select(i => $"第{i}章 占位标题名称 "));
        string payload = $"2:[\"$\",\"ul\",null,{{\"children\":{JsonSerializer.Serialize(titles)}}}]\n5:T{bytes:x},{body}";
        var html = "<div id='__next'></div><script>self.__next_f.push([1," + JsonSerializer.Serialize(payload) + "])</script>";
        using var handler = new FakeHandler(_ => Html(html));
        using var importer = new WebNovelImporter(Options, handler);
        string text = (await importer.FetchChapterAsync(Link())).Text;
        Assert.Equal(string.Join("\n", Enumerable.Repeat(Placeholder, 4)), text);
    }

    [Fact]
    public async Task Start_reading_button_does_not_steal_chapter_one_title_and_every_chapter_gets_a_heading()
    {
        // Seen live: a "开始阅读" button before the list links to chapter 1, so chapter 1 was imported without a heading
        // and ended up outside every chapter in the TXT import.
        string book = "<h1>测试书名</h1><a href='/novel/test/chapter/1'>开始阅读</a>" +
            string.Concat(Enumerable.Range(1, 5).Select(i => $"<a href='/novel/test/chapter/{i}'>第{i}回 占位标题</a>"));
        using var handler = new FakeHandler(r => Html(r.RequestUri!.AbsolutePath == "/novel/test"
            ? book : $"<article>{Placeholder}</article>"));
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(Url("novel/test"));
        Assert.Equal(5, analysis.Chapters.Count);
        Assert.Equal("第1回 占位标题", analysis.Chapters[0].Title);
        var chapters = new List<WebChapterContent>();
        await foreach (var chapter in importer.FetchChaptersAsync(analysis.Chapters)) chapters.Add(chapter);
        string text = importer.ComposeImportText(chapters);
        Assert.StartsWith("第1回 占位标题\n", text);
        Assert.Equal(5, Regex.Matches(text, @"(?m)^第\d+回 ").Count);
        // A title without any number still becomes a heading the TXT import can split on.
        var untitled = chapters[0] with { Link = chapters[0].Link with { Title = "楔子" } };
        Assert.StartsWith("第1章 楔子\n", importer.ComposeImportText([untitled]));
    }

    [Fact]
    public async Task Toc_row_cells_become_a_clean_title_without_counter_or_posting_date()
    {
        // Seen live: each row links "<span>第 N 章</span><span>第N回 标题</span><span>10-03</span>".
        string rows = string.Concat(Enumerable.Range(1, 5).Select(i =>
            $"<a href='/novel/test/chapter/{i}'><span>第 {i} 章</span><span>第{i}回 占位标题</span><span>10-0{i}</span></a>"));
        rows += "<a href='/novel/test/chapter/6'><span>第 6 章</span><span>楔子</span><span>2026-10-06 12:30</span></a>";
        using var handler = new FakeHandler(_ => Html("<h1>测试书名</h1>" + rows));
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(Url("novel/test"));
        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"第{i}回 占位标题").Append("第 6 章 楔子"),
            analysis.Chapters.Select(chapter => chapter.Title));
    }

    [Fact]
    public async Task Directory_with_out_of_order_title_numbers_is_ordered_by_its_urls()
    {
        // Seen live on a dir.html: one title carries an unrelated number, which used to reject the whole list.
        string catalog = string.Concat(Enumerable.Range(1, 20).Select(i => i == 7
            ? $"<a href='/book1/read_{i}.html'>番外 第100章 占位</a>"
            : $"<a href='/book1/read_{i}.html'>第{i}章 占位</a>"));
        using var handler = new FakeHandler(_ => Html("<h1>测试书名</h1>" + catalog));
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(Url("book1/dir.html"));
        Assert.Equal(Enumerable.Range(1, 20).Select(i => $"/book1/read_{i}.html"),
            analysis.Chapters.Select(chapter => chapter.Url.AbsolutePath));
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
            // Like the live page: some title / menu text (~70 CJK) around client-side templates.
            _ => $"<div class='title'>{Placeholder}{Placeholder}{Placeholder}</div>" +
                 "<div id='app'><a :href='v.url'>{{ v.url }}</a>{{ chapter.content }}</div><script src='app.js'></script>"
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
    public async Task CacheResumesChaptersAcrossImportersButReloadsTheBookPage()
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
            // The book page is always fetched again (new chapters appear there); chapter pages come from the cache.
            using var secondHandler = new FakeHandler(r => r.RequestUri!.AbsolutePath == "/book"
                ? Html("<h1>测试书名</h1>" + Catalog(3, i => $"/read_{i}.html"))
                : throw new InvalidOperationException("Cache miss"));
            using var resumed = new WebNovelImporter(options, secondHandler);
            var cached = await resumed.AnalyzeAsync(Url("book"));
            await foreach (var chapter in resumed.FetchChaptersAsync(cached.Chapters)) Assert.Equal(Placeholder, chapter.Text);
            Assert.Equal(["/book"], secondHandler.Calls.Select(call => call.Url.AbsolutePath));
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

    [Theory]
    [InlineData("第1章 起|第2章 承|第3章 转|第4章 合|第5章 终", false)]
    [InlineData("第一章 起|第二章 承|第三章 转|第四章 合|第五章 终", false)]
    [InlineData("第1章 起|第2章 承|第2章 番外|第4章 合|第5章 终", true)]
    [InlineData("第1章 起|第2章 承|第6章 番外|第3章 合|第4章 终", true)]
    [InlineData("第一章 起|第二章 承|第2回 番外|第四章 合|第五章 终", true)]
    [InlineData("番外 100|第1章 起|无编号|第二章 承|第3章 合", false)]
    public async Task Analyze_detects_duplicate_or_reversed_title_numbers(string titles, bool conflict)
    {
        string[] expected = titles.Split('|');
        string catalog = string.Concat(expected.Select((title, i) => $"<a href='/novel/test/chapter/{i + 1}'>{title}</a>"));
        using var handler = new FakeHandler(_ => Html("<h1>测试书名</h1>" + catalog));
        using var importer = new WebNovelImporter(Options, handler);
        var analysis = await importer.AnalyzeAsync(Url("novel/test"));
        Assert.Equal(expected, analysis.Chapters.Select(c => c.Title));
        Assert.Equal(conflict, analysis.NumberingConflict);
        if (conflict) Assert.Contains(analysis.Warnings, w => w.Contains("Giữ đúng thứ tự mục lục"));
        else Assert.Empty(analysis.Warnings);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposeImportText_can_keep_site_order_without_duplicate_headings(bool keepSiteOrder)
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No HTTP expected"));
        using var importer = new WebNovelImporter(Options, handler);
        WebChapterContent[] chapters =
        [
            new(new(1, "第29章 正文", Url("chapter/1")), "unused", "第29章 正文\n正文内容。", 1),
            new(new(2, "第 60 章 番外 X", Url("chapter/2")), "unused", "番外内容。", 1),
            new(new(3, "楔子", Url("chapter/3")), "unused", "楔子内容。", 1)
        ];
        string expected = keepSiteOrder
            ? "第1章 正文\n正文内容。\n\n第2章 番外 X\n番外内容。\n\n第3章 楔子\n楔子内容。"
            : "第29章 正文\n正文内容。\n\n第 60 章 番外 X\n番外内容。\n\n第3章 楔子\n楔子内容。";
        Assert.Equal(expected, importer.ComposeImportText(chapters, keepSiteOrder));
        if (!keepSiteOrder) Assert.Equal(expected, importer.ComposeImportText(chapters));
        Assert.Equal("", importer.ComposeImportText([], keepSiteOrder));
        Assert.Empty(handler.Calls);
    }

    [Theory]
    [InlineData("第 64 章 第29回 正文")]
    [InlineData("第六十四节 第兩十九節 正文")]
    [InlineData("第64卷 第29章 正文")]
    public void ComposeImportText_strips_multiple_counters_from_link_or_own_heading(string title)
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No HTTP expected"));
        using var importer = new WebNovelImporter(Options, handler);
        var link = new WebChapterLink(64, title, Url("chapter/64"));
        Assert.Equal("第64章 正文\n内容。", importer.ComposeImportText([new(link, "unused", "内容。", 1)], true));
        Assert.Equal("第64章 正文\n内容。", importer.ComposeImportText([new(link with { Title = "链接标题" }, "unused", title + "\n内容。", 1)], true));
        Assert.Equal("第64章 正文", importer.ComposeImportText([new(link, "unused", title, 1)], true));
    }

    [Fact]
    public void Keep_site_order_preserves_all_130_chapters_in_txt_preview()
    {
        using var handler = new FakeHandler(_ => throw new InvalidOperationException("No HTTP expected"));
        using var importer = new WebNovelImporter(Options, handler);
        var chapters = Enumerable.Range(1, 130).Select(i =>
        {
            string title = i % 3 == 0 ? $"第 {i} 章 番外 {i}" : $"第{i / 2 + 1}章 正文 {i}";
            var link = new WebChapterLink(i, title, Url($"chapter/{i}"));
            return new WebChapterContent(link, title, $"这是第{i}段独立正文。", 1);
        }).ToArray();
        string text = importer.ComposeImportText(chapters, true);
        var preview = ImportPreviewEditor.Create(new ChapterDetector().Detect(text)).Current;
        var imported = preview.ImportableChapters.ToArray();
        Assert.Equal(chapters.Length, imported.Length);
        Assert.Equal(Enumerable.Range(1, 130).Select(i => (int?)i), imported.Select(c => c.Number));
        Assert.Equal(chapters.Select(c => c.Text), imported.Select(c => preview.Slice(c.ContentSpan).Trim()));
        Assert.False(preview.WasAutomaticallyReordered);
        Assert.Equal(0, preview.AutomaticallySkippedDuplicateCount);
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
