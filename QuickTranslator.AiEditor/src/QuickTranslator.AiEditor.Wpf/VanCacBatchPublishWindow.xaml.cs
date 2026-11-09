using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Publishing;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>A library Project offered for batch upload.</summary>
internal sealed record VanCacBatchBook(Guid ProjectId, string Title, string ManifestPath, int ChapterCount,
    int CompletedChapterCount, string WebLabel, bool WebLinked);

/// <summary>How the batch window reaches Projects: the main window decides which are busy and which session to read.</summary>
internal sealed record VanCacBatchSource(
    ProjectPublicationService Service,
    Func<VanCacBatchBook, string?> BusyReason,
    Func<VanCacBatchBook, CancellationToken, Task<ProjectSession>> OpenSession);

internal sealed class VanCacBatchRow(VanCacBatchBook book) : INotifyPropertyChanged
{
    private bool _selected;
    private string _target = "", _stage = "", _webText = book.WebLinked ? book.WebLabel : "Chưa liên kết";

    public VanCacBatchBook Book { get; } = book;
    public string Title => Book.Title;
    public string ChaptersText => $"{Book.CompletedChapterCount:N0}/{Book.ChapterCount:N0} chương";
    public string WebText { get => _webText; set => Set(ref _webText, value); }
    public bool CanSelect { get; } = book.ChapterCount > 0;
    public bool Selected { get => _selected; set => Set(ref _selected, value && CanSelect); }
    public string Target { get => _target; set => Set(ref _target, value); }
    public string Stage { get => _stage; set => Set(ref _stage, value); }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new(name));
    }
}

internal static class VanCacBatchPlanner
{
    /// <summary>The website novel a Project goes to: its saved link, else a novel with the same title, else none (create one).</summary>
    public static VanCacNovel? FindTarget(IReadOnlyList<VanCacNovel> novels, string? linkedId, string title) =>
        (linkedId is null ? null : novels.FirstOrDefault(novel => novel.Id == linkedId))
        ?? VanCacPublishWindow.SelectNovel(novels, null, title);

    /// <summary>A slug for a new novel; titles without Latin letters fall back to one built from the Project id.</summary>
    public static string NewSlug(string title, Guid projectId, int attempt)
    {
        string slug = VanCacSlug.FromTitle(title);
        if (!VanCacSlug.IsValid(slug)) slug = "truyen";
        string suffix = attempt == 0 ? "" : "-" + projectId.ToString("N")[..(4 + 2 * attempt)];
        if (slug.Length + suffix.Length > 120) slug = slug[..(120 - suffix.Length)].TrimEnd('-');
        return slug + suffix;
    }

    /// <summary>The chapters still to send: every chapter, or only those whose number the website lacks.</summary>
    public static async IAsyncEnumerable<ProjectPublicationChapter> OnlyMissing(IAsyncEnumerable<ProjectPublicationChapter> chapters,
        IReadOnlySet<int> onWeb, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (ProjectPublicationChapter chapter in chapters.WithCancellation(cancellationToken).ConfigureAwait(false))
            if (!onWeb.Contains(chapter.Number)) yield return chapter;
    }

