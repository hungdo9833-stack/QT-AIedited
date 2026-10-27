using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Publishing;

namespace QuickTranslator.AiEditor.Tests;

public sealed class VanCacPublisherTests
{
    private const string Token = "vcp_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Base = "https://van-cac-staging.example.workers.dev";

    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth, string? Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static VanCacPublisherClient Client(FakeHandler handler, List<TimeSpan>? delays = null) =>
        new(Base, Token, handler, (delay, _) => { delays?.Add(delay); return Task.CompletedTask; });

    [Theory]
    [InlineData("https://vancac.example.com", "https://vancac.example.com/")]
    [InlineData("https://vancac.example.com/", "https://vancac.example.com/")]
    [InlineData("http://127.0.0.1:8791", "http://127.0.0.1:8791/")]
    [InlineData("http://localhost:4321/", "http://localhost:4321/")]
    public void BaseUrlAcceptsHttpsOrLoopbackHttp(string input, string expected) =>
        Assert.Equal(expected, VanCacPublisherClient.NormalizeBaseUrl(input).ToString());

    [Theory]
    [InlineData("http://vancac.example.com")]
    [InlineData("https://vancac.example.com/admin")]
    [InlineData("https://user:pass@vancac.example.com")]
    [InlineData("ftp://vancac.example.com")]
    [InlineData("vancac.example.com")]
    [InlineData("")]
    public void BaseUrlRejectsUnsafeOrNonRootAddresses(string input) =>
        Assert.Throws<ArgumentException>(() => VanCacPublisherClient.NormalizeBaseUrl(input));

    [Fact]
    public void TokenFormatIsChecked()
    {
        Assert.True(VanCacPublisherClient.IsValidToken(Token));
        Assert.False(VanCacPublisherClient.IsValidToken(Token.ToUpperInvariant()));
        Assert.False(VanCacPublisherClient.IsValidToken("vcp_short"));
        Assert.Throws<ArgumentException>(() => new VanCacPublisherClient(Base, "Bearer " + Token));
    }

