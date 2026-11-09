using System.Net;
using System.Text;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Tests.WebImport;

/// <summary>
/// 52shuku.net splits a whole book into numbered pages (第1页…), each holding parts of several chapters whose 第N章 headings
/// sit inside the text. Fixtures are trimmed copies of the live markup with placeholder story text.
/// </summary>
public sealed class Shuku52ImportTests
{
    private const string Book = "https://www.52shuku.net/yanqing/14_b/bkash";
    private const string Prose = "占位正文占位正文占位正文占位正文占位正文占位正文。";
    private static WebImportOptions Options => new() { DelayBetweenRequests = TimeSpan.Zero, MaxRetries = 0 };

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WebImport", "52shuku", name));

    private static string P(string text) => $"<p>　　{text}</p>\n";

    // Page n of the book lives at bkash_{n+1}.html; page 1's previous link is the book page itself.
    private static readonly string[] Bodies =
    [
        P("[现代情感] 《测试书名》作者：测试作者【完结+番外】") + P("本书简介:") + P("简介占位简介占位简介占位。") +
            P("第1章 起始占位") + P(Prose) + P("子t啊，" + Prose) + P(Prose),
        P("续一占位" + Prose) + P(Prose) + P("第2章 中段占位") + P(Prose) + P("阿Q和C位都留下。" + Prose),
        P(Prose) + P("。t靠" + Prose) + P("第3章 番外占位") + P(Prose),
        P(Prose) + P("&lt;全文完&gt;") + P("2025.08.04") + P("小贴士:找看好看得小说，就来52书库呀~www.52shuku.net"),
    ];

    private static string Page(int n)
    {
        string prev = n == 1 ? Book + ".html" : $"{Book}_{n}.html";
        string nav = n == Bodies.Length ? "" : $"<a href=\"{Book}_{n + 2}.html\">下一页</a>&nbsp;<a href=\"{Book}_{Bodies.Length + 1}.html\">尾页</a>";
        return Fixture("page.html").Replace("@@N@@", (n + 1).ToString()).Replace("@@PREV@@", prev)
            .Replace("@@NAV@@", nav).Replace("@@BODY@@", Bodies[n - 1]);
    }

