using System.Net;
using System.Text;
using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Tests.WebImport;

public sealed class WebImportBoundsTests
{
    private static readonly Uri Chapter = new("https://fixtures.invalid/book/read_1.html");

    [Fact]
    public async Task Page_larger_than_the_limit_is_not_read_into_memory()
    {
        string html = "<html><body><article>" + new string('测', 4096) + "。</article></body></html>";
        using var handler = new StaticHandler(html);
        using var importer = new WebNovelImporter(new() { DelayBetweenRequests = TimeSpan.Zero, MaxRetries = 0, MaxPageBytes = 1024 }, handler);
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => importer.FetchChapterAsync(new(1, "第一章", Chapter)));
    }

    [Fact]
    public void Non_positive_page_limit_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebNovelImporter(new() { MaxPageBytes = 0 }, new StaticHandler("")));

    [Fact]
    public void Cancellation_stops_streamed_script_extraction()
    {
        string html = "<html><body><div>短</div><script>self.__next_f.push([1,\"" + new string('测', 200) + "\"])</script></body></html>";
        var page = WebPage.Parse(Chapter, Encoding.UTF8.GetBytes(html), "utf-8");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => ChapterContentExtractor.ExtractWithSignature(page, null, cancelled.Token));
    }

    private sealed class StaticHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") });
    }
}
