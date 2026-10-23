using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Wpf;

public partial class WebImportWindow : Window
{
    private readonly ProjectSession _project;
    private readonly Func<WebImportOptions, IWebNovelImporter> _importerFactory;
    private readonly Func<string, Task> _openImportPreview;
    private IWebNovelImporter? _importer;
    private WebImportOptions? _options;
    private WebBookAnalysis? _analysis;
    private CancellationTokenSource? _cancellation;

    internal WebImportWindow(ProjectSession project, Func<WebImportOptions, IWebNovelImporter> importerFactory,
        Func<string, Task> openImportPreview)
    {
        _project = project;
        _importerFactory = importerFactory;
        _openImportPreview = openImportPreview;
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 1020, 760);
        Closed += (_, _) => (_importer as IDisposable)?.Dispose();
    }

    internal string? ImportPath { get; private set; }

    private async void OnAnalyze(object sender, RoutedEventArgs e) => await AnalyzeAsync();
    private async void OnPreview(object sender, RoutedEventArgs e) => await PreviewAsync();
    private async void OnDownload(object sender, RoutedEventArgs e) => await DownloadAsync();
    private void OnCancel(object sender, RoutedEventArgs e) => Cancel();
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    internal Task AnalyzeAsync() => RunAsync(async token =>
    {
        _analysis = null;
        ChapterList.ItemsSource = null;
        BookTitleText.Text = "Chưa phân tích truyện";
        ChapterCountText.Text = "0 chương";
        DetectionSummaryText.Text = WarningsText.Text = "";
        ClearPreview();
        if (!Uri.TryCreate(UrlInput.Text.Trim(), UriKind.Absolute, out Uri? url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException("Hãy nhập URL http hoặc https hợp lệ.");
        StatusText.Text = "Đang phân tích mục lục…";
        WebBookAnalysis analysis = await GetImporter().AnalyzeAsync(url, token);
        token.ThrowIfCancellationRequested();
        if (analysis.Chapters.Count == 0)
            throw new WebImportBlockedException(url, WebImportBlockReason.NoChapterList, "Không tìm thấy mục lục.");
        _analysis = analysis;
        KeepSiteOrderCheckBox.IsChecked = analysis.NumberingConflict;
        BookTitleText.Text = analysis.BookTitle ?? "Truyện chưa có tên";
        ChapterCountText.Text = $"{analysis.Chapters.Count:N0} chương";
        DetectionSummaryText.Text = analysis.DetectionSummary;
        WarningsText.Text = string.Join(Environment.NewLine, analysis.Warnings);
        ChapterList.ItemsSource = analysis.Chapters.Select((chapter, index) => $"{index + 1} · {chapter.Title}").ToArray();
        FromInput.Text = "1";
        ToInput.Text = analysis.Chapters.Count.ToString(CultureInfo.InvariantCulture);
        StatusText.Text = "Đã phân tích. Hãy chọn phạm vi và xem trước nội dung.";
    });

    internal Task PreviewAsync() => RunAsync(async token =>
    {
        ClearPreview();
        WebChapterLink first = SelectedChapters()[0];
        StatusText.Text = $"Đang xem trước · {first.Title}";
        WebChapterContent chapter = await GetImporter().FetchChapterAsync(first, token);
        token.ThrowIfCancellationRequested();
        PreviewTitleText.Text = chapter.Title;
        PreviewPageCountText.Text = $"{chapter.PageCount:N0} trang · 30 dòng đầu";
        using var reader = new StringReader(chapter.Text);
        var lines = new List<string>();
        while (lines.Count < 30 && reader.ReadLine() is string line) lines.Add(line);
        PreviewText.Text = string.Join(Environment.NewLine, lines);
        StatusText.Text = string.IsNullOrWhiteSpace(chapter.Text)
            ? $"Chương {chapter.Link.Index}: nội dung trống. Bạn vẫn có thể tải và nhập."
            : "Hãy kiểm tra nội dung xem trước trước khi tải và nhập.";
    });

    internal Task DownloadAsync() => RunAsync(async token =>
    {
        IReadOnlyList<WebChapterLink> links = SelectedChapters();
        IWebNovelImporter importer = GetImporter();
        var chapters = new List<WebChapterContent>();
        ImportPath = null;
        WarningsText.Text = string.Join(Environment.NewLine, _analysis?.Warnings ?? []);
        StatusText.Text = $"0/{links.Count} · Đang tải chương…";
        BusyProgress.IsIndeterminate = false;
        BusyProgress.Maximum = links.Count;
        BusyProgress.Value = 0;
        CancellationTokenSource? operation = _cancellation;
        var progress = new Progress<WebImportProgress>(value =>
        {
            if (_cancellation != operation || token.IsCancellationRequested) return;
            BusyProgress.Maximum = Math.Max(1, value.Total);
            BusyProgress.Value = value.Completed;
            StatusText.Text = $"{value.Completed}/{value.Total} · {value.CurrentTitle}";
        });
        bool cancelled = false;
        try
        {
            await foreach (WebChapterContent chapter in importer.FetchChaptersAsync(links, progress, token).WithCancellation(token))
            {
                chapters.Add(chapter);
                token.ThrowIfCancellationRequested();
            }
            cancelled = token.IsCancellationRequested;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { cancelled = true; }

        if (chapters.Count == 0)
        {
            if (!cancelled) throw new InvalidDataException("Không tải được chương nào có nội dung. Chưa tạo tệp nhập.");
            StatusText.Text = "Đã hủy; chưa tải xong chương nào.";
            return;
        }
        var issues = WebImportQualityReport.Analyze(chapters);
        WarningsText.Text = string.Join(Environment.NewLine, (_analysis?.Warnings ?? []).Concat(issues.Select(issue => issue.Message)));
        string text = importer.ComposeImportText(chapters, KeepSiteOrderCheckBox.IsChecked == true);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidDataException("Nội dung nhập trống. Hãy kiểm tra lại bản xem trước.");

        // Keep completed chapters even when the download token was cancelled.
        CancelButton.IsEnabled = false;
        string directory = _project.Resolve("Cache/web-import");
        Directory.CreateDirectory(directory);
        string title = _analysis?.BookTitle ?? "truyen-web";
        string path = Path.Combine(directory, $"{SafeFileName(title)}-{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}.txt");
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false));
        ImportPath = path;
        StatusText.Text = cancelled
            ? $"Đã hủy tải; giữ {chapters.Count:N0} chương đã tải để xem trước khi nhập."
            : $"Đã tải {chapters.Count:N0} chương. Đang mở bản xem trước nhập TXT…";
        await _openImportPreview(path);
    });

    private IWebNovelImporter GetImporter()
    {
        string delayText = DelayInput.Text.Trim().Replace(',', '.');
        if (!double.TryParse(delayText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds) ||
            !double.IsFinite(seconds) || seconds < 0.5 || seconds > TimeSpan.MaxValue.TotalSeconds)
            throw new InvalidOperationException("Thời gian nghỉ giữa các yêu cầu phải là số giây, tối thiểu 0,5.");
        var options = new WebImportOptions
        {
            DelayBetweenRequests = TimeSpan.FromSeconds(seconds),
            CacheDirectory = _project.Resolve("Cache/web-import/pages")
        };
        if (_importer is null || _options != options)
        {
            Directory.CreateDirectory(options.CacheDirectory);
            (_importer as IDisposable)?.Dispose(); // Each importer owns an HttpClient.
            _importer = _importerFactory(options);
            _options = options;
        }
        return _importer;
    }

    private IReadOnlyList<WebChapterLink> SelectedChapters()
    {
        if (_analysis is null) throw new InvalidOperationException("Hãy phân tích mục lục trước.");
        if (!int.TryParse(FromInput.Text, out int from) || !int.TryParse(ToInput.Text, out int to) ||
            from < 1 || to < from || to > _analysis.Chapters.Count)
            throw new InvalidOperationException($"Phạm vi phải nằm từ 1 đến {_analysis.Chapters.Count}, với vị trí đầu không lớn hơn vị trí cuối.");
        return _analysis.Chapters.Skip(from - 1).Take(to - from + 1).ToArray();
    }

    private static string SafeFileName(string title)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string safe = new(title.Select(c => invalid.Contains(c) ? '_' : c).Take(80).ToArray());
        safe = safe.Trim().TrimEnd('.');
        return safe.Length == 0 ? "truyen-web" : safe;
    }

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_cancellation is not null) return;
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { StatusText.Text = "Đã hủy thao tác."; }
        catch (WebImportBlockedException ex)
        {
            ShowError(ex.Reason switch
            {
                WebImportBlockReason.RequiresBrowser or WebImportBlockReason.RequiresUserConfirmation or WebImportBlockReason.BotChallenge =>
                    "Trang cần trình duyệt hoặc xác nhận — sẽ hỗ trợ ở giai đoạn sau.",
                WebImportBlockReason.NotFound => "Không tìm thấy trang truyện. Hãy kiểm tra lại URL.",
                WebImportBlockReason.NoChapterList => "Không tìm thấy danh sách chương. Hãy nhập URL trang mục lục của truyện.",
                _ => ex.Message
            });
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _cancellation = null; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        UrlInput.IsEnabled = DelayInput.IsEnabled = KeepSiteOrderCheckBox.IsEnabled = AnalyzeButton.IsEnabled = CloseButton.IsEnabled = !busy;
        RangePanel.IsEnabled = PreviewButton.IsEnabled = DownloadButton.IsEnabled = !busy && _analysis is not null;
        CancelButton.IsEnabled = busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyProgress.IsIndeterminate = busy;
    }

    internal void Cancel()
    {
        if (_cancellation is null) return;
        CancelButton.IsEnabled = false;
        StatusText.Text = "Đang hủy; các chương đã tải xong sẽ được giữ…";
        _cancellation.Cancel();
    }

    private void ClearPreview() => PreviewText.Text = PreviewTitleText.Text = PreviewPageCountText.Text = "";

    private void ShowError(string message)
    {
        StatusText.Text = "Lỗi: " + message;
        ThemedDialog.Show(this, message, "Nhập truyện từ web", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_cancellation is null) return;
        e.Cancel = true;
        Cancel();
    }
}
