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
    private ProjectPublicationInspection? _inspection;
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
            _inspection = await _service.InspectAsync(_session, progress, _operationCancellation.Token);
            _inspectionLoaded = true;
            ChapterCountText.Text = _inspection.TotalChapterCount.ToString("N0");
            InspectionText.Text = _inspection.RequiresConfirmation
                ? $"{_inspection.IncompleteChapterCount} chưa hoàn tất · {_inspection.InspectionChapterCount} cần kiểm tra"
                : "Sẵn sàng · mọi chương đều có bản dịch hợp lệ";
            InspectionText.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty,
                _inspection.RequiresConfirmation ? "WarningBrush" : "SuccessBrush");
            AllowIncompleteInput.Visibility = _inspection.RequiresConfirmation ? Visibility.Visible : Visibility.Collapsed;
            IReadOnlyList<ProjectPublicationIssueChapter> issues = _inspection.IssueChapters ?? [];
            IssueChaptersList.ItemsSource = issues;
            IssueChaptersPanel.Visibility = issues.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ProgressText.Text = _inspection.TotalChapterCount == 0
                ? "Project chưa có chương đang hoạt động." : "Đã kiểm tra xong. Chọn định dạng và nơi lưu.";
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
        string extension = format == PublicationFormat.Pdf ? ".pdf" : ".txt";
        var dialog = new SaveFileDialog
        {
            Filter = format == PublicationFormat.Pdf ? "Tài liệu PDF (*.pdf)|*.pdf" : "Văn bản UTF-8 (*.txt)|*.txt",
            DefaultExt = extension,
            AddExtension = true,
            FileName = SanitizeFileName(_session.Manifest.VietnameseTitle) + extension
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var options = new ProjectPublicationOptions(
            IncludeProjectTitleInput.IsChecked == true,
            IncludeChapterTitlesInput.IsChecked == true,
            AllowIncomplete: AllowIncompleteInput.IsChecked == true);
        SetBusy(true, "Đang xuất bản…");
        _operationCancellation = new();
        try
        {
            var progress = new Progress<ProjectPublicationProgress>(UpdateProgress);
            ProjectPublicationReport report = format == PublicationFormat.Pdf
                ? await ProjectPdfExporter.ExportAsync(_service, _session, _inspection, dialog.FileName, options,
                    progress, _operationCancellation.Token)
                : await _service.ExportTextAsync(_session, _inspection, dialog.FileName, options, progress,
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
        IncludeProjectTitleInput.IsEnabled = !busy;
        IncludeChapterTitlesInput.IsEnabled = !busy;
        AllowIncompleteInput.IsEnabled = !busy;
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
            : _inspection is not { TotalChapterCount: > 0 } ? "Không có chương để duyệt để xuất."
            : _inspection.RequiresConfirmation && AllowIncompleteInput.IsChecked != true
                ? "Xác nhận cho phép xuất bản nháp/chương cần kiểm tra." : "Chọn nơi lưu và xuất bản.";
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatDescriptionText is not null) UpdateFormatDescription();
    }

    private void UpdateFormatDescription() => FormatDescriptionText.Text = SelectedFormat() == PublicationFormat.Pdf
        ? "PDF khổ A4, mỗi chương bắt đầu ở trang mới; nội dung tiếng Việt dùng cùng bộ ghép văn bản với Reader/TXT."
        : "TXT UTF-8 giữ nguyên nội dung tiếng Việt do Reader và trình xuất chương đang sử dụng.";

    private PublicationFormat SelectedFormat() =>
        string.Equals((FormatInput.SelectedItem as ComboBoxItem)?.Tag as string, "Pdf", StringComparison.Ordinal)
            ? PublicationFormat.Pdf : PublicationFormat.Txt;

    private void OnAllowIncompleteChanged(object sender, RoutedEventArgs e) => UpdateExportEnabled();
    private void OnCancelTask(object sender, RoutedEventArgs e) => _operationCancellation?.Cancel();
    private void OnClose(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void ShowError(Exception ex)
    {
        ProgressText.Text = "Lỗi: " + ex.Message;
        ProgressText.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "DangerBrush");
        ThemedDialog.Show(Window.GetWindow(this), ex.Message, "Xuất bản", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private static string SanitizeFileName(string value)
    {
        string safe = string.Concat(value.Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "QT-Studio-Export" : safe;
    }

    private enum PublicationFormat { Txt, Pdf }
}
