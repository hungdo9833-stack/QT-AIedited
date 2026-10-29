using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Publishing;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>
/// Uploads the chapters chosen in the Publication view (range, draft confirmation) to a Sắc Viện website through
/// the Publisher API. Text comes from ProjectPublicationService.ReadChaptersAsync, the same composer Reader and
/// export use. The token is kept in Windows Credential Manager; the Project is never written.
/// </summary>
public partial class VanCacPublishWindow : Window
{
    private const int MaxLogLines = 300;
    private readonly ProjectSession _session;
    private readonly ProjectPublicationService _service;
    private readonly ProjectPublicationInspection _inspection;
    private readonly bool _allowIncomplete;
    private readonly VanCacPublisherPreferences _preferences;
    private readonly VanCacAccountManager _account;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<string> _log = [];
    private VanCacPublisherClient? _client;
    private VanCacUser? _user;
    private CancellationTokenSource? _cancellation;
    private bool _busy, _slugEdited, _settingSlug;
    internal Task InitialConnection { get; private set; } = Task.CompletedTask;

    internal VanCacPublishWindow(ProjectSession session, ProjectPublicationService service,
        ProjectPublicationInspection inspection, bool allowIncomplete, string? preferencesPath = null, Func<string?>? tokenReader = null,
        VanCacAccountManager? account = null)
    {
        // Tests pass a temporary preferences file and token reader so they never touch the user's real settings or vault.
        _account = account ?? (preferencesPath is null && tokenReader is null ? VanCacAccountManager.Default :
            new VanCacAccountManager(preferencesPath, tokenReader: tokenReader));
        _preferences = _account.Preferences;
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _inspection = inspection ?? throw new ArgumentNullException(nameof(inspection));
        _allowIncomplete = allowIncomplete;
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 760, 760);
        LogList.ItemsSource = _log;
        SubtitleText.Text = $"Gửi {inspection.TotalChapterCount:N0} chương của “{inspection.ProjectTitle}”" +
            (inspection.IsRange ? $" (chương {inspection.FromNumber}–{inspection.ToNumber})" : "") +
            " lên website, từng chương một. Gửi lại an toàn: chương giống hệt được báo “không đổi”.";
        BaseUrlInput.Text = _preferences.BaseUrl;
        PublishNowInput.IsChecked = _preferences.PublishImmediately;
        KeepDraftInput.IsChecked = !_preferences.PublishImmediately;
        NewTitleInput.Text = session.Manifest.VietnameseTitle;
        NewDescriptionInput.Text = session.Manifest.Description.Length > 5000 ? session.Manifest.Description[..5000] : session.Manifest.Description;
        ShowSavedToken();
        Loaded += async (_, _) => { if (_account.IsSignedIn) await (InitialConnection = ConnectAsync()); };
    }

    private string? SavedToken()
    {
        return _account.IsSignedIn ? _account.Token : null;
    }

    private void ShowSavedToken()
    {
        string? token = SavedToken();
        TokenStatusText.Text = token is null ? "Tạo mã ở website: Tài khoản → Kết nối QT Studio." : $"Đang dùng mã đã lưu {VanCacTokenVault.Mask(token)}. Dán mã mới để thay.";
        ForgetTokenButton.Visibility = token is null ? Visibility.Collapsed : Visibility.Visible;
        AccountSummaryText.Text = token is null ? "Cần đăng nhập Sắc Viện để chọn truyện và đăng chương." :
            $"Đã đăng nhập: {_account.AccountName} · {_preferences.BaseUrl}";
        AccountButton.Content = token is null ? "Đăng nhập…" : "Đổi tài khoản…";
    }

    private async void OnAccount(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        new VanCacAccountWindow(_account) { Owner = this }.ShowDialog();
        ResetConnection();
        BaseUrlInput.Text = _preferences.BaseUrl;
        TokenInput.Clear();
        ShowSavedToken();
        ConnectionText.Text = _account.IsSignedIn ? "" : "Cần đăng nhập để tiếp tục đăng chương.";
        if (_account.IsSignedIn) await ConnectAsync();
    }

    private void ResetConnection()
    {
        VanCacPublisherClient? previous = _client;
        _client = null; _user = null;
        previous?.Dispose();
        NovelSection.IsEnabled = false;
        NovelInput.ItemsSource = null;
        UpdateStartEnabled();
    }

    private string ApiError(VanCacApiException error)
    {
        if (error.StatusCode != 401) return error.Message;
        _account.ExpireSession();
        ResetConnection();
        ShowSavedToken();
        ConnectionText.Text = VanCacAccountManager.SessionExpiredMessage;
        return VanCacAccountManager.SessionExpiredMessage;
    }

    private async void OnConnect(object sender, RoutedEventArgs e) => await ConnectAsync();

    internal async Task ConnectAsync()
    {
        if (_busy) return;
        string typed = TokenInput.Password.Trim();
        string? token = typed.Length > 0 ? typed : SavedToken();
        Uri baseUrl;
        try { baseUrl = VanCacPublisherClient.NormalizeBaseUrl(BaseUrlInput.Text); }
        catch (ArgumentException ex) { ConnectionText.Text = ex.Message; return; }
        if (!VanCacPublisherClient.IsValidToken(token)) { ConnectionText.Text = "Dán mã kết nối (vcp_…) tạo ở website: Tài khoản → Kết nối QT Studio."; return; }
        SetBusy(true, null);
        ConnectionText.Text = "Đang kiểm tra kết nối…";
        var client = _account.CreateClient(baseUrl.ToString(), token!);
        try
        {
            VanCacUser user = await client.GetMeAsync(_lifetime.Token);
            // Only persist what was proven to work.
            TokenInput.Clear();
            _account.SaveManualConnection(baseUrl.ToString().TrimEnd('/'), typed.Length > 0 ? typed : null, user);
            BaseUrlInput.Text = _preferences.BaseUrl;
            _client?.Dispose();
            _client = client; _user = user;
            ConnectionText.Text = $"Đã kết nối: {user.DisplayName} · {string.Join(", ", user.Roles)}.";
            ShowSavedToken();
            bool admin = user.Roles.Contains("ADMIN");
            AuthorPanel.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
            if (admin) AuthorInput.ItemsSource = await client.ListAuthorsAsync(_lifetime.Token);
            NovelSection.IsEnabled = true;
            await LoadNovelsAsync();
        }
        catch (Exception ex) when (ex is VanCacApiException or Win32Exception or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
        {
            client.Dispose();
            ResetConnection();
            ConnectionText.Text = "Không kết nối được: " + (ex is VanCacApiException api ? ApiError(api) : ex.Message);
        }
        finally { SetBusy(false, null); }
    }

    private async void OnReloadNovels(object sender, RoutedEventArgs e) => await LoadNovelsAsync();

    private async Task LoadNovelsAsync(string? selectId = null)
    {
        VanCacPublisherClient? client = _client;
        if (client is null) return;
        try
        {
            IReadOnlyList<VanCacNovel> novels = await client.ListNovelsAsync(_lifetime.Token);
            if (!ReferenceEquals(_client, client)) return;
            NovelInput.ItemsSource = novels;
            string? wanted = selectId ?? (_preferences.NovelByProject.TryGetValue(_session.Manifest.ProjectId, out string? saved) ? saved : null);
            NovelInput.SelectedItem = SelectNovel(novels, wanted, _session.Manifest.VietnameseTitle);
            if (novels.Count == 0) PlanText.Text = "Tài khoản chưa có truyện nào trên website. Tạo truyện mới bên dưới.";
            else if (NovelInput.SelectedItem is null) PlanText.Text = "Chọn truyện sẽ nhận các chương này.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || !ReferenceEquals(_client, client)) { }
        catch (VanCacApiException ex) { if (ReferenceEquals(_client, client)) PlanText.Text = "Không tải được danh sách truyện: " + ApiError(ex); }
    }

    internal static VanCacNovel? SelectNovel(IReadOnlyList<VanCacNovel> novels, string? savedId, string projectTitle) =>
        savedId is not null ? novels.FirstOrDefault(n => n.Id == savedId) :
        novels.FirstOrDefault(n => string.Equals(n.Title.Trim(), projectTitle.Trim(), StringComparison.OrdinalIgnoreCase));

    private async void OnNovelChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateStartEnabled();
        VanCacPublisherClient? client = _client;
        if (client is null || NovelInput.SelectedItem is not VanCacNovel novel) return;
        PlanText.Text = "Đang đối chiếu chương trên website…";
        try
        {
            _preferences.NovelByProject[_session.Manifest.ProjectId] = novel.Id;
            _account.SavePreferences();
            IReadOnlyList<VanCacRemoteChapter> remote = await client.ListChaptersAsync(novel.Id, _lifetime.Token);
            if (!ReferenceEquals(NovelInput.SelectedItem, novel) || !ReferenceEquals(_client, client)) return;
            var onWeb = remote.Select(c => c.Number).ToHashSet();
            int[] local = (_inspection.ChapterNumbers ?? [])
                .Where(n => n >= (_inspection.FromNumber ?? int.MinValue) && n <= (_inspection.ToNumber ?? int.MaxValue)).ToArray();
            int existing = local.Count(onWeb.Contains);
            PlanText.Text = $"Website có {remote.Count:N0} chương. Trong {local.Length:N0} chương sẽ gửi: {local.Length - existing:N0} chương mới, " +
                $"{existing:N0} chương đã có (chỉ cập nhật khi nội dung khác)." +
                (novel.PublicationStatus != "published" ? " Truyện đang là bản nháp trên website: người đọc chưa thấy truyện cho đến khi bạn xuất bản truyện." : "");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || !ReferenceEquals(_client, client)) { }
        catch (VanCacApiException ex)
        { if (ReferenceEquals(_client, client) && ReferenceEquals(NovelInput.SelectedItem, novel)) PlanText.Text = "Không đối chiếu được chương: " + ApiError(ex); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { PlanText.Text = ex.Message; }
    }

    private void OnToggleCreate(object sender, RoutedEventArgs e)
    {
        bool show = CreatePanel.Visibility != Visibility.Visible;
        CreatePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && NewSlugInput.Text.Length == 0) SetSlug(VanCacSlug.FromTitle(NewTitleInput.Text));
    }

    private void OnNewTitleChanged(object sender, TextChangedEventArgs e) { if (!_slugEdited && NewSlugInput is not null) SetSlug(VanCacSlug.FromTitle(NewTitleInput.Text)); }
    private void OnNewSlugChanged(object sender, TextChangedEventArgs e) { if (!_settingSlug) _slugEdited = true; }
    private void SetSlug(string slug) { _settingSlug = true; NewSlugInput.Text = slug; _settingSlug = false; }

    private async void OnCreateNovel(object sender, RoutedEventArgs e)
    {
        if (_client is null || _user is null || _busy) return;
        string title = NewTitleInput.Text.Trim(), slug = NewSlugInput.Text.Trim();
        string? authorId = _user.Roles.Contains("ADMIN") ? (AuthorInput.SelectedItem as VanCacAuthor)?.Id : _user.AuthorId;
        if (title.Length is 0 or > 200) { PlanText.Text = "Nhập tên truyện 1–200 ký tự."; return; }
        if (!VanCacSlug.IsValid(slug)) { PlanText.Text = "Đường dẫn chỉ gồm chữ thường a–z, số và dấu gạch nối (tối đa 120 ký tự)."; return; }
        if (authorId is null) { PlanText.Text = _user.Roles.Contains("ADMIN") ? "Chọn tác giả cho truyện mới." : "Tài khoản chưa có hồ sơ tác giả đang hoạt động trên website."; return; }
        string[] genres = _session.Manifest.Genres.Select(g => g.Trim()).Where(g => g.Length is > 0 and <= 50).Distinct().Take(8).ToArray();
        SetBusy(true, "Đang tạo truyện trên website…");
        try
        {
            VanCacNovel novel = await _client.CreateNovelAsync(new(title, slug, authorId, NewDescriptionInput.Text, genres, NewCompletedInput.IsChecked == true), _lifetime.Token);
            CreatePanel.Visibility = Visibility.Collapsed;
            await LoadNovelsAsync(novel.Id);
            ProgressText.Text = $"Đã tạo truyện “{novel.Title}” (bản nháp).";
        }
        catch (VanCacApiException ex)
        {
            PlanText.Text = "Không tạo được truyện: " + ApiError(ex);
        }
        finally { SetBusy(false, null); }
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_client is null || _busy || NovelInput.SelectedItem is not VanCacNovel novel) return;
        bool publish = PublishNowInput.IsChecked == true;
        _preferences.PublishImmediately = publish;
        _account.SavePreferences();
        string question = $"Đăng {_inspection.TotalChapterCount:N0} chương lên “{novel.Title}”?\n\n" +
            (publish ? "Các chương sẽ được XUẤT BẢN NGAY và người đọc thấy được (nếu truyện đã xuất bản)." : "Chương mới là bản nháp; chương đã có giữ nguyên trạng thái.");
        if (ThemedDialog.Show(this, question, "Đăng lên Sắc Viện", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        _log.Clear();
        ReportText.Visibility = Visibility.Collapsed;
        PublishProgress.Maximum = Math.Max(1, _inspection.TotalChapterCount);
        PublishProgress.Value = 0;
        SetBusy(true, "Đang đăng…");
        _cancellation = new();
        try
        {
            var progress = new Progress<VanCacPublishProgress>(p =>
            {
                PublishProgress.Value = p.Done;
                ProgressText.Text = $"{p.Done:N0}/{p.Total:N0} · Chương {p.Number}: {p.Title}";
                _log.Add($"Chương {p.Number}: {p.Outcome}");
                while (_log.Count > MaxLogLines) _log.RemoveAt(0);
                LogList.ScrollIntoView(_log[^1]);
            });
            IAsyncEnumerable<ProjectPublicationChapter> chapters = _service.ReadChaptersAsync(_session, _inspection.ProjectRevision,
                cancellationToken: _cancellation.Token, fromNumber: _inspection.FromNumber, toNumber: _inspection.ToNumber,
                language: PublicationLanguage.Vietnamese);
            var sink = new SessionTrackingSink(_client);
            VanCacPublishReport report = await VanCacPublishRunner.RunAsync(chapters, _inspection.TotalChapterCount, sink,
                new(novel.Id, publish ? VanCacChapterStatus.Published : null, _allowIncomplete), progress, _cancellation.Token);
            ShowReport(report, novel, publish);
            if (sink.Unauthorized)
            {
                ApiError(new VanCacApiException(401, ""));
                ReportText.Text += "\n" + VanCacAccountManager.SessionExpiredMessage;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ProgressText.Text = "Lỗi: " + ex.Message;
            ThemedDialog.Show(this, ex.Message, "Đăng lên Sắc Viện", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false, null);
        }
    }

    private void ShowReport(VanCacPublishReport report, VanCacNovel novel, bool published)
    {
        var lines = new List<string>
        {
            $"Đã gửi {report.Sent:N0} chương: {report.Created:N0} tạo mới · {report.Updated:N0} cập nhật · {report.Unchanged:N0} không đổi."
        };
        if (report.SkippedEmpty > 0) lines.Add($"Bỏ qua {report.SkippedEmpty:N0} chương chưa có bản dịch.");
        if (report.SkippedIncomplete > 0) lines.Add($"Bỏ qua {report.SkippedIncomplete:N0} chương chưa hoàn tất/cần kiểm tra (chưa xác nhận đăng bản nháp).");
        if (report.Failures.Count > 0) lines.Add($"{report.Failures.Count:N0} chương lỗi: " + string.Join("; ", report.Failures.Take(5).Select(f => $"chương {f.Number}: {f.Message}")) + (report.Failures.Count > 5 ? "…" : ""));
        if (report.Cancelled) lines.Add("Đã dừng theo yêu cầu. Chạy lại để gửi tiếp; chương đã gửi sẽ báo “không đổi”.");
        if (report.AbortReason is not null) lines.Add("Đã dừng: " + report.AbortReason);
        if (!published && report.Created > 0) lines.Add("Chương mới đang là bản nháp: vào trang quản lý trên website để xuất bản (có nút xuất bản hàng loạt).");
        if (novel.PublicationStatus != "published") lines.Add("Truyện đang là bản nháp trên website: xuất bản truyện để người đọc thấy.");
        ReportText.Text = string.Join("\n", lines);
        ReportText.Visibility = Visibility.Visible;
        ReportText.SetResourceReference(TextBlock.ForegroundProperty, report.Completed && report.Failures.Count == 0 ? "SuccessBrush" : "WarningBrush");
        ProgressText.Text = report.Completed ? "Hoàn tất." : "Chưa hoàn tất.";
        OpenSiteButton.Visibility = Visibility.Visible;
    }

    private void OnOpenSite(object sender, RoutedEventArgs e)
    {
        if (_preferences.BaseUrl.Length == 0) return;
        string path = _user?.Roles.Contains("ADMIN") == true ? "/admin" : "/tac-gia";
        try { Process.Start(new ProcessStartInfo(_preferences.BaseUrl + path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { ProgressText.Text = ex.Message; }
    }

    private async void OnForgetToken(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        try { await _account.LogoutAsync(_lifetime.Token); }
        catch (Win32Exception ex) { ConnectionText.Text = ex.Message; return; }
        _client?.Dispose(); _client = null; _user = null;
        NovelSection.IsEnabled = false;
        NovelInput.ItemsSource = null;
        ConnectionText.Text = "Đã đăng xuất và xóa mã khỏi máy này.";
        ShowSavedToken();
        UpdateStartEnabled();
    }

    private void SetBusy(bool busy, string? message)
    {
        _busy = busy;
        AccountButton.IsEnabled = !busy;
        ConnectButton.IsEnabled = ForgetTokenButton.IsEnabled = BaseUrlInput.IsEnabled = TokenInput.IsEnabled = !busy;
        ReloadNovelsButton.IsEnabled = NovelInput.IsEnabled = ToggleCreateButton.IsEnabled = CreateNovelButton.IsEnabled = !busy;
        KeepDraftInput.IsEnabled = PublishNowInput.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        CancelButton.Visibility = busy && _cancellation is not null ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null) ProgressText.Text = message;
        UpdateStartEnabled();
    }

    private void UpdateStartEnabled()
    {
        bool ready = _client is not null && NovelInput.SelectedItem is VanCacNovel && _inspection.TotalChapterCount > 0;
        PublishSection.IsEnabled = _client is not null;
        StartButton.IsEnabled = !_busy && ready;
        StartButton.ToolTip = ready ? "Gửi các chương đã chọn lên website." : "Kết nối và chọn truyện đích trước.";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        ProgressText.Text = "Sẽ dừng sau chương đang gửi…";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) { _lifetime.Cancel(); _client?.Dispose(); return; }
        e.Cancel = true;
        _cancellation?.Cancel();
        ProgressText.Text = "Đang dừng an toàn, đợi chương hiện tại gửi xong…";
    }

    private sealed class SessionTrackingSink(IVanCacChapterSink inner) : IVanCacChapterSink
    {
        public bool Unauthorized { get; private set; }
        public async Task<VanCacPutResult> PutChapterAsync(string novelId, int number, string title, string content,
            VanCacChapterStatus? status, CancellationToken cancellationToken)
        {
            try { return await inner.PutChapterAsync(novelId, number, title, content, status, cancellationToken); }
            catch (VanCacApiException ex) when (ex.StatusCode == 401) { Unauthorized = true; throw; }
        }
    }
}
