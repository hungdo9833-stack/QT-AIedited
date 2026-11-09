using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Publishing;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class VanCacBatchPublishTests
{
    private const string Token = "vcp_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static VanCacNovel Novel(string id, string title) => new(id, id, title, "a1", "A", "published", 0);

    [Fact]
    public void A_book_goes_to_its_link_then_a_same_title_novel_then_a_new_one()
    {
        VanCacNovel linked = Novel("n1", "Tên khác"), same = Novel("n2", "Kiếp Này");
        Assert.Same(linked, VanCacBatchPlanner.FindTarget([linked, same], "n1", "Kiếp Này"));
        Assert.Same(same, VanCacBatchPlanner.FindTarget([linked, same], "gone", " kiếp này "));
        Assert.Null(VanCacBatchPlanner.FindTarget([linked], null, "Kiếp Này"));
    }

    [Fact]
    public void New_novel_slugs_stay_valid_and_change_on_retry()
    {
        var id = Guid.Parse("0123456789abcdef0123456789abcdef");
        Assert.Equal("kiep-nay", VanCacBatchPlanner.NewSlug("Kiếp Này", id, 0));
        Assert.Equal("kiep-nay-012345", VanCacBatchPlanner.NewSlug("Kiếp Này", id, 1));
        Assert.Equal("truyen", VanCacBatchPlanner.NewSlug("今生", id, 0));
        string longSlug = VanCacBatchPlanner.NewSlug(new string('a', 200), id, 2);
        Assert.True(VanCacSlug.IsValid(longSlug));
        Assert.EndsWith("-01234567", longSlug);
    }

    private sealed class FakeSite : HttpMessageHandler
    {
        public Dictionary<string, (string Title, HashSet<int> Chapters)> Novels { get; } = new()
        {
            ["n1"] = ("Truyện thử tổng hợp", [1])
        };
        public List<string> Calls { get; } = [];
        public string? CreatedSlug { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            Calls.Add(request.Method + " " + path);
            if (path.EndsWith("/me", StringComparison.Ordinal))
                return Json("""{"user":{"displayName":"Người viết","roles":["AUTHOR"],"authorId":"a1"}}""");
            Match put = Regex.Match(path, "/novels/([^/]+)/chapters/(\\d+)$");
            if (request.Method == HttpMethod.Put && put.Success)
            {
                bool added = Novels[put.Groups[1].Value].Chapters.Add(int.Parse(put.Groups[2].Value));
                return Json($$$"""{"action":"{{{(added ? "created" : "unchanged")}}}","item":{"id":"c","publicationStatus":"draft"}}""");
            }
            Match chapters = Regex.Match(path, "/novels/([^/]+)/chapters$");
            if (chapters.Success)
            {
                int[] numbers = Novels[chapters.Groups[1].Value].Chapters.Order().ToArray();
                string items = string.Join(",", numbers.Select(n => $$"""{"id":"c{{n}}","number":{{n}},"title":"T","publicationStatus":"draft"}"""));
                return Json($$$"""{"items":[{{{items}}}],"pagination":{"page":1,"pageSize":100,"total":{{{numbers.Length}}},"totalPages":1}}""");
            }
            if (request.Method == HttpMethod.Post && path.EndsWith("/novels", StringComparison.Ordinal))
            {
                string body = await request.Content!.ReadAsStringAsync(cancellationToken);
                CreatedSlug = Regex.Match(body, "\"slug\":\"([^\"]+)\"").Groups[1].Value;
                Novels["n2"] = ("Truyện mới tinh", []);
                return Json("""{"item":{"id":"n2","slug":"truyen-moi-tinh","title":"Truyện mới tinh","authorId":"a1","author":"A","publicationStatus":"draft","chapterCount":0}}""", HttpStatusCode.Created);
            }
            if (path.EndsWith("/novels", StringComparison.Ordinal))
            {
                string items = string.Join(",", Novels.Select(n => $$"""{"id":"{{n.Key}}","slug":"{{n.Key}}","title":"{{n.Value.Title}}","authorId":"a1","author":"A","publicationStatus":"published","chapterCount":{{n.Value.Chapters.Count}}}"""));
                return Json($$$"""{"items":[{{{items}}}],"pagination":{"page":1,"pageSize":100,"total":{{{Novels.Count}}},"totalPages":1}}""");
            }
            return Json("""{"error":{"message":"Không có."}}""", HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed class MemoryCredentialStore : IGeminiCredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string target) => _values.GetValueOrDefault(target);
        public void Write(string target, string value) => _values[target] = value;
        public void Delete(string target) => _values.Remove(target);
    }

    private static async Task AddTranslatedChapterAsync(ProjectRepository repository, ProjectSession project, int number)
    {
        ChapterDocument chapter = TranslationService.CreateChapter($"Chương {number}", "你好。王秀兰来了。");
        foreach (var segment in chapter.Segments) segment.UserDraft = $"Bản dịch chương {number}.";
        await repository.AddChapterAsync(project, chapter, number);
    }

    [Fact]
    public Task Ticked_books_upload_one_after_another_and_busy_books_wait() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        for (int n = 1; n <= 3; n++) await AddTranslatedChapterAsync(fixture.Repository, fixture.Project, n);
        ProjectSession fresh = await fixture.Repository.CreateAsync(Path.Combine(fixture.Root, "Fresh"), "Truyện mới tinh");
        for (int n = 1; n <= 2; n++) await AddTranslatedChapterAsync(fixture.Repository, fresh, n);
        ProjectSession busy = await fixture.Repository.CreateAsync(Path.Combine(fixture.Root, "Busy"), "Đang dịch dở");
        await AddTranslatedChapterAsync(fixture.Repository, busy, 1);
        ProjectSession[] sessions = [fixture.Project, fresh, busy];

        string preferences = Path.Combine(fixture.Root, "account.json");
        new VanCacPublisherPreferences
        {
            BaseUrl = "https://sacvien.example", DisplayName = "Người viết", Roles = ["AUTHOR"],
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(1800000000)
        }.Save(preferences);
        var store = new MemoryCredentialStore();
        VanCacTokenVault.Save(store, Token);
        var site = new FakeSite();
        var account = new VanCacAccountManager(preferences, store, site);

        VanCacBatchBook Book(ProjectSession session) =>
            new(session.Manifest.ProjectId, session.Manifest.VietnameseTitle, session.ManifestPath, session.Manifest.Chapters.Count, 0, "", false);
        var source = new VanCacBatchSource(new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog")),
            book => book.ProjectId == busy.Manifest.ProjectId ? "hàng đợi dịch đang chạy" : null,
            (book, _) => Task.FromResult(sessions.Single(s => s.Manifest.ProjectId == book.ProjectId)));
        var window = new VanCacBatchPublishWindow(account, sessions.Select(Book).ToArray(), source)
        {
            Width = 1100, Height = 560, Left = -10000, Top = -10000, ShowActivated = false
        };
        ThemedDialog.Responder = _ => MessageBoxResult.Yes;
        try
        {
            window.Show();
            await window.InitialConnection;
            Assert.Equal(["Truyện thử tổng hợp", "Tạo truyện mới (bản nháp)", "Tạo truyện mới (bản nháp)"], window.Rows.Select(r => r.Target));
            var start = (Button)window.FindName("StartButton");
            Assert.False(start.IsEnabled);
            foreach (VanCacBatchRow row in window.Rows) row.Selected = true;
            Assert.Equal("Đăng 3 truyện", start.Content);
            Assert.True(start.IsEnabled);
            ((CheckBox)window.FindName("AllowIncompleteInput")).IsChecked = true;

            await window.StartAsync();

            // Chapter 1 was already on the website, so only 2 and 3 go up for the matched novel.
            Assert.Equal([1, 2, 3], site.Novels["n1"].Chapters.Order());
            Assert.DoesNotContain(site.Calls, call => call.StartsWith("PUT", StringComparison.Ordinal) && call.EndsWith("/novels/n1/chapters/1", StringComparison.Ordinal));
            Assert.StartsWith("Đã gửi 2: 2 mới", window.Rows[0].Stage);
            Assert.Equal([1, 2], site.Novels["n2"].Chapters.Order());
            Assert.Equal("truyen-moi-tinh", site.CreatedSlug);
            Assert.Equal("Truyện mới tinh", window.Rows[1].Target);
            Assert.Equal("Bỏ qua: hàng đợi dịch đang chạy", window.Rows[2].Stage);
            Assert.Equal("Trên web: 3 chương nháp", window.Rows[0].WebText);
            Assert.Equal("Chưa liên kết", window.Rows[2].WebText);
            Assert.Equal(2, site.Novels.Count);

            VanCacPublisherPreferences saved = VanCacPublisherPreferences.Load(preferences);
            Assert.Equal("n1", saved.NovelByProject[fixture.Project.Manifest.ProjectId]);
            Assert.Equal("n2", saved.NovelByProject[fresh.Manifest.ProjectId]);
            Assert.False(saved.NovelByProject.ContainsKey(busy.Manifest.ProjectId));
            Assert.Equal(3, saved.WebProgressByProject[fixture.Project.Manifest.ProjectId].DraftCount);
            Assert.Contains("1 truyện có lỗi hoặc bị bỏ qua", ((TextBlock)window.FindName("StatusText")).Text);

            window.UpdateLayout();
            Capture(window, "batch-publish.png");

            // A second run finds nothing new and sends nothing.
            int puts = site.Calls.Count(call => call.StartsWith("PUT", StringComparison.Ordinal));
            window.Rows[2].Selected = false;
            await window.StartAsync();
            Assert.Equal(puts, site.Calls.Count(call => call.StartsWith("PUT", StringComparison.Ordinal)));
            Assert.Equal("Không có chương mới để gửi", window.Rows[0].Stage);
        }
        finally
        {
            ThemedDialog.Responder = null;
            window.Close();
        }
    });

    private static void Capture(Window window, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("QTSTUDIO_UI_CAPTURE_DIR");
        if (directory is null) return;
        Directory.CreateDirectory(directory);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(Path.Combine(directory, name));
        encoder.Save(stream);
    }
}
