using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Publishing;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class VanCacPublishUiTests
{
    private const string Token = "vcp_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private sealed class MemoryCredentialStore : IGeminiCredentialStore
    {
        public Dictionary<string, string> Values { get; } = [];
        public string? Read(string target) => Values.GetValueOrDefault(target);
        public void Write(string target, string value) => Values[target] = value;
        public void Delete(string target) => Values.Remove(target);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];
        public int Calls => Methods.Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class PendingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            throw new InvalidOperationException("Unreachable");
        }
    }

    [Fact]
    public Task Closing_account_cancels_login_and_clears_password_while_inputs_show_progress() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        var handler = new PendingHandler();
        var store = new MemoryCredentialStore();
        var account = new VanCacAccountManager(Path.Combine(fixture.Root, "account.json"), store, handler);
        var window = new VanCacAccountWindow(account);
        try
        {
            window.Show();
            ((TextBox)window.FindName("EmailInput")).Text = "author@example.com";
            ((PasswordBox)window.FindName("PasswordInput")).Password = "private-password";
            Task login = window.SignInAsync();
            await handler.Started.Task;
            Assert.False(((FrameworkElement)window.FindName("SignInPanel")).IsEnabled);
            Assert.False(((Button)window.FindName("SignInButton")).IsEnabled);
            Assert.Equal(Visibility.Visible, ((ProgressBar)window.FindName("BusyProgress")).Visibility);
            window.Close();
            await login;
            await handler.Cancelled.Task;
            Assert.False(account.IsSignedIn);
            Assert.Null(VanCacTokenVault.Read(store));
            Assert.Equal("", ((PasswordBox)window.FindName("PasswordInput")).Password);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Closing_novel_browser_cancels_pending_requests() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        var store = new MemoryCredentialStore();
        VanCacTokenVault.Save(store, Token);
        var handler = new PendingHandler();
        var account = new VanCacAccountManager(Path.Combine(fixture.Root, "account.json"), store, handler);
        var window = new VanCacNovelsWindow(account);
        try
        {
            window.Show();
            await handler.Started.Task;
            window.Close();
            await window.NovelsLoad;
            await handler.Cancelled.Task;
            Assert.Empty(((ListBox)window.FindName("NovelsList")).Items);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Manual_code_fallback_connects_and_keeps_token_in_fake_vault() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
            ? Json("""{"user":{"displayName":"Tác giả thủ công","roles":["AUTHOR"],"authorId":"a1"}}""")
            : Json("""{"items":[],"pagination":{"page":1,"pageSize":100,"total":0,"totalPages":1}}"""));
        var store = new MemoryCredentialStore();
        string path = Path.Combine(fixture.Root, "account.json");
        var account = new VanCacAccountManager(path, store, handler);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        var window = new VanCacPublishWindow(fixture.Project, service, await service.InspectAsync(fixture.Project), true, account: account);
        try
        {
            window.Show();
            ((Expander)window.FindName("ManualCodeExpander")).IsExpanded = true;
            ((TextBox)window.FindName("BaseUrlInput")).Text = "http://localhost:4321";
            ((PasswordBox)window.FindName("TokenInput")).Password = Token;
            await window.ConnectAsync();
            Assert.Equal(Token, VanCacTokenVault.Read(store));
            Assert.Equal("http://localhost:4321", VanCacPublisherPreferences.Load(path).BaseUrl);
            Assert.Contains("Đã đăng nhập: Tác giả thủ công", ((TextBlock)window.FindName("AccountSummaryText")).Text);
            Assert.Equal("", ((PasswordBox)window.FindName("TokenInput")).Password);
            Assert.True(((FrameworkElement)window.FindName("NovelSection")).IsEnabled);
            Assert.DoesNotContain(Token, File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally { window.Close(); }
    });

    private static VanCacAccountManager Account(string path, MemoryCredentialStore store, FakeHandler handler, bool signedIn)
    {
        new VanCacPublisherPreferences
        {
            BaseUrl = "https://sacvien.example", DisplayName = "Người viết", Roles = ["AUTHOR"],
            ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(1800000000)
        }.Save(path);
        if (signedIn) VanCacTokenVault.Save(store, Token);
        return new(path, store, handler);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Account_dialog_and_status_bar_use_cached_state_without_network(bool signedIn) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        var handler = new FakeHandler(_ => throw new InvalidOperationException("Must not use HTTP on startup"));
        var account = Account(Path.Combine(fixture.Root, "account.json"), new MemoryCredentialStore(), handler, signedIn);
        var dialog = new VanCacAccountWindow(account);
        fixture.Call("InitializeVanCacAccount", account);
        MainWindow main = fixture.Window;
        try
        {
            dialog.Show();
            await Task.Yield();
            Assert.Equal(signedIn ? Visibility.Collapsed : Visibility.Visible, ((FrameworkElement)dialog.FindName("SignInPanel")).Visibility);
            Assert.Equal(signedIn ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)dialog.FindName("SignedInPanel")).Visibility);
            Assert.Equal("https://sacvien.example", ((TextBox)dialog.FindName("WebsiteInput")).Text);
            Assert.Contains("Người viết", ((TextBlock)dialog.FindName("DisplayNameText")).Text);
            Assert.Contains("AUTHOR", ((TextBlock)dialog.FindName("RolesText")).Text);
            Assert.Contains("15/01/2027", ((TextBlock)dialog.FindName("ExpiryText")).Text);
            Assert.Equal(signedIn ? "Sắc Viện: Người viết" : "Sắc Viện: chưa đăng nhập", ((TextBlock)main.FindName("VanCacAccountStatus")).Text);
            Assert.True(((Button)dialog.FindName("SignInButton")).IsDefault);
            Assert.True(((Button)dialog.FindName("CloseButton")).IsCancel);
            Assert.Equal(0, handler.Calls);
            account.ExpireSession();
            Assert.Equal("Sắc Viện: chưa đăng nhập", ((TextBlock)main.FindName("VanCacAccountStatus")).Text);
        }
        finally { dialog.Close(); }
    });

    [Fact]
    public Task Account_login_clears_password_and_persists_only_non_secret_metadata() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        string path = Path.Combine(fixture.Root, "account.json");
        var store = new MemoryCredentialStore();
        var handler = new FakeHandler(_ => Json($$$"""{"token":"{{{Token}}}","expiresAt":1800000000,"user":{"displayName":"Người viết","roles":["AUTHOR"],"authorId":"a1"}}""", HttpStatusCode.Created));
        var account = new VanCacAccountManager(path, store, handler);
        var window = new VanCacAccountWindow(account);
        try
        {
            window.Show();
            ((TextBox)window.FindName("EmailInput")).Text = "author@example.com";
            ((PasswordBox)window.FindName("PasswordInput")).Password = "private-password";
            await window.SignInAsync();
            Assert.True(account.IsSignedIn);
            Assert.Equal(Token, VanCacTokenVault.Read(store));
            Assert.Equal("", ((PasswordBox)window.FindName("PasswordInput")).Password);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("SignedInPanel")).Visibility);
            var loaded = VanCacPublisherPreferences.Load(path);
            Assert.Equal("https://sacvien.com", loaded.BaseUrl);
            Assert.Equal("Người viết", loaded.DisplayName);
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1800000000), loaded.ExpiresAt);
            Assert.Equal(Guid.Parse(loaded.DeviceId).ToString("D"), loaded.DeviceId);
            string id = loaded.DeviceId;
            Assert.Equal(id, loaded.EnsureDeviceId());
            Assert.DoesNotContain(Token, File.ReadAllText(path), StringComparison.Ordinal);
            Assert.DoesNotContain("private-password", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.DoesNotContain("author@example.com", File.ReadAllText(path), StringComparison.Ordinal);
            Assert.Single(handler.Methods);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Account_signout_deletes_local_token_even_when_revocation_fails() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        var store = new MemoryCredentialStore();
        var handler = new FakeHandler(_ => Json("""{"error":{"message":"Đã thu hồi."}}""", HttpStatusCode.Unauthorized));
        string path = Path.Combine(fixture.Root, "account.json");
        var account = Account(path, store, handler, signedIn: true);
        var window = new VanCacAccountWindow(account);
        try
        {
            window.Show();
            await window.SignOutAsync();
            Assert.False(account.IsSignedIn);
            Assert.Null(VanCacTokenVault.Read(store));
            Assert.Equal("", VanCacPublisherPreferences.Load(path).DisplayName);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("SignInPanel")).Visibility);
            Assert.Equal(HttpMethod.Delete, Assert.Single(handler.Methods));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Signed_in_publish_window_shows_account_and_offers_relogin_on_401(bool expired) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.AddChapterAsync("Một", 1);
        var handler = new FakeHandler(request => expired
            ? Json("""{"error":{"message":"Revoked."}}""", HttpStatusCode.Unauthorized)
            : request.RequestUri!.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
                ? Json("""{"user":{"displayName":"Người viết","roles":["AUTHOR"],"authorId":"a1"}}""")
                : Json("""{"items":[],"pagination":{"page":1,"pageSize":100,"total":0,"totalPages":1}}"""));
        var account = Account(Path.Combine(fixture.Root, "account.json"), new MemoryCredentialStore(), handler, signedIn: true);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        var inspection = await service.InspectAsync(fixture.Project);
        var window = new VanCacPublishWindow(fixture.Project, service, inspection, true, account: account);
        try
        {
            Assert.Equal("Đã đăng nhập: Người viết · https://sacvien.example", ((TextBlock)window.FindName("AccountSummaryText")).Text);
            Assert.Equal("Đổi tài khoản…", ((Button)window.FindName("AccountButton")).Content);
            Assert.Equal(0, handler.Calls);
            window.Show();
            await Task.Yield();
            await window.InitialConnection;
            if (expired)
            {
                Assert.Contains(VanCacAccountManager.SessionExpiredMessage, ((TextBlock)window.FindName("ConnectionText")).Text);
                Assert.Equal("Đăng nhập…", ((Button)window.FindName("AccountButton")).Content);
                Assert.False(((Button)window.FindName("StartButton")).IsEnabled);
                Assert.False(account.IsSignedIn);
                Assert.Equal(1, handler.Calls);
            }
            else Assert.True(((FrameworkElement)window.FindName("NovelSection")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Auto_link_compares_trimmed_titles_without_removing_Vietnamese_diacritics()
    {
        VanCacNovel[] novels = [new("n1", "duong", "  ĐƯỜNG VỀ  ", "a", "A", "draft", 0), new("n2", "khac", "Khác", "a", "A", "published", 2)];
        Assert.Same(novels[0], VanCacPublishWindow.SelectNovel(novels, null, " đường về "));
        Assert.Same(novels[1], VanCacPublishWindow.SelectNovel(novels, "n2", "Đường về"));
        Assert.Null(VanCacPublishWindow.SelectNovel(novels, null, "Duong ve"));
        Assert.Null(VanCacPublishWindow.SelectNovel(novels, "missing", "Đường về"));
        Assert.Null(VanCacPublishWindow.SelectNovel(novels, null, "Chưa có"));
    }

    [Fact]
    public Task Novel_browser_lists_readonly_novels_chapters_and_links_current_project() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var handler = new FakeHandler(request => request.RequestUri!.AbsolutePath.EndsWith("/chapters", StringComparison.Ordinal)
            ? Json("""{"items":[{"id":"c1","number":1,"title":"Trở về","publicationStatus":"draft"},{"id":"c2","number":2,"title":"Gặp lại","publicationStatus":"published"},{"id":"c3","number":3,"title":"Bí mật","publicationStatus":"hidden"}],"pagination":{"page":1,"pageSize":100,"total":3,"totalPages":1}}""")
            : Json("""{"items":[{"id":"n1","slug":"duong-ve","title":"Đường về","authorId":"a1","author":"A","publicationStatus":"published","chapterCount":3}],"pagination":{"page":1,"pageSize":100,"total":1,"totalPages":1}}"""));
        string path = Path.Combine(fixture.Root, "account.json");
        var account = Account(path, new MemoryCredentialStore(), handler, signedIn: true);
        var window = new VanCacNovelsWindow(account, fixture.Project);
        try
        {
            window.Show();
            await Task.Yield();
            await window.NovelsLoad;
            var novels = (ListBox)window.FindName("NovelsList");
            var row = Assert.IsType<VanCacNovelsWindow.NovelRow>(Assert.Single(novels.Items.Cast<object>()));
            Assert.Equal("Đường về", row.Novel.Title);
            Assert.Equal("Đã xuất bản · 3 chương", row.Details);
            novels.SelectedIndex = 0;
            await window.ChaptersLoad;
            var chapters = (ListBox)window.FindName("ChaptersList");
            Assert.Equal(["Nháp", "Đã xuất bản", "Ẩn"], chapters.Items.Cast<VanCacNovelsWindow.ChapterRow>().Select(c => c.Status));
            Assert.Equal("1. Trở về", ((VanCacNovelsWindow.ChapterRow)chapters.Items[0]).Title);
            ((Button)window.FindName("LinkButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal("n1", VanCacPublisherPreferences.Load(path).NovelByProject[fixture.Project.Manifest.ProjectId]);
            Assert.Contains("Đường về", ((TextBlock)window.FindName("LinkedNovelText")).Text);
            Assert.All(handler.Methods, method => Assert.Equal(HttpMethod.Get, method));
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(401)]
    [InlineData(200)]
    public Task Novel_browser_shows_expired_or_empty_state(int status) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        var handler = new FakeHandler(_ => status == 401 ? Json("""{"error":{"message":"Revoked."}}""", HttpStatusCode.Unauthorized)
            : Json("""{"items":[],"pagination":{"page":1,"pageSize":100,"total":0,"totalPages":1}}"""));
        var account = Account(Path.Combine(fixture.Root, "account.json"), new MemoryCredentialStore(), handler, signedIn: true);
        var window = new VanCacNovelsWindow(account);
        try
        {
            window.Show();
            await Task.Yield();
            await window.NovelsLoad;
            Assert.Empty(((ListBox)window.FindName("NovelsList")).Items);
            Assert.Contains(status == 401 ? VanCacAccountManager.SessionExpiredMessage : "chưa có truyện", ((TextBlock)window.FindName("StatusText")).Text);
            if (status == 401) Assert.Equal("Đăng nhập…", ((Button)window.FindName("AccountButton")).Content);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Token_vault_accepts_only_publisher_tokens_and_masks_them()
    {
        var store = new MemoryCredentialStore();
        Assert.Null(VanCacTokenVault.Read(store));
        VanCacTokenVault.Save(store, "  " + Token + "\n");
        Assert.Equal(Token, store.Values[VanCacTokenVault.Target]);
        Assert.Equal(Token, VanCacTokenVault.Read(store));
        Assert.Throws<ArgumentException>(() => VanCacTokenVault.Save(store, "AIza-not-a-publisher-token"));
        Assert.Equal("vcp_…cdef", VanCacTokenVault.Mask(Token));
        store.Values[VanCacTokenVault.Target] = "garbage";
        Assert.Null(VanCacTokenVault.Read(store));
    }

    [Fact]
    public void Preferences_round_trip_and_never_contain_the_token()
    {
        string path = Path.Combine(Path.GetTempPath(), "qt-vancac-" + Guid.NewGuid().ToString("N"), "prefs.json");
        try
        {
            Guid project = Guid.NewGuid();
            var preferences = new VanCacPublisherPreferences { BaseUrl = "https://vancac.example", PublishImmediately = true };
            preferences.NovelByProject[project] = "novel-1";
            preferences.Save(path);
            VanCacPublisherPreferences loaded = VanCacPublisherPreferences.Load(path);
            Assert.Equal("https://vancac.example", loaded.BaseUrl);
            Assert.True(loaded.PublishImmediately);
            Assert.Equal("novel-1", loaded.NovelByProject[project]);
            Assert.DoesNotContain("vcp_", File.ReadAllText(path), StringComparison.Ordinal);
            File.WriteAllText(path, "{not json");
            Assert.Equal("https://sacvien.com", VanCacPublisherPreferences.Load(path).BaseUrl);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
    }

    [Fact]
    public Task Publication_offers_van_cac_upload_for_the_vietnamese_translation_only() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.AddChapterAsync("Chương một", 1);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        var window = new ProjectPublicationWindow(fixture.Project, service);
        try
        {
            await window.LoadInspectionAsync();
            ((CheckBox)window.FindName("AllowIncompleteInput")).IsChecked = true;
            var format = (ComboBox)window.FindName("FormatInput");
            format.SelectedItem = format.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "VanCac");
            var export = (Button)window.FindName("ExportButton");
            Assert.Equal("Đăng lên Sắc Viện…", export.Content);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("FileContentOptionsPanel")).Visibility);
            Assert.Contains("Sắc Viện", ((TextBlock)window.FindName("FormatDescriptionText")).Text);
            Assert.True(export.IsEnabled);

            var language = (ComboBox)window.FindName("LanguageInput");
            language.SelectedItem = language.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "Chinese");
            await window.LoadInspectionAsync();
            Assert.False(export.IsEnabled);
            Assert.Contains("tiếng Việt", (string)export.ToolTip);

            format.SelectedItem = format.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "Txt");
            Assert.Equal("Chọn nơi lưu và xuất", export.Content);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("FileContentOptionsPanel")).Visibility);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Upload_window_opens_disconnected_without_touching_real_settings() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Project.Manifest.Description = "Giới thiệu truyện.";
        await fixture.AddChapterAsync("Chương một", 1);
        await fixture.AddChapterAsync("Chương hai", 2);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        ProjectPublicationInspection inspection = await service.InspectAsync(fixture.Project);
        string preferences = Path.Combine(fixture.Root, "vancac-prefs.json");
        int tokenReads = 0;
        var window = new VanCacPublishWindow(fixture.Project, service, inspection, allowIncomplete: true, preferences,
            () => { tokenReads++; return null; });
        try
        {
            window.Show();
            await Task.Yield();
            Assert.Contains("2 chương", ((TextBlock)window.FindName("SubtitleText")).Text);
            Assert.False(((Button)window.FindName("StartButton")).IsEnabled);
            Assert.False(((FrameworkElement)window.FindName("NovelSection")).IsEnabled);
            Assert.Contains("Cần đăng nhập", ((TextBlock)window.FindName("AccountSummaryText")).Text);
            Assert.Equal("Đăng nhập…", ((Button)window.FindName("AccountButton")).Content);
            var manual = (Expander)window.FindName("ManualCodeExpander");
            Assert.Equal("Nâng cao: dùng mã kết nối", manual.Header);
            Assert.False(manual.IsExpanded);
            Assert.Equal("Giới thiệu truyện.", ((TextBox)window.FindName("NewDescriptionInput")).Text);
            Assert.True(tokenReads > 0);
            Assert.False(File.Exists(preferences), "opening the window must not write settings");

            ((TextBox)window.FindName("BaseUrlInput")).Text = "http://vancac.example";
            ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            await Task.Yield();
            Assert.Contains("HTTPS", ((TextBlock)window.FindName("ConnectionText")).Text);
            Assert.False(File.Exists(preferences));
        }
        finally { window.Close(); }
    });
}
