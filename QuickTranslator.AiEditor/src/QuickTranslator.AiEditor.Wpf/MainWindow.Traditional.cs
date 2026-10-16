using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Traditional;
using CoreTextRange = QuickTranslator.AiEditor.Core.Traditional.TextRange;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private readonly TraditionalTranslationService _traditionalService = new(cacheCapacity: 96);
    private readonly TraditionalEditorPreferencesStore _traditionalPreferencesStore = new(TraditionalEditorPreferencesStore.DefaultPath);
    private readonly AlignmentDiagnosticsLogger _alignmentDiagnostics = new();
    private readonly HashSet<TraditionalPane> _hiddenTraditionalPanes = [];
    private TraditionalTranslationResult? _traditionalResult;
    private Guid? _traditionalDisplayedSegmentId;
    private TraditionalPane? _maximizedTraditionalPane;
    private CancellationTokenSource? _traditionalLoadCancellation;
    private CancellationTokenSource? _traditionalTranslationCancellation;
    private string? _traditionalRequestKey;
    private bool _syncingTraditionalSelection;
    private bool _loadingTraditionalPreferences;
    private bool _showAiComparison;
    private TextBoxRangeHighlightAdorner? _chineseHighlight;
    private TextBoxRangeHighlightAdorner? _hanVietHighlight;
    private TextBoxRangeHighlightAdorner? _vietPhraseHighlight;

    private void InitializeTraditionalEditor()
    {
        Loaded += OnTraditionalEditorLoaded;
        Loaded += (_, _) => InitializeTraditionalHighlightLayers();
        Closed += (_, _) =>
        {
            _traditionalLoadCancellation?.Cancel();
            _traditionalTranslationCancellation?.Cancel();
        };
        ApplyTraditionalPaneLayout();
        AlignmentDiagnosticsPathText.Text = "Log: " + AlignmentDiagnosticsLogger.DefaultPath;
    }

    private void OnToggleAiComparison(object sender, RoutedEventArgs e)
    {
        _showAiComparison = !_showAiComparison;
        UpdateAiComparisonVisibility();
    }

    private void UpdateAiComparisonVisibility()
    {
        if (AiText is null) return;
        bool hasDiff = !string.IsNullOrWhiteSpace(AiText.Text) &&
                       !string.Equals(AiText.Text.Trim(), DraftText.Text.Trim(), StringComparison.Ordinal);
        bool visible = _showAiComparison || hasDiff;
        AiText.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (FindName("ToggleAiCompareButton") is Button toggleBtn)
            toggleBtn.Content = visible ? "Ẩn bản AI" : "So sánh AI gốc";
    }

    private async void OnTraditionalEditorLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnTraditionalEditorLoaded;
        _loadingTraditionalPreferences = true;
        try
        {
            var preferences = await _traditionalPreferencesStore.LoadAsync();
            TraditionalDataDirectoryInput.Text = preferences.DataDirectory;
            TraditionalPrioritizeNamesInput.IsChecked = preferences.PrioritizeNames;
            TraditionalAlgorithmInput.SelectedIndex = Math.Clamp(preferences.TranslationAlgorithm, 0, 2);
            TraditionalAlignmentDiagnosticsInput.IsChecked = preferences.EnableAlignmentDiagnostics;
            if (!string.IsNullOrWhiteSpace(preferences.DataDirectory))
                await LoadTraditionalDataUiAsync(preferences.DataDirectory, savePreferences: false);
        }
        catch (Exception ex)
        {
            TraditionalDataStatus.Text = "Không đọc được thiết lập/Data truyền thống. AI Editor vẫn dùng được: " + ex.Message;
            TraditionalDiagnosticsText.Text = ex.ToString();
        }
        finally { _loadingTraditionalPreferences = false; }
    }

    private async void OnBrowseTraditionalData(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Chọn thư mục Data có Dictionaries.ini",
            Multiselect = false,
            InitialDirectory = Directory.Exists(TraditionalDataDirectoryInput.Text) ? TraditionalDataDirectoryInput.Text : null
        };
        if (dialog.ShowDialog(this) != true) return;
        TraditionalDataDirectoryInput.Text = dialog.FolderName;
        await LoadTraditionalDataUiAsync(dialog.FolderName, savePreferences: true);
    }

    private async void OnReloadTraditionalData(object sender, RoutedEventArgs e) =>
        await LoadTraditionalDataUiAsync(TraditionalDataDirectoryInput.Text, savePreferences: true);

    private async Task LoadTraditionalDataUiAsync(string directory, bool savePreferences)
    {
        _traditionalLoadCancellation?.Cancel();
        _traditionalLoadCancellation?.Dispose();
        _traditionalLoadCancellation = new();
        var token = _traditionalLoadCancellation.Token;
        ReloadTraditionalDataButton.IsEnabled = false;
        TraditionalDataStatus.Text = "Đang nạp và kiểm tra Data ngoài luồng giao diện…";
        TraditionalDiagnosticsText.Clear();
        try
        {
            var manifest = await _traditionalService.LoadDataAsync(directory, token);
            TraditionalDataDirectoryInput.Text = manifest.RootDirectory;
            TraditionalDataStatus.Text = $"Đã nạp: {manifest.HanVietEntries:N0} Hán Việt · {manifest.VietPhraseEntries:N0} VietPhrase · " +
                $"{manifest.NameEntries:N0} tên · {manifest.RuleEntries:N0} luật. Data chỉ được đọc.";
            TraditionalDiagnosticsText.Text = manifest.Diagnostics.Count == 0 ? "Không phát hiện vấn đề cấu trúc." :
                string.Join(Environment.NewLine, manifest.Diagnostics.Select(item =>
                    $"{item.Severity}: {item.File}{(item.Line is int line ? $" dòng {line}" : "")} — {item.Message}"));
            _traditionalResult = null;
            _traditionalDisplayedSegmentId = null;
            _traditionalRequestKey = null;
            ClearTraditionalHighlights();
            if (savePreferences) await SaveTraditionalPreferencesAsync();
            var selected = _service?.GetSnapshot().Segments.SingleOrDefault(segment => segment.Id == _selectedId);
            RefreshTraditionalSegment(selected);
            SetStatus("Đã nạp Data QuickTranslator. Hán Việt và VP chạy offline bằng CPU.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            TraditionalDataStatus.Text = "Không nạp được Data; dịch AI và Reader vẫn hoạt động.";
            TraditionalDiagnosticsText.Text = ex is TraditionalDataException dataError
                ? string.Join(Environment.NewLine, dataError.Diagnostics.Select(item => $"{item.Severity}: {item.File} — {item.Message}"))
                : ex.Message;
            SetStatus("Data truyền thống chưa sẵn sàng: " + ex.Message);
        }
        finally { ReloadTraditionalDataButton.IsEnabled = true; }
    }

    private async void OnTraditionalPreferenceChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loadingTraditionalPreferences) return;
        bool translationChanged = !ReferenceEquals(sender, TraditionalAlignmentDiagnosticsInput);
        if (translationChanged)
        {
            _traditionalResult = null;
            _traditionalDisplayedSegmentId = null;
            _traditionalRequestKey = null;
            ClearTraditionalHighlights();
        }
        try
        {
            await SaveTraditionalPreferencesAsync();
            if (translationChanged)
            {
                var selected = _service?.GetSnapshot().Segments.SingleOrDefault(segment => segment.Id == _selectedId);
                RefreshTraditionalSegment(selected);
            }
        }
        catch (Exception ex) { SetStatus("Chưa lưu được thiết lập dịch truyền thống: " + ex.Message); }
    }

    private Task SaveTraditionalPreferencesAsync() => _traditionalPreferencesStore.SaveAsync(new()
    {
        DataDirectory = TraditionalDataDirectoryInput.Text,
        PrioritizeNames = TraditionalPrioritizeNamesInput.IsChecked == true,
        TranslationAlgorithm = Math.Clamp(TraditionalAlgorithmInput.SelectedIndex, 0, 2),
        EnableAlignmentDiagnostics = TraditionalAlignmentDiagnosticsInput.IsChecked == true
    });

    private void RefreshTraditionalSegment(TranslationSegment? segment)
    {
        if (segment is null)
        {
            _traditionalTranslationCancellation?.Cancel();
            _traditionalRequestKey = null;
            _traditionalResult = null;
            _traditionalDisplayedSegmentId = null;
            ClearTraditionalHighlights();
            SetTraditionalTexts("", "");
            return;
        }
        var snapshot = _traditionalService.CurrentSnapshot;
        if (snapshot is null)
        {
            _traditionalResult = null;
            _traditionalDisplayedSegmentId = null;
            ClearTraditionalHighlights();
            SetTraditionalTexts("Chưa nạp Data QuickTranslator.", "Chưa nạp Data QuickTranslator.");
            return;
        }
        string key = snapshot.Manifest.Fingerprint + ":" + segment.Id + ":" + segment.SourceText +
            ":" + TraditionalPrioritizeNamesInput.IsChecked + ":" + TraditionalAlgorithmInput.SelectedIndex;
        if (_traditionalDisplayedSegmentId == segment.Id && _traditionalResult?.SourceText == segment.SourceText &&
            _traditionalResult.DictionaryFingerprint == snapshot.Manifest.Fingerprint) return;
        if (_traditionalRequestKey == key) return;
        _traditionalTranslationCancellation?.Cancel();
        _traditionalTranslationCancellation?.Dispose();
        _traditionalTranslationCancellation = new();
        _traditionalRequestKey = key;
        _traditionalResult = null;
        _traditionalDisplayedSegmentId = null;
        ClearTraditionalHighlights();
        SetTraditionalTexts("Đang chuyển Hán Việt…", "Đang dịch VP đơn ý…");
        TranslateTraditionalSegmentAsync(key, segment.Id, segment.SourceText, _traditionalTranslationCancellation.Token);
    }

    private async void TranslateTraditionalSegmentAsync(string key, Guid segmentId, string source, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _traditionalService.TranslateAsync(source, new()
            {
                PrioritizeNames = TraditionalPrioritizeNamesInput.IsChecked == true,
                TranslationAlgorithm = Math.Clamp(TraditionalAlgorithmInput.SelectedIndex, 0, 2)
            }, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _traditionalRequestKey != key || SourceText.Text != source) return;
            _traditionalResult = result;
            _traditionalDisplayedSegmentId = segmentId;
            SetTraditionalTexts(result.HanVietText, result.VietPhraseText);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_traditionalRequestKey != key) return;
            _traditionalResult = null;
            _traditionalDisplayedSegmentId = null;
            SetTraditionalTexts("Không tạo được Hán Việt.", "Không tạo được VP đơn ý.");
            SetStatus("Lỗi dịch truyền thống; bản AI không bị ảnh hưởng: " + ex.Message);
        }
    }

    private void SetTraditionalTexts(string hanViet, string vietPhrase)
    {
        ClearTraditionalHighlights();
        _syncingTraditionalSelection = true;
        try { HanVietText.Text = hanViet; VietPhraseText.Text = vietPhrase; }
        finally { _syncingTraditionalSelection = false; }
        if (_initialized && _service?.GetSnapshot().Segments.SingleOrDefault(segment => segment.Id == _selectedId) is { } selected)
            RefreshTranslationInspector(selected);
    }

    private void OnTraditionalSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingTraditionalSelection || _rendering || sender is not TextBox input) return;
        if (input.SelectionLength <= 0)
        {
            ClearTraditionalHighlights();
            UpdateInspectorLiveSelection("");
            return;
        }
        TraditionalPane origin = ReferenceEquals(input, SourceText) ? TraditionalPane.Chinese :
            ReferenceEquals(input, HanVietText) ? TraditionalPane.HanViet : TraditionalPane.VietPhrase;
        if (origin == TraditionalPane.Chinese)
            UpdateInspectorLiveSelection(input.SelectedText);
        if (_traditionalResult is null || _traditionalDisplayedSegmentId != _selectedId ||
            !string.Equals(_traditionalResult.SourceText, SourceText.Text, StringComparison.Ordinal))
        {
            ClearTraditionalHighlights();
            WriteAlignmentDiagnostic(origin, new(input.SelectionStart, input.SelectionLength), null, "stale-or-unavailable");
            return;
        }
        SelectionProjection projected = SelectionAlignment.Project(_traditionalResult, origin,
            new(input.SelectionStart, input.SelectionLength));
        if (projected.Chinese.Length <= 0)
        {
            ClearTraditionalHighlights();
            WriteAlignmentDiagnostic(origin, new(input.SelectionStart, input.SelectionLength), projected, "no-source-mapping");
            return;
        }
        if (origin != TraditionalPane.Chinese &&
            projected.Chinese.Start >= 0 &&
            projected.Chinese.Start + projected.Chinese.Length <= SourceText.Text.Length)
        {
            UpdateInspectorLiveSelection(SourceText.Text.Substring(projected.Chinese.Start, projected.Chinese.Length));
        }
        ShowTraditionalHighlights(origin, projected);
        ScrollMappedTargetsIntoView(origin, projected);
        WriteAlignmentDiagnostic(origin, new(input.SelectionStart, input.SelectionLength), projected, "mapped");
    }

    private void InitializeTraditionalHighlightLayers()
    {
        _chineseHighlight ??= AddHighlightLayer(SourceText);
        _hanVietHighlight ??= AddHighlightLayer(HanVietText);
        _vietPhraseHighlight ??= AddHighlightLayer(VietPhraseText);
    }

    private static TextBoxRangeHighlightAdorner? AddHighlightLayer(TextBox textBox)
    {
        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(textBox);
        if (layer is null) return null;
        var adorner = new TextBoxRangeHighlightAdorner(textBox);
        layer.Add(adorner);
        return adorner;
    }

    private void ShowTraditionalHighlights(TraditionalPane origin, SelectionProjection projection)
    {
        _chineseHighlight?.SetRange(origin == TraditionalPane.Chinese ? null : projection.Chinese);
        _hanVietHighlight?.SetRange(origin == TraditionalPane.HanViet ? null : projection.HanViet);
        _vietPhraseHighlight?.SetRange(origin == TraditionalPane.VietPhrase ? null : projection.VietPhrase);
    }

    private void ClearTraditionalHighlights()
    {
        _chineseHighlight?.SetRange(null);
        _hanVietHighlight?.SetRange(null);
        _vietPhraseHighlight?.SetRange(null);
    }

    private void ScrollMappedTargetsIntoView(TraditionalPane origin, SelectionProjection projection)
    {
        if (origin != TraditionalPane.Chinese) EnsureRangeVisible(SourceText, projection.Chinese);
        if (origin != TraditionalPane.HanViet) EnsureRangeVisible(HanVietText, projection.HanViet);
        if (origin != TraditionalPane.VietPhrase) EnsureRangeVisible(VietPhraseText, projection.VietPhrase);
    }

    private static void EnsureRangeVisible(TextBox textBox, CoreTextRange range)
    {
        if (range.Length <= 0 || textBox.Visibility != Visibility.Visible || textBox.ActualHeight <= 0) return;
        int position = Math.Clamp(range.Start, 0, textBox.Text.Length);
        Rect rectangle = textBox.GetRectFromCharacterIndex(position, trailingEdge: false);
        if (!rectangle.IsEmpty && rectangle.Top >= 0 && rectangle.Bottom <= textBox.ActualHeight) return;
        int line = textBox.GetLineIndexFromCharacterIndex(position);
        if (line >= 0) textBox.ScrollToLine(line);
    }

    private async void WriteAlignmentDiagnostic(TraditionalPane origin, CoreTextRange selected,
        SelectionProjection? projection, string outcome)
    {
        if (TraditionalAlignmentDiagnosticsInput.IsChecked != true) return;
        try { await _alignmentDiagnostics.WriteAsync(_selectedId, _traditionalResult?.SourceVersion ?? "none", origin, selected, projection, outcome); }
        catch (Exception ex) { SetStatus("Không ghi được log alignment: " + ex.Message); }
    }

    private void OnHideTraditionalPane(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        var visible = Enum.GetValues<TraditionalPane>().Where(item => !_hiddenTraditionalPanes.Contains(item)).ToList();
        int visibleCount = visible.Count + (AiPane.Visibility == Visibility.Visible && _maximizedTraditionalPane is null ? 1 : 0);
        if (tag == "Ai")
        {
            if (visibleCount <= 1) { SetStatus("Phải giữ lại ít nhất một ô."); return; }
            AiPane.Visibility = Visibility.Collapsed;
        }
        else if (Enum.TryParse(tag, out TraditionalPane pane))
        {
            if (visibleCount <= 1) { SetStatus("Phải giữ lại ít nhất một ô."); return; }
            _hiddenTraditionalPanes.Add(pane);
        }
        else return;
        _maximizedTraditionalPane = null;
        ApplyTraditionalPaneLayout();
    }

    private void OnMaximizeTraditionalPane(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        _hiddenTraditionalPanes.Clear();
        _maximizedTraditionalPane = tag == "Ai" ? null : Enum.TryParse(tag, out TraditionalPane pane) ? pane : null;
        if (tag == "Ai")
        {
            foreach (var item in Enum.GetValues<TraditionalPane>()) _hiddenTraditionalPanes.Add(item);
            AiPane.Visibility = Visibility.Visible;
        }
        else
        {
            foreach (var item in Enum.GetValues<TraditionalPane>())
                if (item != _maximizedTraditionalPane) _hiddenTraditionalPanes.Add(item);
            AiPane.Visibility = Visibility.Collapsed;
        }
        ApplyTraditionalPaneLayout();
    }

    private void OnRestoreTraditionalPanes(object sender, RoutedEventArgs e)
    {
        _hiddenTraditionalPanes.Clear();
        _maximizedTraditionalPane = null;
        AiPane.Visibility = Visibility.Visible;
        ApplyTraditionalPaneLayout();
    }

    private void ApplyTraditionalPaneLayout()
    {
        if (ChinesePane is null) return;
        bool zhVisible = !_hiddenTraditionalPanes.Contains(TraditionalPane.Chinese);
        bool hvVisible = !_hiddenTraditionalPanes.Contains(TraditionalPane.HanViet);
        bool vpVisible = !_hiddenTraditionalPanes.Contains(TraditionalPane.VietPhrase);
        bool aiVisible = AiPane.Visibility == Visibility.Visible;

        ChinesePane.Visibility = zhVisible ? Visibility.Visible : Visibility.Collapsed;
        HanVietPane.Visibility = hvVisible ? Visibility.Visible : Visibility.Collapsed;
        VietPhrasePane.Visibility = vpVisible ? Visibility.Visible : Visibility.Collapsed;

        bool left = zhVisible || vpVisible;
        bool right = hvVisible || aiVisible;
        bool top = zhVisible || hvVisible;
        bool bottom = vpVisible || aiVisible;

        // BL-09: Dynamic Column / ColumnSpan & Row / RowSpan expansion when 1 pane in a row/column is hidden.
        Grid.SetColumn(ChinesePane, 0);
        Grid.SetColumnSpan(ChinesePane, zhVisible && !hvVisible && right ? 3 : 1);
        Grid.SetRow(ChinesePane, 0);
        Grid.SetRowSpan(ChinesePane, zhVisible && !vpVisible && bottom && (!hvVisible || aiVisible) ? 1 : 1);

        Grid.SetColumn(HanVietPane, hvVisible && !zhVisible && left ? 0 : 2);
        Grid.SetColumnSpan(HanVietPane, hvVisible && !zhVisible && left ? 3 : 1);
        Grid.SetRow(HanVietPane, 0);

        Grid.SetColumn(VietPhrasePane, 0);
        Grid.SetColumnSpan(VietPhrasePane, vpVisible && !aiVisible && right ? 3 : 1);
        Grid.SetRow(VietPhrasePane, 2);

        Grid.SetColumn(AiPane, aiVisible && !vpVisible && left ? 0 : 2);
        Grid.SetColumnSpan(AiPane, aiVisible && !vpVisible && left ? 3 : 1);
        Grid.SetRow(AiPane, 2);

        TraditionalLeftColumn.Width = left ? new(_workspaceLayout.FourPaneLeftStar, GridUnitType.Star) : new(0);
        TraditionalRightColumn.Width = right ? new(1, GridUnitType.Star) : new(0);
        TraditionalVerticalSplitterColumn.Width = left && right && ((zhVisible && hvVisible) || (vpVisible && aiVisible)) ? new(6) : new(0);
        TraditionalTopRow.Height = top ? new(_workspaceLayout.FourPaneTopStar, GridUnitType.Star) : new(0);
        TraditionalBottomRow.Height = bottom ? new(1.35, GridUnitType.Star) : new(0);
        TraditionalHorizontalSplitterRow.Height = top && bottom ? new(6) : new(0);
        _chineseHighlight?.InvalidateVisual();
        _hanVietHighlight?.InvalidateVisual();
        _vietPhraseHighlight?.InvalidateVisual();
    }
}