    [Fact]
    public async Task PutChapterSendsBearerJsonAndParsesAction()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.Created,
            """{"action":"created","item":{"id":"c1","publicationStatus":"draft","number":12}}"""));
        using VanCacPublisherClient client = Client(handler);
        VanCacPutResult result = await client.PutChapterAsync("novel/1", 12, "  Trở về  ", "Đoạn 1\n\nĐoạn 2\n", VanCacChapterStatus.Published);
        Assert.Equal(new VanCacPutResult(VanCacPutAction.Created, "c1", "draft"), result);
        var call = Assert.Single(handler.Calls);
        Assert.Equal(HttpMethod.Put, call.Method);
        Assert.Equal(Base + "/api/publisher/v1/novels/novel%2F1/chapters/12", call.Url);
        Assert.Equal("Bearer " + Token, call.Auth);
        JsonNode body = JsonNode.Parse(call.Body!)!;
        Assert.Equal("Trở về", (string?)body["title"]);
        Assert.Equal("Đoạn 1\n\nĐoạn 2", (string?)body["content"]);
        Assert.Equal("published", (string?)body["status"]);
    }

    [Fact]
    public async Task OmittedStatusIsNotSent()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, """{"action":"unchanged","item":{"id":"c1","publicationStatus":"published"}}"""));
        using VanCacPublisherClient client = Client(handler);
        Assert.Equal(VanCacPutAction.Unchanged, (await client.PutChapterAsync("n", 1, "A", "B", null)).Action);
        Assert.Null(JsonNode.Parse(handler.Calls[0].Body!)!["status"]);
    }

    [Fact]
    public async Task TransientFailuresAreRetriedWithBackoff()
    {
        int calls = 0;
        var handler = new FakeHandler((_, _) => ++calls < 3
            ? new HttpResponseMessage(calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.TooManyRequests)
            : Json(HttpStatusCode.OK, """{"user":{"displayName":"Tác giả","roles":["READER","AUTHOR"],"authorId":"a1"}}"""));
        var delays = new List<TimeSpan>();
        using VanCacPublisherClient client = Client(handler, delays);
        VanCacUser user = await client.GetMeAsync();
        Assert.Equal("a1", user.AuthorId);
        Assert.Equal(3, calls);
        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
    }

    [Fact]
    public async Task GivesUpAfterFourAttempts()
    {
        var handler = new FakeHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway));
        using VanCacPublisherClient client = Client(handler);
        var error = await Assert.ThrowsAsync<VanCacApiException>(() => client.GetMeAsync());
        Assert.Equal(502, error.StatusCode);
        Assert.Equal(4, handler.Calls.Count);
    }

    [Fact]
    public async Task AuthAndValidationErrorsAreNotRetriedAndKeepServerMessage()
    {
        var handler = new FakeHandler((request, _) => request.Method == HttpMethod.Get
            ? Json(HttpStatusCode.Unauthorized, """{"error":{"message":"Mã kết nối không hợp lệ."}}""")
            : Json(HttpStatusCode.UnprocessableEntity, """{"error":{"message":"Kiểm tra lại.","fields":{"title":"Nhập 1–200 ký tự."}}}"""));
        using VanCacPublisherClient client = Client(handler);
        var auth = await Assert.ThrowsAsync<VanCacApiException>(() => client.GetMeAsync());
        Assert.True(auth.IsAuthFailure);
        Assert.Equal("Mã kết nối không hợp lệ.", auth.Message);
        var invalid = await Assert.ThrowsAsync<VanCacApiException>(() => client.PutChapterAsync("n", 1, "A", "B", null));
        Assert.False(invalid.IsAuthFailure);
        Assert.Equal("Nhập 1–200 ký tự.", invalid.Fields["title"]);
        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task ListsFollowPagination()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.Query.Contains("page=1&")
            ? Json(HttpStatusCode.OK, """{"items":[{"id":"n1","slug":"a","title":"A","authorId":"x","author":"X","publicationStatus":"draft","chapterCount":3}],"pagination":{"page":1,"pageSize":100,"total":2,"totalPages":2}}""")
            : Json(HttpStatusCode.OK, """{"items":[{"id":"n2","slug":"b","title":"B","authorId":"x","author":"X","publicationStatus":"published","chapterCount":0}],"pagination":{"page":2,"pageSize":100,"total":2,"totalPages":2}}"""));
        using VanCacPublisherClient client = Client(handler);
        IReadOnlyList<VanCacNovel> novels = await client.ListNovelsAsync();
        Assert.Equal(["n1", "n2"], novels.Select(n => n.Id));
        Assert.All(handler.Calls, call => Assert.Contains("pageSize=100", call.Url));
    }

    [Fact]
    public void ChapterLimitsMirrorTheWebsite()
    {
        VanCacPublisherClient.ValidateChapter(1, "A", "B");
        Assert.Throws<ArgumentOutOfRangeException>(() => VanCacPublisherClient.ValidateChapter(0, "A", "B"));
        Assert.Throws<ArgumentException>(() => VanCacPublisherClient.ValidateChapter(1, " ", "B"));
        Assert.Throws<ArgumentException>(() => VanCacPublisherClient.ValidateChapter(1, new string('x', 201), "B"));
        Assert.Throws<ArgumentException>(() => VanCacPublisherClient.ValidateChapter(1, "A", "  "));
        Assert.Throws<ArgumentException>(() => VanCacPublisherClient.ValidateChapter(1, "A", new string('x', 200_001)));
    }

    [Theory]
    [InlineData("Mộng Vân Sơn – Quyển 1!", "mong-van-son-quyen-1")]
    [InlineData("Đường về Đại Lý", "duong-ve-dai-ly")]
    [InlineData("  ---  ", "")]
    [InlineData("Kiếm Lai 2.0", "kiem-lai-2-0")]
    public void SlugFollowsTheWebsiteRule(string title, string slug)
    {
        Assert.Equal(slug, VanCacSlug.FromTitle(title));
        Assert.Equal(slug.Length > 0, VanCacSlug.IsValid(slug));
    }

    [Theory]
    [InlineData(12, "Chương 12: Trở về", "Trở về")]
    [InlineData(12, "Chương 012 - Trở về", "Trở về")]
    [InlineData(5, "第5章 归来", "归来")]
    [InlineData(12, "Chương 13: Khác số", "Chương 13: Khác số")]
    [InlineData(7, "Gặp lại cố nhân", "Gặp lại cố nhân")]
    [InlineData(9, "", "Chương 9")]
    [InlineData(9, "Chương 9", "Chương 9")]
    public void WebTitleDropsOnlyAMatchingNumberPrefix(int number, string title, string expected) =>
        Assert.Equal(expected, VanCacPublishRunner.WebTitle(number, title));

    private sealed class FakeSink(Func<int, VanCacPutResult> respond) : IVanCacChapterSink
    {
        public List<(int Number, string Title, VanCacChapterStatus? Status)> Sent { get; } = [];
        public Task<VanCacPutResult> PutChapterAsync(string novelId, int number, string title, string content,
            VanCacChapterStatus? status, CancellationToken cancellationToken)
        {
            Sent.Add((number, title, status));
            return Task.FromResult(respond(number));
        }
    }

    private static async IAsyncEnumerable<ProjectPublicationChapter> Chapters(params ProjectPublicationChapter[] chapters)
    {
        foreach (ProjectPublicationChapter chapter in chapters) { await Task.Yield(); yield return chapter; }
    }

    private static ProjectPublicationChapter Chapter(int number, string text = "Nội dung", bool complete = true, bool inspect = false) =>
        new(Guid.NewGuid(), number, $"Chương {number}: Tên {number}", text, complete, inspect);

    [Fact]
    public async Task RunnerSkipsIncompleteUnlessAllowedAndCountsOutcomes()
    {
        var sink = new FakeSink(number => new(number switch { 1 => VanCacPutAction.Created, 2 => VanCacPutAction.Updated, _ => VanCacPutAction.Unchanged }, "c", "draft"));
        var seen = new List<VanCacPublishProgress>();
        VanCacPublishReport report = await VanCacPublishRunner.RunAsync(
            Chapters(Chapter(1), Chapter(2), Chapter(3), Chapter(4, complete: false), Chapter(5, inspect: true), Chapter(6, text: "  ")),
            6, sink, new("n", VanCacChapterStatus.Draft, AllowIncomplete: false), new SyncProgress(seen.Add));
        Assert.Equal((1, 1, 1, 1, 2), (report.Created, report.Updated, report.Unchanged, report.SkippedEmpty, report.SkippedIncomplete));
        Assert.True(report.Completed);
        Assert.Equal([1, 2, 3], sink.Sent.Select(s => s.Number));
        Assert.All(sink.Sent, s => Assert.Equal(VanCacChapterStatus.Draft, s.Status));
        Assert.Equal("Tên 1", sink.Sent[0].Title);
        Assert.Equal(6, seen.Count);
        Assert.Equal(6, seen[^1].Done);

        var allowAll = new FakeSink(_ => new(VanCacPutAction.Created, "c", "published"));
        VanCacPublishReport loose = await VanCacPublishRunner.RunAsync(Chapters(Chapter(4, complete: false), Chapter(5, inspect: true)), 2,
            allowAll, new("n", VanCacChapterStatus.Published, AllowIncomplete: true));
        Assert.Equal(2, loose.Created);
    }

    [Fact]
    public async Task RunnerRecordsChapterErrorsButStopsOnAuthFailure()
    {
        var sink = new FakeSink(number => number switch
        {
            2 => throw new VanCacApiException(422, "Tiêu đề quá dài."),
            4 => throw new VanCacApiException(401, "Mã kết nối đã bị thu hồi."),
            _ => new(VanCacPutAction.Created, "c", "draft")
        });
        VanCacPublishReport report = await VanCacPublishRunner.RunAsync(Chapters(Chapter(1), Chapter(2), Chapter(3), Chapter(4), Chapter(5)), 5,
            sink, new("n", null, false));
        Assert.Equal(2, report.Created);
        Assert.Equal([2, 4], report.Failures.Select(f => f.Number));
        Assert.Equal("Mã kết nối đã bị thu hồi.", report.AbortReason);
        Assert.False(report.Completed);
        Assert.DoesNotContain(sink.Sent, s => s.Number == 5);
    }

    [Fact]
    public async Task RunnerStopsBeforeTheNextChapterWhenCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new FakeSink(number => { if (number == 2) cancellation.Cancel(); return new(VanCacPutAction.Created, "c", "draft"); });
        VanCacPublishReport report = await VanCacPublishRunner.RunAsync(Chapters(Chapter(1), Chapter(2), Chapter(3)), 3, sink,
            new("n", null, false), cancellationToken: cancellation.Token);
        Assert.True(report.Cancelled);
        Assert.Equal(2, report.Created);
        Assert.Equal([1, 2], sink.Sent.Select(s => s.Number));
    }

    [Fact]
    public async Task RunnerReportsProjectChangedMidRun()
    {
        static async IAsyncEnumerable<ProjectPublicationChapter> Changing()
        {
            yield return Chapter(1);
            await Task.Yield();
            throw new InvalidOperationException("Project đã thay đổi trong lúc xuất bản.");
        }
        VanCacPublishReport report = await VanCacPublishRunner.RunAsync(Changing(), 2, new FakeSink(_ => new(VanCacPutAction.Created, "c", "draft")),
            new("n", null, false));
        Assert.Equal(1, report.Created);
        Assert.Contains("Project đã thay đổi", report.AbortReason);
    }

    private sealed class SyncProgress(Action<VanCacPublishProgress> report) : IProgress<VanCacPublishProgress>
    {
        public void Report(VanCacPublishProgress value) => report(value);
    }
}