    private static HttpResponseMessage Respond(HttpRequestMessage request)
    {
        string path = request.RequestUri!.AbsolutePath;
        string? html = path == "/yanqing/14_b/bkash.html" ? Fixture("index.html")
            : System.Text.RegularExpressions.Regex.Match(path, @"^/yanqing/14_b/bkash_(\d+)\.html$") is { Success: true } m &&
              int.Parse(m.Groups[1].Value) - 1 is int n and >= 1 && n <= Bodies.Length ? Page(n) : null;
        return html is null ? new(HttpStatusCode.NotFound)
            : new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };
    }

    [Fact]
    public async Task BookPage_ListsEveryPageInOrder_IncludingThePageBehindTheStartButton()
    {
        using var handler = new Handler(Respond);
        using var importer = new WebNovelImporter(Options, handler);

        WebBookAnalysis analysis = await importer.AnalyzeAsync(new Uri(Book + ".html"));

        Assert.Equal("测试书名", analysis.BookTitle);
        Assert.True(analysis.ContinuousPages);
        Assert.False(analysis.NumberingConflict);
        Assert.Equal(new[] { "第1页", "第2页", "第3页", "第4页" }, analysis.Chapters.Select(c => c.Title));
        Assert.Equal(Enumerable.Range(2, 4).Select(i => $"{Book}_{i}.html"), analysis.Chapters.Select(c => c.Url.AbsoluteUri));
        Assert.DoesNotContain(analysis.Chapters, c => c.Url.AbsoluteUri.Contains("bkasg") || c.Url.AbsoluteUri.Contains("bkasi"));
    }

    [Fact]
    public async Task Page_KeepsOnlyTheStoryAndDropsSiteNoticesAndWatermark()
    {
        using var handler = new Handler(Respond);
        using var importer = new WebNovelImporter(Options, handler);
        WebBookAnalysis analysis = await importer.AnalyzeAsync(new Uri(Book + ".html"));

        WebChapterContent first = await importer.FetchChapterAsync(analysis.Chapters[0]);

        // A page is never followed into the next page by itself: the list already holds every page.
        Assert.Equal(1, first.PageCount);
        Assert.StartsWith("[现代情感] 《测试书名》", first.Text);
        Assert.Contains("第1章 起始占位", first.Text);
        Assert.Contains("子啊，", first.Text);
        Assert.DoesNotContain("续一占位", first.Text);
        foreach (string junk in new[] { "52书库", "52shuku", "传送门", "排行榜单", "哦豁", "上一页", "下一页", "目录", "阅读记录" })
            Assert.DoesNotContain(junk, first.Text);
    }

    [Fact]
    public async Task ComposedBook_JoinsPagesSoChaptersSplitOnTheirOwnHeadings()
    {
        using var handler = new Handler(Respond);
        using var importer = new WebNovelImporter(Options, handler);
        WebBookAnalysis analysis = await importer.AnalyzeAsync(new Uri(Book + ".html"));
        var pages = new List<WebChapterContent>();
        await foreach (WebChapterContent page in importer.FetchChaptersAsync(analysis.Chapters)) pages.Add(page);

        string text = importer.ComposeImportText(pages, keepSiteOrder: true);

        Assert.DoesNotContain("页", text);
        Assert.DoesNotContain("52书库", text);
        Assert.DoesNotContain("t", text.Replace("2025.08.04", ""));
        Assert.Contains("阿Q和C位都留下。", text);
        // Page 2 continues chapter 1, so it stays under chapter 1 instead of getting a heading of its own.
        Assert.True(text.IndexOf("续一占位", StringComparison.Ordinal) < text.IndexOf("第2章", StringComparison.Ordinal));
        Assert.EndsWith("<全文完>\n2025.08.04", text);

        ImmutableImportPlan plan = new ChapterDetector().Detect(RawSourceReader.FromText(text));
        ImportPreviewItem[] chapters = ImportPreviewEditor.Create(plan).Current.ImportableChapters.Where(c => c.IsChapter).ToArray();
        Assert.Equal(new int?[] { 1, 2, 3 }, chapters.Select(c => c.Number));
        Assert.Equal(new[] { "起始占位", "中段占位", "番外占位" }, chapters.Select(c => c.Title));
    }

    [Theory]
    [InlineData("https://www.52shuku.net/a.html", "子t啊。t靠", "子啊。靠")]
    [InlineData("https://www.52shuku.net/a.html", "阿Q, C位, test。", "阿Q, C位, test。")]
    [InlineData("https://other.invalid/a.html", "子t啊", "子t啊")]
    public void Watermark_IsRemovedOnlyOn52shuku(string url, string input, string expected) =>
        Assert.Equal(expected, ChapterContentExtractor.SiteCleanup(new Uri(url), input));

    [Theory]
    [InlineData("哦豁，小伙伴们如果觉得52书库不错，记得收藏网址 https://www.52shuku.net/ 或推荐给朋友哦~拜托啦 (>.<)", true)]
    [InlineData("小贴士:找看好看得小说，就来52书库呀~www.52shuku.net", true)]
    [InlineData("他把网址写在纸上递给她，说这是他小时候最喜欢的游戏论坛，后来倒闭了，现在只剩下一个空壳，偶尔还能打开首页看看当年的留言和照片，那些照片里有他们一起熬夜的样子，也有他们第一次见面的那天，www.example.com 也还在。", false)]
    [InlineData("她笑着说：“走吧。”", false)]
    public void ShortLineWithWebAddress_IsSiteNotice(string line, bool boilerplate) =>
        Assert.Equal(boilerplate, ChapterContentExtractor.IsBoilerplate(line));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
