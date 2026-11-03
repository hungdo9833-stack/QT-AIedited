using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Publishing;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class LibraryWebStatusTests
{
    private static readonly DateTimeOffset Checked = new(2026, 11, 3, 10, 44, 0, TimeSpan.FromHours(7));

    private static ProjectLibraryCard Card(bool linked = false, VanCacWebProgress? web = null, bool missing = false) =>
        new(Guid.NewGuid(), "Tiểu Mạn", "", ProjectStatus.Translating, @"C:\x\a.qtproj", null, 64, 0, 1, "Chương 64",
            Checked, missing, null, linked, web);

    [Fact]
    public void Web_progress_counts_only_public_chapters_toward_the_highest_number()
    {
        VanCacWebProgress web = VanCacWebProgress.From(
        [
            new("a", 1, "Một", "published"), new("b", 2, "Hai", "published"),
            new("c", 3, "Ba", "draft"), new("d", 40, "Bốn mươi", "hidden"),
        ], Checked);
        Assert.Equal(new VanCacWebProgress(2, 2, 1, Checked), web);
        Assert.Equal(new VanCacWebProgress(0, 0, 0, Checked), VanCacWebProgress.From([], Checked));
    }

    [Fact]
    public void Recording_progress_follows_the_novel_link_and_survives_a_reload()
    {
        string path = Path.Combine(Path.GetTempPath(), "qt-web-progress-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            Guid project = Guid.NewGuid();
            var preferences = new VanCacPublisherPreferences();
            Assert.False(preferences.RecordWebProgress("n1", [new("a", 1, "Một", "published")]));
            preferences.NovelByProject[project] = "n1";
            Assert.True(preferences.RecordWebProgress("n1", [new("a", 1, "Một", "published"), new("b", 7, "Bảy", "published")]));
            preferences.Save(path);
            VanCacWebProgress reloaded = VanCacPublisherPreferences.Load(path).WebProgressByProject[project];
            Assert.Equal(7, reloaded.PublishedThrough);
            Assert.Equal(2, reloaded.PublishedCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Card_line_states_how_far_the_novel_is_on_the_web()
    {
        Assert.False(Card().HasWebStatus);
        Assert.False(Card(linked: true, missing: true).HasWebStatus);

        ProjectLibraryCard unchecked_ = Card(linked: true);
        Assert.True(unchecked_.HasWebStatus);
        Assert.False(unchecked_.IsOnWeb);
        Assert.Equal("Đã liên kết Sắc Viện · chưa kiểm tra", unchecked_.WebLabel);

        Assert.Equal("Trên web: 3 chương nháp", Card(true, new(0, 0, 3, Checked)).WebLabel);
        Assert.Equal("Trên web: chưa có chương", Card(true, new(0, 0, 0, Checked)).WebLabel);

        ProjectLibraryCard published = Card(true, new(64, 64, 2, Checked));
        Assert.True(published.IsOnWeb);
        Assert.Equal("Đã có trên web đến chương 64", published.WebLabel);
        Assert.Contains("64 chương công khai, 2 chương nháp", published.WebToolTip);
    }

    [Fact]
    public Task Library_card_shows_a_green_check_only_for_novels_public_on_the_web() => StaHost.Run(async () =>
    {
        var view = new ProjectLibraryView([Card(true, new(64, 64, 0, Checked)), Card(true), Card()], null);
        var window = new Window { Content = view, Width = 1200, Height = 800, Left = -4000, ShowActivated = false };
        try
        {
            window.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            TextBlock[] labels = Descendants<TextBlock>(view).Where(t => t.IsVisible && t.Text.Contains("web", StringComparison.Ordinal)
                || t.IsVisible && t.Text.Contains("Sắc Viện", StringComparison.Ordinal)).ToArray();
            TextBlock onWeb = Assert.Single(labels, t => t.Text == "Đã có trên web đến chương 64");
            Assert.Equal(((SolidColorBrush)Application.Current.Resources["SuccessBrush"]).Color, ((SolidColorBrush)onWeb.Foreground).Color);
            Assert.Single(labels, t => t.Text == "Đã liên kết Sắc Viện · chưa kiểm tra");
            Assert.Equal(1, Descendants<TextBlock>(view).Count(t => t.IsVisible && t.Text == "\uE73E"));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Opening_the_library_signed_in_asks_the_website_once_and_redraws_linked_cards() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        Guid projectId = fixture.Project.Manifest.ProjectId;
        var handler = new CountingHandler("""{"items":[{"id":"c1","number":1,"title":"Một","publicationStatus":"published"},{"id":"c2","number":2,"title":"Hai","publicationStatus":"published"},{"id":"c3","number":3,"title":"Ba","publicationStatus":"draft"}],"pagination":{"page":1,"pageSize":100,"total":3,"totalPages":1}}""");
        string path = Path.Combine(fixture.Root, "account.json");
        var preferences = new VanCacPublisherPreferences { BaseUrl = "https://sacvien.example" };
        preferences.NovelByProject[projectId] = "n1";
        preferences.Save(path);
        var store = new MemoryCredentialStore();
        VanCacTokenVault.Save(store, "vcp_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
        fixture.Set("_vanCacAccount", new VanCacAccountManager(path, store, handler));

        ProjectLibraryCard[] cards = [Card(linked: true) with { ProjectId = projectId }];
        var view = new ProjectLibraryView(cards, null);
        await fixture.TaskCall("RefreshLibraryWebStatusAsync", view, cards, null, CancellationToken.None);

        ProjectLibraryCard shown = Assert.Single(view.ProjectList.Items.Cast<ProjectLibraryCard>());
        Assert.Equal("Đã có trên web đến chương 2", shown.WebLabel);
        Assert.Equal(2, VanCacPublisherPreferences.Load(path).WebProgressByProject[projectId].PublishedThrough);
        Assert.Equal(1, handler.Calls);

        // Reopening right away reuses the fresh result instead of asking the website again.
        await fixture.TaskCall("RefreshLibraryWebStatusAsync", view, cards, null, CancellationToken.None);
        Assert.Equal(1, handler.Calls);
    });

    private sealed class MemoryCredentialStore : IGeminiCredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public string? Read(string target) => _values.GetValueOrDefault(target);
        public void Write(string target, string value) => _values[target] = value;
        public void Delete(string target) => _values.Remove(target);
    }

    private sealed class CountingHandler(string body) : System.Net.Http.HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            { Content = new System.Net.Http.StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (T nested in Descendants<T>(child)) yield return nested;
        }
    }
}
