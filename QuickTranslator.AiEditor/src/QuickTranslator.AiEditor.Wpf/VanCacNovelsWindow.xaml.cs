using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Publishing;

namespace QuickTranslator.AiEditor.Wpf;

public partial class VanCacNovelsWindow : Window
{
    private readonly VanCacAccountManager _account;
    private readonly ProjectSession? _project;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _chaptersCancellation;
    private bool _closed, _loading;
    internal Task ChaptersLoad { get; private set; } = Task.CompletedTask;
    internal Task NovelsLoad { get; private set; } = Task.CompletedTask;

    internal sealed record NovelRow(VanCacNovel Novel)
    {
        public string Details => $"{StatusName(Novel.PublicationStatus)} · {Novel.ChapterCount:N0} chương";
    }
    internal sealed record ChapterRow(VanCacRemoteChapter Chapter)
    {
        public string Title => $"{Chapter.Number}. {Chapter.Title}";
        public string Status => StatusName(Chapter.PublicationStatus);
    }
    internal static string StatusName(string status) => status switch
    {
        "draft" => "Nháp", "published" => "Đã xuất bản", "hidden" => "Ẩn", _ => "Chưa rõ"
    };

    internal VanCacNovelsWindow(VanCacAccountManager account, ProjectSession? project = null)
    {
        _account = account;
        _project = project;
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 1000, 650);
        RefreshAccount();
        Loaded += async (_, _) => await (NovelsLoad = LoadNovelsAsync());
    }

    private void RefreshAccount()
    {
        AccountText.Text = _account.IsSignedIn ? $"{_account.AccountName} · {_account.Preferences.BaseUrl}" : "Cần đăng nhập để xem truyện trên Sắc Viện.";
        AccountButton.Content = _account.IsSignedIn ? "Đổi tài khoản…" : "Đăng nhập…";
        ReloadButton.IsEnabled = _account.IsSignedIn && !_loading;
        UpdateLink();
    }

    private void UpdateLink()
    {
        string? linkedId = _project is null ? null : _account.Preferences.NovelByProject.GetValueOrDefault(_project.Manifest.ProjectId);
        NovelRow? linked = NovelsList.Items.OfType<NovelRow>().FirstOrDefault(n => n.Novel.Id == linkedId);
        LinkedNovelText.Text = _project is null ? "Chưa mở Project. Mở Project để liên kết truyện." :
            $"Project “{_project.Manifest.VietnameseTitle}”: " + (linkedId is null ? "chưa liên kết truyện." :
                $"đã liên kết “{linked?.Novel.Title ?? linkedId}”.");
        LinkButton.IsEnabled = _account.IsSignedIn && _project is not null && NovelsList.SelectedItem is NovelRow;
        OpenWebButton.IsEnabled = _account.IsSignedIn && NovelsList.SelectedItem is NovelRow;
    }

    internal async Task LoadNovelsAsync()
    {
        if (_loading || _closed || !_account.IsSignedIn) return;
        _loading = true;
        RefreshAccount();
        BusyProgress.Visibility = Visibility.Visible;
        NovelsList.ItemsSource = null;
        ChaptersList.ItemsSource = null;
        StatusText.Text = "Đang tải truyện…";
        try
        {
            using VanCacPublisherClient client = _account.CreateClient();
            IReadOnlyList<VanCacNovel> novels = await client.ListNovelsAsync(_lifetime.Token);
            if (_closed) return;
            NovelsList.ItemsSource = novels.Select(n => new NovelRow(n)).ToArray();
            StatusText.Text = novels.Count == 0 ? "Tài khoản chưa có truyện nào trên Sắc Viện." : $"{novels.Count:N0} truyện. Chọn truyện để xem chương.";
            string? linkedId = _project is null ? null : _account.Preferences.NovelByProject.GetValueOrDefault(_project.Manifest.ProjectId);
            NovelsList.SelectedItem = NovelsList.Items.OfType<NovelRow>().FirstOrDefault(n => n.Novel.Id == linkedId);
            UpdateLink();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is VanCacApiException or ArgumentException) { ShowError(ex); }
        finally { _loading = false; if (!_closed) { BusyProgress.Visibility = Visibility.Collapsed; RefreshAccount(); } }
    }

    private void OnNovelChanged(object sender, SelectionChangedEventArgs e)
    {
        _chaptersCancellation?.Cancel();
        ChaptersList.ItemsSource = null;
        UpdateLink();
        if (NovelsList.SelectedItem is not NovelRow row) { ChaptersStatusText.Text = "Chọn một truyện để xem chương."; return; }
        ChaptersLoad = LoadChaptersAsync(row);
    }

    private async Task LoadChaptersAsync(NovelRow row)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _chaptersCancellation = cancellation;
        ChaptersStatusText.Text = "Đang tải chương…";
        try
        {
            using VanCacPublisherClient client = _account.CreateClient();
            IReadOnlyList<VanCacRemoteChapter> chapters = await client.ListChaptersAsync(row.Novel.Id, cancellation.Token);
            if (_closed || cancellation.IsCancellationRequested || !ReferenceEquals(NovelsList.SelectedItem, row)) return;
            ChaptersList.ItemsSource = chapters.OrderBy(c => c.Number).Select(c => new ChapterRow(c)).ToArray();
            ChaptersStatusText.Text = chapters.Count == 0 ? "Truyện chưa có chương nào." : $"{chapters.Count:N0} chương.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) when (ex is VanCacApiException or ArgumentException)
        { if (!cancellation.IsCancellationRequested && ReferenceEquals(NovelsList.SelectedItem, row)) { ShowError(ex); ChaptersStatusText.Text = StatusText.Text; } }
        finally { if (ReferenceEquals(_chaptersCancellation, cancellation)) _chaptersCancellation = null; }
    }

    private void ShowError(Exception ex)
    {
        if (ex is VanCacApiException { StatusCode: 401 })
        {
            _account.ExpireSession();
            StatusText.Text = VanCacAccountManager.SessionExpiredMessage;
            NovelsList.ItemsSource = null;
            ChaptersList.ItemsSource = null;
            RefreshAccount();
        }
        else StatusText.Text = "Không tải được dữ liệu: " + ex.Message;
    }

    private async void OnReload(object sender, RoutedEventArgs e) => await LoadNovelsAsync();
    private async void OnAccount(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _chaptersCancellation?.Cancel();
        new VanCacAccountWindow(_account) { Owner = this }.ShowDialog();
        NovelsList.ItemsSource = null;
        ChaptersList.ItemsSource = null;
        RefreshAccount();
        await LoadNovelsAsync();
    }
    private void OnLink(object sender, RoutedEventArgs e)
    {
        if (_project is null || NovelsList.SelectedItem is not NovelRow row) return;
        try { _account.Preferences.NovelByProject[_project.Manifest.ProjectId] = row.Novel.Id; _account.SavePreferences(); UpdateLink(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { StatusText.Text = ex.Message; }
    }
    private void OnOpenWeb(object sender, RoutedEventArgs e)
    {
        if (NovelsList.SelectedItem is not NovelRow row) return;
        try
        {
            Uri origin = VanCacPublisherClient.NormalizeBaseUrl(_account.Preferences.BaseUrl);
            Process.Start(new ProcessStartInfo(new Uri(origin, "truyen/" + Uri.EscapeDataString(row.Novel.Slug)).AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException) { StatusText.Text = ex.Message; }
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosed(object? sender, EventArgs e) { _closed = true; _lifetime.Cancel(); }
}