    public static string Summary(VanCacPublishReport report)
    {
        var parts = new List<string> { $"Đã gửi {report.Sent:N0}: {report.Created:N0} mới · {report.Updated:N0} cập nhật · {report.Unchanged:N0} không đổi" };
        if (report.SkippedIncomplete > 0) parts.Add($"bỏ qua {report.SkippedIncomplete:N0} chưa hoàn tất");
        if (report.SkippedEmpty > 0) parts.Add($"bỏ qua {report.SkippedEmpty:N0} chưa dịch");
        if (report.Failures.Count > 0) parts.Add($"{report.Failures.Count:N0} lỗi (chương {string.Join(", ", report.Failures.Take(3).Select(f => f.Number))}" +
            (report.Failures.Count > 3 ? "…" : "") + "): " + report.Failures[0].Message);
        if (report.Cancelled) parts.Add("đã dừng");
        if (report.AbortReason is not null) parts.Add("dừng: " + report.AbortReason);
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// "Xuất bản → Đăng hàng loạt lên Sắc Viện…": ticks several library Projects and uploads them one after another with
/// the same runner as the single-book window. Each Project goes to its linked novel, else a same-title novel, else a
/// new draft novel; the link is saved so the library shows its web progress.
/// </summary>
public partial class VanCacBatchPublishWindow : Window
{
    private readonly VanCacAccountManager _account;
    private readonly VanCacBatchSource _source;
    private readonly ObservableCollection<VanCacBatchRow> _rows = [];
    private readonly CancellationTokenSource _lifetime = new();
    private VanCacPublisherClient? _client;
    private VanCacUser? _user;
    private List<VanCacNovel> _novels = [];
    private CancellationTokenSource? _cancellation;
    private bool _busy;
    internal Task InitialConnection { get; private set; } = Task.CompletedTask;
    internal IReadOnlyList<VanCacBatchRow> Rows => _rows;

    internal VanCacBatchPublishWindow(VanCacAccountManager account, IReadOnlyList<VanCacBatchBook> books, VanCacBatchSource source)
    {
        _account = account ?? throw new ArgumentNullException(nameof(account));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 1100, 720);
        foreach (VanCacBatchBook book in books)
        {
            var row = new VanCacBatchRow(book) { Stage = book.ChapterCount == 0 ? "Chưa có chương" : "" };
            row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(VanCacBatchRow.Selected)) UpdateStart(); };
            _rows.Add(row);
        }
        BookGrid.ItemsSource = _rows;
        PublishNowInput.IsChecked = _account.Preferences.PublishImmediately;
        KeepDraftInput.IsChecked = !_account.Preferences.PublishImmediately;
        RefreshAccount();
        UpdateStart();
        Loaded += async (_, _) => { if (_account.IsSignedIn) await (InitialConnection = ConnectAsync()); };
    }

    private void RefreshAccount()
    {
        AccountText.Text = !_account.IsSignedIn ? "Cần đăng nhập Sắc Viện để đăng chương." :
            _user is null ? $"Tài khoản: {_account.AccountName} · {_account.Preferences.BaseUrl}" :
            $"Đã kết nối: {_user.DisplayName} · {_account.Preferences.BaseUrl} · {_novels.Count:N0} truyện trên web";
        AccountButton.Content = _account.IsSignedIn ? "Đổi tài khoản…" : "Đăng nhập…";
    }

    private async void OnAccount(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        new VanCacAccountWindow(_account) { Owner = this }.ShowDialog();
        DropConnection();
        RefreshAccount();
        if (_account.IsSignedIn) await ConnectAsync();
    }

    private void DropConnection()
    {
        _client?.Dispose();
        _client = null; _user = null; _novels = [];
        foreach (VanCacBatchRow row in _rows) row.Target = "";
        UpdateStart();
    }

    private async Task ConnectAsync()
    {
        if (_busy || !_account.IsSignedIn) return;
        SetBusy(true);
        StatusText.Text = "Đang kết nối Sắc Viện…";
        VanCacPublisherClient? client = null;
        try
        {
            client = _account.CreateClient();
            VanCacUser user = await client.GetMeAsync(_lifetime.Token);
            _account.CacheUser(user);
            bool admin = user.Roles.Contains("ADMIN");
            AuthorPanel.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
            if (admin) AuthorInput.ItemsSource = await client.ListAuthorsAsync(_lifetime.Token);
            _novels = (await client.ListNovelsAsync(_lifetime.Token)).ToList();
            _client?.Dispose();
            _client = client; _user = user;
            ShowTargets();
            StatusText.Text = "Tích truyện cần đăng rồi bấm Đăng.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { client?.Dispose(); }
        catch (Exception ex) when (ex is VanCacApiException or Win32Exception or ArgumentException or System.IO.IOException
            or UnauthorizedAccessException or System.Net.Http.HttpRequestException)
        {
            client?.Dispose();
            if (ex is VanCacApiException { IsAuthFailure: true }) _account.ExpireSession();
            DropConnection();
            StatusText.Text = "Không kết nối được: " + (ex is VanCacApiException { StatusCode: 401 } ? VanCacAccountManager.SessionExpiredMessage : ex.Message);
        }
        finally { SetBusy(false); RefreshAccount(); }
    }

    private void ShowTargets()
    {
        foreach (VanCacBatchRow row in _rows)
        {
            VanCacNovel? novel = VanCacBatchPlanner.FindTarget(_novels, LinkedId(row.Book), row.Book.Title);
            row.Target = novel is null ? "Tạo truyện mới (bản nháp)" : novel.Title;
        }
    }

    private string? LinkedId(VanCacBatchBook book) =>
        _account.Preferences.NovelByProject.TryGetValue(book.ProjectId, out string? id) ? id : null;

    private void OnSelectAll(object sender, RoutedEventArgs e) { foreach (VanCacBatchRow row in _rows) row.Selected = true; }
    private void OnSelectNone(object sender, RoutedEventArgs e) { foreach (VanCacBatchRow row in _rows) row.Selected = false; }
    private void OnSelectLinked(object sender, RoutedEventArgs e)
    {
        foreach (VanCacBatchRow row in _rows) row.Selected = _account.Preferences.NovelByProject.ContainsKey(row.Book.ProjectId);
    }

    private void UpdateStart()
    {
        int count = _rows.Count(row => row.Selected);
        StartButton.Content = $"Đăng {count:N0} truyện";
        StartButton.IsEnabled = !_busy && _client is not null && count > 0;
    }

    private async void OnStart(object sender, RoutedEventArgs e) => await StartAsync();

    internal async Task StartAsync()
    {
        if (_busy || _client is null || _user is null) return;
        VanCacBatchRow[] chosen = _rows.Where(row => row.Selected).ToArray();
        if (chosen.Length == 0) return;
        bool publish = PublishNowInput.IsChecked == true, onlyNew = OnlyNewInput.IsChecked == true,
            allowIncomplete = AllowIncompleteInput.IsChecked == true;
        string question = $"Đăng {chosen.Length:N0} truyện lên Sắc Viện?\n\n" +
            (onlyNew ? "Chỉ gửi chương chưa có trên website. " : "Gửi mọi chương; chương giống hệt báo “không đổi”. ") +
            (publish ? "Chương được XUẤT BẢN NGAY." : "Chương mới là bản nháp.");
        if (ThemedDialog.Show(this, question, "Đăng hàng loạt", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _account.Preferences.PublishImmediately = publish;
        TrySavePreferences();

        foreach (VanCacBatchRow row in chosen) row.Stage = "Chờ";
        BatchProgress.Maximum = chosen.Length;
        BatchProgress.Value = 0;
        _cancellation = new();
        SetBusy(true);
        int done = 0, failed = 0;
        try
        {
            foreach (VanCacBatchRow row in chosen)
            {
                if (_cancellation.IsCancellationRequested) { row.Stage = "Chưa đăng (đã dừng)"; continue; }
                StatusText.Text = $"Truyện {done + 1:N0}/{chosen.Length:N0}: {row.Title}";
                BookOutcome outcome = await PublishBookAsync(row, publish, onlyNew, allowIncomplete, _cancellation.Token);
                if (outcome != BookOutcome.Done) failed++;
                BatchProgress.Value = ++done;
                if (outcome == BookOutcome.SessionLost)
                {
                    foreach (VanCacBatchRow rest in chosen.Skip(done)) rest.Stage = "Chưa đăng (cần đăng nhập lại)";
                    break;
                }
            }
            StatusText.Text = _cancellation.IsCancellationRequested ? $"Đã dừng sau {done:N0} truyện." :
                failed == 0 ? $"Hoàn tất {chosen.Length:N0} truyện." : $"Xong: {chosen.Length - failed:N0} truyện ổn, {failed:N0} truyện có lỗi hoặc bị bỏ qua.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
            ShowTargets();
        }
    }

    private enum BookOutcome { Done, Problem, SessionLost }

    private async Task<BookOutcome> PublishBookAsync(VanCacBatchRow row, bool publish, bool onlyNew, bool allowIncomplete,
        CancellationToken cancellationToken)
    {
        VanCacPublisherClient client = _client!;
        try
        {
            if (_source.BusyReason(row.Book) is { } busy) { row.Stage = "Bỏ qua: " + busy; return BookOutcome.Problem; }
            row.Stage = "Đang đọc dự án…";
            ProjectSession session = await _source.OpenSession(row.Book, cancellationToken);
            ProjectPublicationInspection inspection = await _source.Service.InspectAsync(session, cancellationToken: cancellationToken);
            if (inspection.TotalChapterCount == 0) { row.Stage = "Bỏ qua: chưa có chương"; return BookOutcome.Problem; }

            string title = string.IsNullOrWhiteSpace(session.Manifest.VietnameseTitle) ? row.Title : session.Manifest.VietnameseTitle.Trim();
            VanCacNovel? novel = VanCacBatchPlanner.FindTarget(_novels, LinkedId(row.Book), title);
            if (novel is null)
            {
                row.Stage = "Đang tạo truyện trên website…";
                novel = await CreateNovelAsync(client, session, title, cancellationToken);
                if (novel is null) { row.Stage = "Lỗi: chọn tác giả cho truyện mới (tài khoản quản trị)."; return BookOutcome.Problem; }
                _novels.Add(novel);
            }
            row.Target = novel.Title;
            _account.Preferences.NovelByProject[row.Book.ProjectId] = novel.Id;
            TrySavePreferences();

            row.Stage = "Đang đối chiếu chương trên website…";
            IReadOnlyList<VanCacRemoteChapter> remote = await client.ListChaptersAsync(novel.Id, cancellationToken);
            HashSet<int> onWeb = onlyNew ? remote.Select(chapter => chapter.Number).ToHashSet() : [];
            int total = (inspection.ChapterNumbers ?? []).Count(number => !onWeb.Contains(number));
            if (total == 0)
            {
                row.Stage = "Không có chương mới để gửi";
                RecordWebProgress(row, novel, remote);
                return BookOutcome.Done;
            }

            IAsyncEnumerable<ProjectPublicationChapter> chapters = _source.Service.ReadChaptersAsync(session, inspection.ProjectRevision,
                cancellationToken: cancellationToken, language: PublicationLanguage.Vietnamese);
            if (onlyNew) chapters = VanCacBatchPlanner.OnlyMissing(chapters, onWeb, cancellationToken);
            var progress = new Progress<VanCacPublishProgress>(p => row.Stage = $"{p.Done:N0}/{p.Total:N0} · Chương {p.Number}: {p.Outcome}");
            var sink = new AuthTrackingSink(client);
            VanCacPublishReport report = await VanCacPublishRunner.RunAsync(chapters, total, sink,
                new(novel.Id, publish ? VanCacChapterStatus.Published : null, allowIncomplete), progress, cancellationToken);
            row.Stage = VanCacBatchPlanner.Summary(report);
            if (sink.AuthFailed) return SessionLost();
            await RefreshWebProgressAsync(row, client, novel);
            return report.Completed && report.Failures.Count == 0 ? BookOutcome.Done : BookOutcome.Problem;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            row.Stage = "Đã dừng";
            return BookOutcome.Problem;
        }
        catch (VanCacApiException ex) when (ex.IsAuthFailure)
        {
            row.Stage = "Lỗi: " + (ex.StatusCode == 401 ? VanCacAccountManager.SessionExpiredMessage : ex.Message);
            return SessionLost();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            row.Stage = "Lỗi: " + ex.Message;
            return BookOutcome.Problem;
        }
    }

    // The runner stops a book on 401/403; no later book can succeed either, so the batch stops too.
    private sealed class AuthTrackingSink(IVanCacChapterSink inner) : IVanCacChapterSink
    {
        public bool AuthFailed { get; private set; }
        public async Task<VanCacPutResult> PutChapterAsync(string novelId, int number, string title, string content,
            VanCacChapterStatus? status, CancellationToken cancellationToken)
        {
            try { return await inner.PutChapterAsync(novelId, number, title, content, status, cancellationToken); }
            catch (VanCacApiException ex) when (ex.IsAuthFailure) { AuthFailed = true; throw; }
        }
    }

    private BookOutcome SessionLost()
    {
        _account.ExpireSession();
        DropConnection();
        RefreshAccount();
        return BookOutcome.SessionLost;
    }

    private async Task<VanCacNovel?> CreateNovelAsync(VanCacPublisherClient client, ProjectSession session, string title,
        CancellationToken cancellationToken)
    {
        string? authorId = _user!.Roles.Contains("ADMIN") ? (AuthorInput.SelectedItem as VanCacAuthor)?.Id : _user.AuthorId;
        if (authorId is null) return null;
        string description = session.Manifest.Description.Length > 5000 ? session.Manifest.Description[..5000] : session.Manifest.Description;
        string[] genres = session.Manifest.Genres.Select(g => g.Trim()).Where(g => g.Length is > 0 and <= 50).Distinct().Take(8).ToArray();
        string name = title.Length > 200 ? title[..200].TrimEnd() : title;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return await client.CreateNovelAsync(new(name, VanCacBatchPlanner.NewSlug(name, session.Manifest.ProjectId, attempt),
                    authorId, description, genres, false), cancellationToken);
            }
            catch (VanCacApiException ex) when (ex.StatusCode == 409 && attempt < 2) { }
        }
    }

    private async Task RefreshWebProgressAsync(VanCacBatchRow row, VanCacPublisherClient client, VanCacNovel novel)
    {
        try { RecordWebProgress(row, novel, await client.ListChaptersAsync(novel.Id, _lifetime.Token)); }
        catch (Exception ex) when (ex is VanCacApiException or OperationCanceledException or System.Net.Http.HttpRequestException
            or System.IO.IOException or UnauthorizedAccessException) { }
    }

    private void RecordWebProgress(VanCacBatchRow row, VanCacNovel novel, IReadOnlyList<VanCacRemoteChapter> remote)
    {
        if (_account.Preferences.RecordWebProgress(novel.Id, remote)) TrySavePreferences();
        row.WebText = ProjectLibraryCard.WebLabelFor(_account.Preferences.WebProgressByProject.GetValueOrDefault(row.Book.ProjectId));
    }

    private void TrySavePreferences()
    {
        try { _account.SavePreferences(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { StatusText.Text = ex.Message; }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AccountButton.IsEnabled = CloseButton.IsEnabled = !busy;
        OnlyNewInput.IsEnabled = AllowIncompleteInput.IsEnabled = PublishNowInput.IsEnabled = KeepDraftInput.IsEnabled = AuthorInput.IsEnabled = !busy;
        BookGrid.IsReadOnly = busy;
        CancelButton.Visibility = busy && _cancellation is not null ? Visibility.Visible : Visibility.Collapsed;
        UpdateStart();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _cancellation?.Cancel();
        StatusText.Text = "Sẽ dừng sau chương đang gửi…";
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy) { _lifetime.Cancel(); _client?.Dispose(); return; }
        e.Cancel = true;
        _cancellation?.Cancel();
        StatusText.Text = "Đang dừng an toàn, đợi chương hiện tại gửi xong…";
    }
}
