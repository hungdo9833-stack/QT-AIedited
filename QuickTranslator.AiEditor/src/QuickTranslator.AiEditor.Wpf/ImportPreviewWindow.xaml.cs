using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ImportPreviewWindow : Window
{
    private readonly ProjectSession _project;
    private readonly ObservableCollection<PreviewRow> _rows = [];
    private ImportPreviewEditor? _editor;
    private bool _loading;

    public ImportPreviewWindow(ProjectSession project)
    {
        _project = project ?? throw new ArgumentNullException(nameof(project));
        InitializeComponent();
        PreviewGrid.ItemsSource = _rows;
    }

    public ImportPreviewPlan? ConfirmedPlan { get; private set; }
    public string? SourcePath { get; private set; }
    internal ImportPreviewPlan? CurrentPlan => _editor?.Current;

    internal async Task LoadSourceAsync(string path, SourceTextEncoding encoding, CancellationToken token = default)
    {
        if (_loading) return;
        _loading = true;
        PreviewStatusText.Text = "Đang đọc và nhận diện…";
        ConfirmButton.IsEnabled = false;
        ConfirmedPlan = null;
        _editor = null;
        _rows.Clear();
        try
        {
            RawSourceSnapshot source = await RawSourceReader.ReadFileAsync(path, encoding, token);
            ImmutableImportPlan detected = await Task.Run(() => new ChapterDetector().Detect(source), token);
            _editor = ImportPreviewEditor.Create(detected);
            SourcePath = Path.GetFullPath(path);
            SourcePathInput.Text = SourcePath;
            RefreshRows();
            PreviewStatusText.Text = "Đã nhận diện. Kiểm tra các cảnh báo trước khi nhập.";
        }
        finally { _loading = false; }
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Văn bản tiểu thuyết (*.txt)|*.txt|Tất cả tệp|*.*", Multiselect = false };
        if (dialog.ShowDialog(this) != true) return;
        SourcePathInput.Text = dialog.FileName;
        await AnalyzePathAsync(dialog.FileName);
    }

    private async void OnAnalyze(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SourcePathInput.Text)) { ShowError("Hãy chọn một tệp TXT."); return; }
        await AnalyzePathAsync(SourcePathInput.Text);
    }

    private async Task AnalyzePathAsync(string path)
    {
        try { await LoadSourceAsync(path, SelectedEncoding()); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        { ShowError(ex.Message); }
    }

    private SourceTextEncoding SelectedEncoding() => Enum.Parse<SourceTextEncoding>(
        ((EncodingInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Auto"), ignoreCase: true);

    private void RefreshRows(Guid? select = null)
    {
        if (_editor is null) return;
        ImportPreviewPlan plan = _editor.Current;
        Guid? selectedId = select ?? (PreviewGrid.SelectedItem as PreviewRow)?.Id;
        string query = SearchInput.Text.Trim();
        string filter = (FilterInput.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        _rows.Clear();
        var projectIssues = new List<string>();
        foreach (ImportPreviewItem item in plan.Items)
        {
            string[] problems = plan.Issues.Where(x => x.ItemIds.Contains(item.Id)).Select(x => x.Message).ToArray();
            string? collision = ProjectCollision(item);
            if (collision is not null) { problems = [.. problems, collision]; projectIssues.Add(collision); }
            var row = new PreviewRow(item, problems);
            bool matchesQuery = query.Length == 0 || row.NumberLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || row.VolumeTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase);
            bool matchesFilter = filter switch
            {
                "Problems" => problems.Length > 0,
                "Included" => item.Included,
                "Unclassified" => item.Kind == PlannedRegionKind.Unclassified,
                _ => true
            };
            if (matchesQuery && matchesFilter) _rows.Add(row);
        }
        PreviewGrid.SelectedItem = _rows.FirstOrDefault(x => x.Id == selectedId) ?? _rows.FirstOrDefault();
        IssuesList.ItemsSource = plan.Issues.Select(x => SeverityLabel(x.Severity) + " " + x.Message)
            .Concat(projectIssues.Distinct().Select(x => "● " + x)).ToArray();
        int collisions = plan.ImportableChapters.Count(x => ProjectCollision(x) is not null);
        int problemsCount = plan.Issues.Count(x => x.Severity != DetectionIssueSeverity.Information) + collisions;
        ImportCountText.Text = $"{plan.ImportableChapters.Count():N0} chương";
        ProblemCountText.Text = $"{problemsCount:N0} mục";
        SourceSummaryText.Text = $"{plan.Source.EncodingName} · {plan.Source.Text.Length:N0} ký tự · {plan.Source.NewLineStyle}";
        UndoButton.IsEnabled = _editor.CanUndo;
        ConfirmButton.IsEnabled = !plan.HasBlockingIssues && collisions == 0 && plan.ImportableChapters.Any();
        FooterHintText.Text = ConfirmButton.IsEnabled
            ? $"Sẵn sàng nhập {plan.ImportableChapters.Count():N0} chương. Source hash {plan.Source.RawSha256[..12]}…"
            : "Cần xử lý lỗi màu cảnh báo hoặc collision trước khi nhập. Nguồn TXT vẫn giữ nguyên.";
        UpdateSelection();
    }

    private void OnFilterChanged(object sender, EventArgs e)
    {
        if (IsLoaded && _editor is not null) RefreshRows();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        if (_editor is null || PreviewGrid.SelectedItem is not PreviewRow row) return;
        ImportPreviewItem item = _editor.Current.Items.Single(x => x.Id == row.Id);
        NumberInput.Text = item.Number?.ToString() ?? "";
        TitleInput.Text = item.Title;
        NumberInput.IsEnabled = TitleInput.IsEnabled = IncludeButton.IsEnabled = item.IsChapter;
        IncludeButton.Content = item.Included ? "Loại khỏi import" : "Đưa vào import";
        StartContextText.Text = Context(item.Span.StartUtf16, "⟦ BẮT ĐẦU ⟧");
        EndContextText.Text = Context(item.Span.EndUtf16, "⟦ KẾT THÚC ⟧");
        SelectedDetailText.Text = $"{row.Status} · span {item.Span.StartUtf16:N0}–{item.Span.EndUtf16:N0} · " +
            (item.Kind == PlannedRegionKind.Unclassified ? "Vùng này chỉ nằm trong source rescue." :
                $"confidence decision: {item.Decision}") + (row.Problems.Length == 0 ? "" : "\n" + string.Join("\n", row.Problems));
    }

    private string Context(int boundary, string marker)
    {
        string source = _editor!.Current.Source.Text;
        int before = Math.Max(0, boundary - 260), after = Math.Min(source.Length, boundary + 260);
        return source[before..boundary] + marker + source[boundary..after];
    }

    private void OnApplyEdit(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        try
        {
            int? number = string.IsNullOrWhiteSpace(NumberInput.Text) ? null : int.Parse(NumberInput.Text);
            _editor!.UpdateChapter(row.Id, number, TitleInput.Text);
            RefreshRows(row.Id);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { ShowError(ex.Message); }
    }

    private void OnToggleInclude(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        ImportPreviewItem item = _editor!.Current.Items.Single(x => x.Id == row.Id);
        try { _editor.SetIncluded(row.Id, !item.Included); RefreshRows(row.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnSplit(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        ImportPreviewItem item = _editor!.Current.Items.Single(x => x.Id == row.Id);
        LineChoice[] choices = _editor.Current.Lines.Where(x => x.FullSpan.StartUtf16 > item.ContentSpan.StartUtf16 && x.FullSpan.StartUtf16 < item.Span.EndUtf16)
            .Select(x => new LineChoice(x.FullSpan.StartUtf16, $"Dòng {x.Number}: {Short(x.Content)}")).ToArray();
        if (!TryChooseBoundary("Tách chương", choices, (item.Number ?? 0) + 1, "Chương tách mới", out int offset, out int number, out string title)) return;
        try { _editor.Split(row.Id, offset, number, title); RefreshRows(row.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnMerge(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        try { _editor!.MergeWithNext(row.Id); RefreshRows(row.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnMoveBoundary(object sender, RoutedEventArgs e)
    {
        if (!TrySelectedChapter(out PreviewRow? row)) return;
        ImportPreviewItem item = _editor!.Current.Items.Single(x => x.Id == row.Id);
        int index = _editor.Current.Items.IndexOf(item);
        if (index + 1 >= _editor.Current.Items.Length || !_editor.Current.Items[index + 1].IsChapter)
        { ShowError("Không có chương liền kề phía sau."); return; }
        ImportPreviewItem next = _editor.Current.Items[index + 1];
        LineChoice[] choices = _editor.Current.Lines.Where(x => x.FullSpan.StartUtf16 > item.ContentSpan.StartUtf16 && x.FullSpan.StartUtf16 < next.Span.EndUtf16)
            .Select(x => new LineChoice(x.FullSpan.StartUtf16, $"Dòng {x.Number}: {Short(x.Content)}")).ToArray();
        if (!TryChooseBoundary("Dời ranh giới", choices, next.Number ?? 1, next.Title, out int offset, out _, out _)) return;
        try { _editor.MoveBoundary(row.Id, offset); RefreshRows(row.Id); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private void OnUndo(object sender, RoutedEventArgs e) { _editor?.Undo(); RefreshRows(); }

    private bool TryChooseBoundary(string title, LineChoice[] choices, int suggestedNumber, string suggestedTitle,
        out int offset, out int number, out string chapterTitle)
    {
        offset = 0; number = suggestedNumber; chapterTitle = suggestedTitle;
        if (choices.Length == 0) { ShowError("Không có đầu dòng hợp lệ trong vùng đã chọn."); return false; }
        var lines = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(LineChoice.Label), SelectedIndex = 0, Margin = new Thickness(8) };
        var numberInput = new TextBox { Text = suggestedNumber.ToString(), Margin = new Thickness(8) };
        var titleInput = new TextBox { Text = suggestedTitle, Margin = new Thickness(8) };
        var ok = new Button { Content = "Áp dụng", IsDefault = true }; var cancel = new Button { Content = "Hủy", IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = "Chọn dòng bắt đầu", Foreground = System.Windows.Media.Brushes.Gainsboro }); panel.Children.Add(lines);
        panel.Children.Add(new TextBlock { Text = "Số chương", Foreground = System.Windows.Media.Brushes.Gainsboro }); panel.Children.Add(numberInput);
        panel.Children.Add(new TextBlock { Text = "Tên chương", Foreground = System.Windows.Media.Brushes.Gainsboro }); panel.Children.Add(titleInput); panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = title, Width = 620, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 27, 45)), Content = panel };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true || lines.SelectedItem is not LineChoice selected || !int.TryParse(numberInput.Text, out number) || number <= 0) return false;
        offset = selected.Offset; chapterTitle = titleInput.Text.Trim(); return true;
    }

    private bool TrySelectedChapter([NotNullWhen(true)] out PreviewRow? row)
    {
        row = PreviewGrid.SelectedItem as PreviewRow;
        if (row is null || _editor is null)
        { ShowError("Hãy chọn một chương."); return false; }
        Guid selectedId = row.Id;
        if (!_editor.Current.Items.Single(x => x.Id == selectedId).IsChapter)
        { ShowError("Hãy chọn một chương."); return false; }
        return true;
    }

    private string? ProjectCollision(ImportPreviewItem item)
    {
        if (!item.Included || item.Number is not int number) return null;
        ProjectChapterEntry? id = _project.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == item.ChapterId);
        if (id is not null) return id.DeletedAt is null ? null : "ChapterId nằm trong History/xóa mềm.";
        ProjectChapterEntry? used = _project.Manifest.Chapters.SingleOrDefault(x => x.DeletedAt is null && x.Number == number);
        return used is null ? null : $"Số {number} đã thuộc “{used.VietnameseTitle}”.";
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_editor is null || !ConfirmButton.IsEnabled) return;
        ConfirmedPlan = _editor.Current;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
    private void ShowError(string message) { PreviewStatusText.Text = "Lỗi: " + message; MessageBox.Show(this, message, "Import Preview", MessageBoxButton.OK, MessageBoxImage.Error); }
    private static string SeverityLabel(DetectionIssueSeverity severity) => severity switch { DetectionIssueSeverity.Error => "●", DetectionIssueSeverity.Warning => "▲", _ => "ℹ" };
    private static string Short(string value) => value.Length <= 70 ? value : value[..67] + "…";

    private sealed record LineChoice(int Offset, string Label);
    private sealed record PreviewRow(Guid Id, string IncludedLabel, string NumberLabel, string Title, string VolumeTitle,
        string Status, string ProblemLabel, string[] Problems)
    {
        public PreviewRow(ImportPreviewItem item, string[] problems) : this(item.Id, item.Included ? "✓" : "—",
            item.Number?.ToString() ?? "—", item.Kind == PlannedRegionKind.Unclassified ? "Vùng chưa phân loại" : item.Title,
            item.VolumeTitle ?? "", item.Kind switch
            {
                PlannedRegionKind.Volume => "Quyển", PlannedRegionKind.Unclassified => "Chưa phân loại",
                _ when item.Decision == CandidateDecision.NeedsReview => "Cần xem", _ => "Tin cậy"
            }, problems.Length == 0 ? "" : string.Join(" · ", problems), problems) { }
    }
}
