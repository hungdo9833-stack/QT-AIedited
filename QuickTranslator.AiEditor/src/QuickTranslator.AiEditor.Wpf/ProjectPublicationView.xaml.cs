using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ProjectPublicationView : UserControl
{
    private readonly ProjectSession _session;
    private readonly ProjectPublicationService _service;
    private readonly Func<Guid, Task>? _openChapter;
    private ProjectPublicationInspection? _wholeInspection;
    private ProjectPublicationInspection? _inspection; // _wholeInspection limited to the chosen range; null while the range is invalid.
    private string? _rangeError;
    private CancellationTokenSource? _operationCancellation;
    private Task? _inspectionTask;
    private bool _busy;
    private bool _attached;
    private bool _inspectionLoaded;
    private bool _showCloseButton = true;
    private int _inspectionLoadCount;

    internal ProjectPublicationView(ProjectSession session, ProjectPublicationService service,
        Func<Guid, Task>? openChapter = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _openChapter = openChapter;
        InitializeComponent();
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(ProgressText, (_, _) => Accessibility.Announce(ProgressText));
        ProjectTitleText.Text = session.Manifest.VietnameseTitle;
        UpdateFormatDescription();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public event EventHandler? CloseRequested;
    public event EventHandler<string>? Exported;

    internal string? LastExportPath { get; private set; }
    internal ProjectPublicationInspection? CurrentInspection => _inspection;
    internal int InspectionLoadCount => _inspectionLoadCount;
    internal bool IsBusy => _busy;
    internal bool ShowCloseButton
    {
        get => _showCloseButton;
        set
        {
            _showCloseButton = value;
            CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal void Attach()
    {
        if (_attached) return;
        _attached = true;
        if (!_inspectionLoaded) _ = EnsureInspectionAsync();
    }

    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;
    }

    internal Task LoadInspectionAsync() => EnsureInspectionAsync();

    internal async Task RefreshAsync()
    {
        if (_inspectionTask is not null) await _inspectionTask;
        _inspectionTask = null;
        _inspectionLoaded = false;
        await EnsureInspectionAsync();
    }

    internal void HandleHostClosing(CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true;
        _operationCancellation?.Cancel();
        ProgressText.Text = "Đang hủy tác vụ an toàn…";
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Attach();
    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private Task EnsureInspectionAsync()
    {
        if (_inspectionLoaded) return Task.CompletedTask;
        if (_inspectionTask?.IsCompleted == true) _inspectionTask = null;
        return _inspectionTask ??= LoadInspectionCoreAsync();
    }

    private async Task LoadInspectionCoreAsync()
    {
        SetBusy(true, "Đang kiểm tra các chương…");
        _operationCancellation = new();
        _inspectionLoadCount++;
        try
        {
            var progress = new Progress<ProjectPublicationProgress>(UpdateProgress);
            _wholeInspection = await _service.InspectAsync(_session, progress, _operationCancellation.Token, SelectedLanguage());
            ProjectTitleText.Text = _wholeInspection.ProjectTitle;
            _inspectionLoaded = true;
            ApplyRange();
            ProgressText.Text = _wholeInspection.TotalChapterCount == 0
                ? "Project chưa có chương đang hoạt động." : "Đã kiểm tra xong. Chọn định dạng, phạm vi và nơi lưu.";
        }
        catch (OperationCanceledException) { ProgressText.Text = "Đã hủy kiểm tra."; }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void OnRangeChanged(object sender, RoutedEventArgs e)
    {
        if (RangeInputsPanel is null || RangeHintText is null) return; // Raised while the XAML is still loading.
        ApplyRange();
    }

    /// <summary>Limits the inspection to the chosen chapters and refreshes counts, warnings and the issue list.</summary>
    private void ApplyRange()
    {
        string mode = (RangeModeInput.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        RangeInputsPanel.Visibility = mode == "All" ? Visibility.Collapsed : Visibility.Visible;
        RangeToLabel.Visibility = RangeToInput.Visibility = mode == "Range" ? Visibility.Visible : Visibility.Collapsed;
        RangeFromLabel.Text = mode == "Single" ? "Chương số" : "Từ chương";
        if (_wholeInspection is null) return;
        _rangeError = null;
        ProjectPublicationInspection scoped = _wholeInspection;
        if (mode != "All")
        {
            int? from = ParseChapterNumber(RangeFromInput.Text);
            int? to = mode == "Single" ? from : ParseChapterNumber(RangeToInput.Text);
            if (from is null || to is null)
                _rangeError = mode == "Single" ? "Nhập số chương cần xuất." : "Nhập số chương bắt đầu và kết thúc.";
            else if (from > to) _rangeError = "Chương bắt đầu phải nhỏ hơn hoặc bằng chương kết thúc.";
            else
            {
                scoped = _wholeInspection.WithRange(from, to);
                if (scoped.TotalChapterCount == 0) _rangeError = "Không có chương nào trong phạm vi này.";
            }
        }
        _inspection = _rangeError is null ? scoped : null;
        RangeHintText.Text = _rangeError ?? "Theo số chương ở cột Số trong Quản lý chương.";
        RangeHintText.Visibility = mode == "All" ? Visibility.Collapsed : Visibility.Visible;
        RangeHintText.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
            _rangeError is null ? "MutedBrush" : "WarningBrush");

        ChapterCountText.Text = _inspection is null ? "—" : _inspection.TotalChapterCount.ToString("N0");
        bool needsConfirmation = _inspection?.RequiresConfirmation == true;
        InspectionText.Text = _inspection is null ? _rangeError
            : needsConfirmation ? $"{_inspection.IncompleteChapterCount} chưa hoàn tất · {_inspection.InspectionChapterCount} cần kiểm tra"
            : SelectedLanguage() == PublicationLanguage.Chinese ? "Sẵn sàng · xuất bản gốc tiếng Trung"
            : _inspection.IsRange ? "Sẵn sàng · mọi chương trong phạm vi đều có bản dịch hợp lệ"
            : "Sẵn sàng · mọi chương đều có bản dịch hợp lệ";
        InspectionText.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
            _inspection is null || needsConfirmation ? "WarningBrush" : "SuccessBrush");
        AllowIncompleteInput.Visibility = needsConfirmation ? Visibility.Visible : Visibility.Collapsed;
        IReadOnlyList<ProjectPublicationIssueChapter> issues = _inspection?.IssueChapters ?? [];
        IssueChaptersList.ItemsSource = issues;
        IssueChaptersPanel.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateExportEnabled();
    }

    private static int? ParseChapterNumber(string text) =>
        int.TryParse(text.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int number) ? number : null;

    private async void OnIssueChapterDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_busy || _openChapter is null || IssueChaptersList.SelectedItem is not ProjectPublicationIssueChapter issue)
            return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        await _openChapter(issue.ChapterId);
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_inspection is null || _busy) return;
        PublicationFormat format = SelectedFormat();
        string extension = format switch { PublicationFormat.Pdf => ".pdf", PublicationFormat.Epub => ".epub", _ => ".txt" };
        var dialog = new SaveFileDialog
        {
            Filter = format switch
            {
                PublicationFormat.Pdf => "Tài liệu PDF (*.pdf)|*.pdf",
                PublicationFormat.Epub => "Sách điện tử EPUB (*.epub)|*.epub",
                _ => "Văn bản UTF-8 (*.txt)|*.txt"
            },
            DefaultExt = extension,
            AddExtension = true,
            FileName = SuggestedFileName(extension)
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        await ExportToPathAsync(dialog.FileName);
    }

    internal string SuggestedFileName(string extension) =>
        SanitizeFileName((_inspection?.ProjectTitle ?? _session.Manifest.VietnameseTitle) +
            (_inspection is null ? "" : RangeFileSuffix(_inspection)) +
            (SelectedLanguage() == PublicationLanguage.Chinese ? "-zh" : "")) + extension;

    internal async Task ExportToPathAsync(string path)
    {
        if (_inspection is null || _busy) return;
        PublicationFormat format = SelectedFormat();
        var options = new ProjectPublicationOptions(
            IncludeProjectTitleInput.IsChecked == true,
            IncludeChapterTitlesInput.IsChecked == true,
            AllowIncomplete: AllowIncompleteInput.IsChecked == true,
            Language: SelectedLanguage());
        SetBusy(true, "Đang xuất bản…");
        _operationCancellation = new();
        try
        {
            var progress = new Progress<ProjectPublicationProgress>(UpdateProgress);
            ProjectPublicationReport report = format == PublicationFormat.Pdf
                ? await ProjectPdfExporter.ExportAsync(_service, _session, _inspection, path, options,
                    progress, _operationCancellation.Token)
                : format == PublicationFormat.Epub
                    ? await _service.ExportEpubAsync(_session, _inspection, path, options, progress,
                        _operationCancellation.Token)
                : await _service.ExportTextAsync(_session, _inspection, path, options, progress,
                    _operationCancellation.Token);
            LastExportPath = report.OutputPath;
            OutputPathText.Text = "Đã xuất " + report.ExportedChapterCount.ToString("N0") + " chương:\n" + report.OutputPath;
            ExportedActionsPanel.Visibility = Visibility.Visible;
            ProgressText.Text = report.IncompleteChapterCount > 0 || report.InspectionChapterCount > 0
                ? "Đã xuất bản nháp theo xác nhận của người dùng. Dữ liệu Project không bị thay đổi."
                : "Xuất bản hoàn tất. Dữ liệu Project không bị thay đổi.";
            Exported?.Invoke(this, report.OutputPath);
        }
        catch (OperationCanceledException) { ProgressText.Text = "Đã hủy. File đích cũ được giữ nguyên."; }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
        }
    }

    private void OnOpenExportedFile(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LastExportPath) || !File.Exists(LastExportPath)) return;
        try { Process.Start(new ProcessStartInfo(LastExportPath) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnOpenExportFolder(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(LastExportPath)) return;
        string? dir = Path.GetDirectoryName(LastExportPath);
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SetBusy(bool busy, string? message = null)
    {
        _busy = busy;
        FormatInput.IsEnabled = !busy;
        LanguageInput.IsEnabled = !busy;
        IncludeProjectTitleInput.IsEnabled = !busy;
        IncludeChapterTitlesInput.IsEnabled = !busy;
        RangeModeInput.IsEnabled = RangeFromInput.IsEnabled = RangeToInput.IsEnabled = !busy;
        AllowIncompleteInput.IsEnabled = !busy && SelectedLanguage() == PublicationLanguage.Vietnamese;
        CloseButton.IsEnabled = !busy;
        CancelTaskButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (message is not null) ProgressText.Text = message;
        UpdateExportEnabled();
    }

    private void UpdateProgress(ProjectPublicationProgress progress)
    {
        ExportProgress.Maximum = Math.Max(1, progress.TotalChapterCount);
        ExportProgress.Value = progress.CompletedChapterCount;
        ProgressText.Text = $"{progress.CompletedChapterCount:N0}/{progress.TotalChapterCount:N0} · {progress.ChapterTitle}";
    }

    private void UpdateExportEnabled()
    {
        ExportButton.IsEnabled = !_busy && _inspection is { TotalChapterCount: > 0 } &&
            (!_inspection.RequiresConfirmation || AllowIncompleteInput.IsChecked == true);
        ExportButton.ToolTip = _busy ? "Đang kiểm tra chương hoặc thực hiện tác vụ."
            : _rangeError is not null ? _rangeError
            : _inspection is not { TotalChapterCount: > 0 } ? "Không có chương để duyệt để xuất."
            : _inspection.RequiresConfirmation && AllowIncompleteInput.IsChecked != true
                ? "Xác nhận cho phép xuất bản nháp/chương cần kiểm tra." : "Chọn nơi lưu và xuất bản.";
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatDescriptionText is not null) UpdateFormatDescription();
    }

    private async void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatDescriptionText is null) return; // Raised while the XAML is still loading.
        _wholeInspection = _inspection = null;
        AllowIncompleteInput.IsChecked = false;
        AllowIncompleteInput.Visibility = IssueChaptersPanel.Visibility = Visibility.Collapsed;
        IssueChaptersList.ItemsSource = null;
        ChapterCountText.Text = "Đang kiểm tra…";
        InspectionText.Text = "Đang kiểm tra ngôn ngữ đã chọn…";
        UpdateFormatDescription();
        await RefreshAsync();
    }

    private PublicationLanguage SelectedLanguage() =>
        (LanguageInput.SelectedItem as ComboBoxItem)?.Tag as string == "Chinese"
            ? PublicationLanguage.Chinese : PublicationLanguage.Vietnamese;

    private void UpdateFormatDescription() => FormatDescriptionText.Text = SelectedFormat() switch
    {
        PublicationFormat.Pdf when SelectedLanguage() == PublicationLanguage.Chinese =>
            "PDF khổ A4, mỗi chương bắt đầu ở trang mới; giữ nguyên bản gốc tiếng Trung và dùng phông chữ hỗ trợ tiếng Trung.",
        PublicationFormat.Pdf => "PDF khổ A4, mỗi chương bắt đầu ở trang mới; nội dung tiếng Việt dùng cùng bộ ghép văn bản với Reader/TXT.",
        PublicationFormat.Epub => "EPUB có mục lục nhấn để mở từng chương và liên kết quay lại mục lục. Bố cục tự co giãn, giãn dòng 1,6; đổi cỡ chữ, phông chữ và chế độ tối trong ứng dụng đọc. Mục lục luôn giữ tên chương dù bỏ chọn tiêu đề trong nội dung.",
        _ when SelectedLanguage() == PublicationLanguage.Chinese => "TXT UTF-8 giữ nguyên tiêu đề và nội dung bản gốc tiếng Trung.",
        _ => "TXT UTF-8 giữ nguyên nội dung tiếng Việt do Reader và trình xuất chương đang sử dụng."
    };

    private PublicationFormat SelectedFormat() => ((FormatInput.SelectedItem as ComboBoxItem)?.Tag as string) switch
    {
        "Pdf" => PublicationFormat.Pdf,
        "Epub" => PublicationFormat.Epub,
        _ => PublicationFormat.Txt
    };

    private void OnAllowIncompleteChanged(object sender, RoutedEventArgs e) => UpdateExportEnabled();
    private void OnCancelTask(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void ShowError(Exception ex)
    {
        ProgressText.Text = "Lỗi: " + ex.Message;
        ProgressText.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "DangerBrush");
        ThemedDialog.Show(Window.GetWindow(this), ex.Message, "Xuất bản", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string RangeFileSuffix(ProjectPublicationInspection inspection) => inspection switch
    {
        { FromNumber: int from, ToNumber: int to } when from == to => $" - Chương {from}",
        { FromNumber: int from, ToNumber: int to } => $" - Chương {from}-{to}",
        _ => ""
    };

    private static string SanitizeFileName(string value)
    {
        string safe = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "QT-Studio-Export" : safe;
    }

    private enum PublicationFormat { Txt, Pdf, Epub }
}
