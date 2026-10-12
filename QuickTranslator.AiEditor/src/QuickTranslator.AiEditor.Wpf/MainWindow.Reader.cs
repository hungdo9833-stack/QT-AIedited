using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Reading;
using QuickTranslator.AiEditor.Core.TextCleaning;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private ReaderPreferencesStore _readerStore = new(ReaderPreferencesStore.DefaultPath);
    private readonly DispatcherTimer _readerSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private ReaderPreferences _readerPreferences = new();
    private ReaderDocument? _readerDocument;
    private readonly List<ReaderRunRange> _readerRuns = [];
    private bool _readerReady, _applyingReaderAppearance, _restoringReaderScroll, _focusMode;
    private Guid? _restoredReaderChapterId;
    private string _renderedReaderText = "";
    private int _readerSelectionOffset;
    private WindowStyle _savedWindowStyle;
    private WindowState _savedWindowState;

    private async void InitializeReader()
    {
        ReaderFontInput.ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(n => n).ToArray();
        _readerSaveTimer.Tick += OnReaderPreferencesSave;
        var store = _readerStore;
        try
        {
            var preferences = await store.LoadAsync();
            if (!ReferenceEquals(store, _readerStore)) return;
            _readerPreferences = preferences;
            if (_readerStore.RecoveredFromBackup) SetStatus("Đã phục hồi thiết lập Reader từ bản dự phòng.");
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(store, _readerStore)) return;
            ShowError(ex); _readerPreferences = new();
        }
        _readerReady = true;
        LoadReaderAppearanceControls();
        ApplyReaderAppearance();
        RefreshReader(_service?.GetSnapshot());
    }

    private void RefreshReader(ChapterDocument? chapter)
    {
        if (!_readerReady) return;
        ReaderTitle.Text = chapter?.Title ?? TitleInput.Text;
        if (chapter is null)
        {
            _readerDocument = null;
            ReaderWarning.Text = "Hãy nhập và chia chương trong chế độ Biên tập để bắt đầu đọc.";
            if (_renderedReaderText.Length > 0) BuildReaderFlowDocument("");
            return;
        }

        var next = ReaderDocumentComposer.Compose(chapter);
        if (!string.Equals(next.Text, _renderedReaderText, StringComparison.Ordinal))
        {
            double offset = ReaderScroll.VerticalOffset;
            _readerDocument = next;
            BuildReaderFlowDocument(next.Text);
            Dispatcher.BeginInvoke(() =>
            {
                _restoringReaderScroll = true;
                ReaderScroll.ScrollToVerticalOffset(offset);
                _restoringReaderScroll = false;
                RestoreReaderPosition(chapter.Id);
            }, DispatcherPriority.Loaded);
        }
        else _readerDocument = next;

        var notes = new List<string>();
        if (next.IncompleteSegmentIds.Count > 0) notes.Add($"{next.IncompleteSegmentIds.Count} đoạn chưa hoàn tất");
        if (next.ErrorSegmentIds.Count > 0) notes.Add($"{next.ErrorSegmentIds.Count} đoạn lỗi");
        if (next.InspectionSegmentIds.Count > 0) notes.Add($"{next.InspectionSegmentIds.Count} đoạn cần kiểm tra");
        var ignoredChinese = chapter.TextCleaner.IgnoredChineseText.ToHashSet(StringComparer.Ordinal);
        int chineseLeft = chapter.Segments.Sum(segment => ChineseTextScanner.FindClusters(segment.EditableText)
            .Count(cluster => !ignoredChinese.Contains(cluster.Text)));
        if (chineseLeft > 0) notes.Add($"{chineseLeft} cụm chữ Hán còn sót");
        ReaderWarning.Text = notes.Count == 0 ? "" : "Bản đọc chưa hoàn chỉnh: " + string.Join(" · ", notes) + ".";
        ReaderChineseIssuesButton.Visibility = chineseLeft > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildReaderFlowDocument(string text)
    {
        _renderedReaderText = text;
        _readerRuns.Clear();
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        foreach (var display in ReaderDisplayLayout.Create(text, _readerPreferences.CollapseExtraBlankLinesForDisplay))
        {
            var paragraph = CreateReaderParagraph();
            AddReaderRuns(paragraph, display.Start, display.Length);
            document.Blocks.Add(paragraph);
        }
        ReaderText.Document = document;
        ApplyReaderAppearance();
    }

    private Paragraph CreateReaderParagraph() => new()
    {
        Margin = new Thickness(0, 0, 0, _readerPreferences.ParagraphSpacing),
        TextIndent = _readerPreferences.FirstLineIndent,
        TextAlignment = _readerPreferences.TextAlignment == ReaderTextAlignment.Justify ? TextAlignment.Justify : TextAlignment.Left
    };

    private void AddReaderRuns(Paragraph paragraph, int start, int length)
    {
        int end = start + length;
        if (_readerDocument is null || length == 0) return;
        foreach (var span in _readerDocument.Spans.Where(s => s.Start < end && s.Start + s.Length > start))
        {
            int runStart = Math.Max(start, span.Start);
            int runEnd = Math.Min(end, span.Start + span.Length);
            var run = new Run(_readerDocument.Text[runStart..runEnd]) { Tag = span.SegmentId };
            paragraph.Inlines.Add(run);
            _readerRuns.Add(new(runStart, runEnd - runStart, run));
        }
    }

    private void OnReaderModeChanged(object sender, RoutedEventArgs e)
    {
        if (ReaderView is null || EditorView is null) return;
        bool reader = ReaderModeButton.IsChecked == true;
        ReaderView.Visibility = reader ? Visibility.Visible : Visibility.Collapsed;
        EditorView.Visibility = reader ? Visibility.Collapsed : Visibility.Visible;
        ApplyReaderChrome();
        if (reader) RefreshReader(_service?.GetSnapshot());
    }

    private void OnToggleReaderAppearance(object sender, RoutedEventArgs e) => ReaderAppearancePopup.IsOpen = !ReaderAppearancePopup.IsOpen;

    private void LoadReaderAppearanceControls()
    {
        _applyingReaderAppearance = true;
        try
        {
            SelectTaggedItem(ReaderThemeInput, _readerPreferences.Theme.ToString());
            SelectTaggedItem(ReaderDensityInput, _readerPreferences.Density.ToString());
            SelectTaggedItem(ReaderWidthInput, _readerPreferences.PageWidth.ToString());
            ReaderFontInput.Text = _readerPreferences.FontFamily;
            ReaderFontSizeInput.Value = _readerPreferences.FontSize;
            ReaderLineHeightInput.Value = _readerPreferences.LineHeight;
            ReaderParagraphSpacingInput.Value = _readerPreferences.ParagraphSpacing;
            ReaderPageMarginInput.Value = _readerPreferences.PageMargin;
            ReaderCollapseBlankLinesInput.IsChecked = _readerPreferences.CollapseExtraBlankLinesForDisplay;
            ReaderJustifyInput.IsChecked = _readerPreferences.TextAlignment == ReaderTextAlignment.Justify;
            ReaderIndentInput.IsChecked = _readerPreferences.FirstLineIndent > 0;
        }
        finally { _applyingReaderAppearance = false; }
    }

    private static void SelectTaggedItem(ComboBox combo, string tag) => combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, tag));

    private void OnReaderAppearanceChanged(object sender, RoutedEventArgs e)
    {
        if (!_readerReady || _applyingReaderAppearance) return;
        ReadReaderAppearanceControls();
        ApplyReaderAppearance();
        QueueReaderPreferencesSave();
    }

    private void OnReaderDensityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_readerReady || _applyingReaderAppearance || (ReaderDensityInput.SelectedItem as ComboBoxItem)?.Tag is not string value ||
            !Enum.TryParse(value, out ReaderDensity density)) return;
        _readerPreferences.Density = density;
        var preset = ReaderDensitySettings.For(density);
        _applyingReaderAppearance = true;
        try
        {
            ReaderLineHeightInput.Value = preset.LineHeight;
            ReaderParagraphSpacingInput.Value = preset.ParagraphSpacing;
            ReaderPageMarginInput.Value = preset.PageMargin;
        }
        finally { _applyingReaderAppearance = false; }
        ReadReaderAppearanceControls();
        ApplyReaderAppearance();
        QueueReaderPreferencesSave();
    }

    private void OnReaderBlankLinesChanged(object sender, RoutedEventArgs e)
    {
        if (!_readerReady || _applyingReaderAppearance) return;
        _readerPreferences.CollapseExtraBlankLinesForDisplay = ReaderCollapseBlankLinesInput.IsChecked == true;
        double offset = ReaderScroll.VerticalOffset;
        BuildReaderFlowDocument(_readerDocument?.Text ?? "");
        Dispatcher.BeginInvoke(() => ReaderScroll.ScrollToVerticalOffset(offset), DispatcherPriority.Loaded);
        QueueReaderPreferencesSave();
    }

    private void ReadReaderAppearanceControls()
    {
        if ((ReaderThemeInput.SelectedItem as ComboBoxItem)?.Tag is string theme && Enum.TryParse(theme, out ReaderTheme parsedTheme)) _readerPreferences.Theme = parsedTheme;
        if ((ReaderWidthInput.SelectedItem as ComboBoxItem)?.Tag is string width && Enum.TryParse(width, out ReaderPageWidth parsedWidth)) _readerPreferences.PageWidth = parsedWidth;
        string font = ReaderFontInput.Text.Trim();
        if (!string.IsNullOrWhiteSpace(font)) _readerPreferences.FontFamily = font;
        _readerPreferences.FontSize = ReaderFontSizeInput.Value;
        _readerPreferences.LineHeight = ReaderLineHeightInput.Value;
        _readerPreferences.ParagraphSpacing = ReaderParagraphSpacingInput.Value;
        _readerPreferences.PageMargin = ReaderPageMarginInput.Value;
        _readerPreferences.TextAlignment = ReaderJustifyInput.IsChecked == true ? ReaderTextAlignment.Justify : ReaderTextAlignment.Left;
        _readerPreferences.FirstLineIndent = ReaderIndentInput.IsChecked == true ? 24 : 0;
    }

    private void ApplyReaderAppearance()
    {
        if (ReaderText is null) return;
        var palette = ReaderThemePalette.For(_readerPreferences.Theme);
        Color background = (Color)ColorConverter.ConvertFromString(palette.PageBackground);
        Color foreground = (Color)ColorConverter.ConvertFromString(palette.TextForeground);
        ReaderBackground.Background = new SolidColorBrush(background);
        ReaderText.Foreground = ReaderTitle.Foreground = new SolidColorBrush(foreground);
        try { ReaderText.FontFamily = new FontFamily(_readerPreferences.FontFamily); } catch { ReaderText.FontFamily = new FontFamily("Georgia"); }
        ReaderText.FontSize = _readerPreferences.FontSize;
        ReaderText.Document.LineHeight = _readerPreferences.FontSize * _readerPreferences.LineHeight;
        ReaderPage.MaxWidth = _readerPreferences.PageWidth switch { ReaderPageWidth.Narrow => 620, ReaderPageWidth.Wide => 980, _ => 760 };
        ReaderPage.Padding = new Thickness(_readerPreferences.PageMargin);
        ReaderTitle.Margin = new Thickness(0, 0, 0, Math.Max(12, _readerPreferences.ParagraphSpacing + 12));
        foreach (var paragraph in ReaderText.Document.Blocks.OfType<Paragraph>())
        {
            paragraph.Margin = new Thickness(0, 0, 0, _readerPreferences.ParagraphSpacing);
            paragraph.TextIndent = _readerPreferences.FirstLineIndent;
            paragraph.TextAlignment = _readerPreferences.TextAlignment == ReaderTextAlignment.Justify ? TextAlignment.Justify : TextAlignment.Left;
        }
        ApplyReaderChrome();
    }

    private void ApplyReaderChrome()
    {
        if (ChapterWorkspace is null) return;
        bool active = ReaderModeButton.IsChecked == true;
        bool dark = active && _readerPreferences.Theme == ReaderTheme.Dark;
        var palette = ReaderThemePalette.For(dark ? ReaderTheme.Dark : ReaderTheme.Light);
        var background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.ChromeBackground));
        var foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.TextForeground));
        ChapterWorkspace.Background = background;
        ChapterModeBar.Background = ReaderToolbar.Background = background;
        ChapterModeHint.Foreground = foreground;
        ReaderSearchPanel.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? palette.ControlBackground : "#F7F9FC"));
        ReaderAppearancePanel.Background = background;
        ReaderAppearancePanel.SetValue(TextElement.ForegroundProperty, foreground);
        ReaderSearchInput.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.ControlBackground));
        ReaderSearchInput.Foreground = foreground;
        ReaderWarning.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Warning));
        ReaderChineseIssuesButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.ControlBackground));
        ReaderChineseIssuesButton.Foreground = foreground;
        ReaderChineseIssuesButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Border));
        ReaderProgressText.Foreground = foreground;
        foreach (var button in FindVisualChildren<Button>(ReaderToolbar))
        {
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.ControlBackground));
            button.Foreground = foreground;
            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Border));
        }
        foreach (var button in FindVisualChildren<Button>(ReaderSearchPanel))
        {
            button.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.ControlBackground));
            button.Foreground = foreground;
            button.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Border));
        }
        foreach (var combo in FindVisualChildren<ComboBox>(ReaderAppearancePanel))
        {
            combo.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.ControlBackground));
            combo.Foreground = foreground;
        }
        ReaderModeButton.Foreground = EditorModeButton.Foreground = foreground;
        ReaderModeButton.Background = EditorModeButton.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.ControlBackground));
        ReaderModeButton.BorderBrush = EditorModeButton.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette.Border));
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private void OnReaderSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_readerReady) return;
        if (FindRun(ReaderText.Selection.Start) is { } range)
            _readerSelectionOffset = Math.Clamp(range.Start + range.Run.ContentStart.GetOffsetToPosition(ReaderText.Selection.Start), range.Start, range.Start + range.Length);
    }

    private ReaderRunRange? FindRun(TextPointer pointer)
    {
        DependencyObject? parent = pointer.Parent;
        while (parent is not null && parent is not Run) parent = LogicalTreeHelper.GetParent(parent);
        return parent is Run run ? _readerRuns.FirstOrDefault(r => ReferenceEquals(r.Run, run)) : null;
    }

    private void OnReaderTextDoubleClick(object sender, MouseButtonEventArgs e) => OpenReaderOffsetInEditor(_readerSelectionOffset);
    private void OnOpenReaderSelectionInEditor(object sender, RoutedEventArgs e) => OpenReaderOffsetInEditor(_readerSelectionOffset);
    private void OpenReaderOffsetInEditor(int offset)
    {
        Guid? id = _readerDocument?.FindSegmentAt(offset);
        if (id is null) return;
        _selectedId = id; _shownAttemptId = null;
        EditorTabs.SelectedIndex = 1;
        Refresh();
        DraftText.Focus();
    }

    private void OnOpenReaderSearch(object sender, RoutedEventArgs e) { ReaderSearchPanel.Visibility = Visibility.Visible; ReaderSearchInput.Focus(); ReaderSearchInput.SelectAll(); }
    private void OnCloseReaderSearch(object sender, RoutedEventArgs e) { ReaderSearchPanel.Visibility = Visibility.Collapsed; ReaderText.Focus(); }
    private void OnReaderSearchKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { OnFindNext(sender, e); e.Handled = true; } }
    private void OnFindNext(object sender, RoutedEventArgs e)
    {
        if (_readerDocument is null || string.IsNullOrEmpty(ReaderSearchInput.Text)) return;
        int start = Math.Min(_readerSelectionOffset + 1, _readerDocument.Text.Length);
        int found = _readerDocument.Text.IndexOf(ReaderSearchInput.Text, start, StringComparison.CurrentCultureIgnoreCase);
        if (found < 0) found = _readerDocument.Text.IndexOf(ReaderSearchInput.Text, StringComparison.CurrentCultureIgnoreCase);
        if (found < 0) { SetStatus("Không tìm thấy trong bản dịch chương."); return; }
        SelectReaderRange(found, ReaderSearchInput.Text.Length);
        _readerSelectionOffset = found;
        SetStatus($"Đã tìm thấy tại ký tự {found + 1}.");
    }

    private void SelectReaderRange(int start, int length)
    {
        var startRun = _readerRuns.FirstOrDefault(r => start >= r.Start && start < r.Start + r.Length);
        var endRun = _readerRuns.LastOrDefault(r => start + length > r.Start && start + length <= r.Start + r.Length);
        if (startRun is null || endRun is null) { OpenReaderOffsetInEditor(start); return; }
        var from = startRun.Run.ContentStart.GetPositionAtOffset(start - startRun.Start);
        var to = endRun.Run.ContentStart.GetPositionAtOffset(start + length - endRun.Start);
        if (from is null || to is null) return;
        ReaderText.Selection.Select(from, to);
        ReaderText.Focus();
        from.Paragraph?.BringIntoView();
    }

    private void OnToggleBookmark(object sender, RoutedEventArgs e)
    {
        if (_service?.GetSnapshot() is not { } chapter) return;
        var position = GetReaderPosition(chapter.Id);
        position.BookmarkOffset = position.BookmarkOffset == _readerSelectionOffset ? null : _readerSelectionOffset;
        QueueReaderPreferencesSave();
        SetStatus(position.BookmarkOffset is null ? "Đã bỏ đánh dấu." : "Đã đánh dấu vị trí đọc trên máy này.");
    }

    private void OnReaderScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (!_readerReady || _restoringReaderScroll) return;
        double percent = ReaderScroll.ScrollableHeight <= 0 ? 100 : 100 * ReaderScroll.VerticalOffset / ReaderScroll.ScrollableHeight;
        ReaderProgressText.Text = $"{percent:0}%";
        if (_service?.GetSnapshot() is { } chapter)
        {
            var position = GetReaderPosition(chapter.Id);
            position.ScrollOffset = ReaderScroll.VerticalOffset;
            position.CharacterOffset = _readerSelectionOffset;
            position.SegmentId = _readerDocument?.FindSegmentAt(_readerSelectionOffset);
            position.UpdatedAt = DateTimeOffset.UtcNow;
            QueueReaderPreferencesSave();
        }
    }

    private ReaderChapterPosition GetReaderPosition(Guid chapterId)
    {
        if (!_readerPreferences.ChapterPositions.TryGetValue(chapterId, out var position))
            _readerPreferences.ChapterPositions[chapterId] = position = new();
        return position;
    }

    private void RestoreReaderPosition(Guid chapterId)
    {
        if (_restoredReaderChapterId == chapterId || !_readerPreferences.ChapterPositions.TryGetValue(chapterId, out var position)) return;
        _restoredReaderChapterId = chapterId;
        _readerSelectionOffset = Math.Min(position.CharacterOffset, _readerDocument?.Text.Length ?? 0);
        ReaderScroll.ScrollToVerticalOffset(position.ScrollOffset);
    }

    private void QueueReaderPreferencesSave() { _readerSaveTimer.Stop(); _readerSaveTimer.Start(); }
    private async Task SaveReaderPreferencesAsync()
    {
        if (!_readerReady) return;
        await _readerStore.SaveAsync(_readerPreferences);
    }

    private async void OnReaderPreferencesSave(object? sender, EventArgs e)
    {
        _readerSaveTimer.Stop();
        if (!BeginUiOperation(allowDuringTranslation: true))
        {
            if (!_closing) _readerSaveTimer.Start();
            return;
        }
        try { await SaveReaderPreferencesAsync(); }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void OnToggleFocusMode(object sender, RoutedEventArgs e)
    {
        _focusMode = !_focusMode;
        GlobalToolbar.Visibility = GlobalStatus.Visibility = _focusMode ? Visibility.Collapsed : Visibility.Visible;
        ReaderToolbar.Visibility = _focusMode ? Visibility.Collapsed : Visibility.Visible;
        FocusModeButton.Content = _focusMode ? "Thoát tập trung" : "Tập trung";
    }

    private void OnToggleFullScreen(object sender, RoutedEventArgs e)
    {
        if (WindowStyle != WindowStyle.None)
        {
            _savedWindowStyle = WindowStyle; _savedWindowState = WindowState;
            WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized;
        }
        else { WindowStyle = _savedWindowStyle; WindowState = _savedWindowState; }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { OnToggleFullScreen(sender, e); e.Handled = true; return; }
        if (e.Key == Key.Escape && _focusMode) { OnToggleFocusMode(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H)
        {
            OnOpenBatchReplace(sender, e); e.Handled = true; return;
        }
        if (EditorTabs.SelectedIndex != 0 || ReaderModeButton.IsChecked != true) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { OnOpenReaderSearch(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key is Key.Add or Key.OemPlus)) { ReaderFontSizeInput.Value = Math.Min(30, ReaderFontSizeInput.Value + 1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (e.Key is Key.Subtract or Key.OemMinus)) { ReaderFontSizeInput.Value = Math.Max(14, ReaderFontSizeInput.Value - 1); e.Handled = true; }
        else if (!ReaderSearchInput.IsKeyboardFocusWithin && e.Key is Key.PageDown or Key.Space) { ReaderScroll.PageDown(); e.Handled = true; }
        else if (!ReaderSearchInput.IsKeyboardFocusWithin && e.Key == Key.PageUp) { ReaderScroll.PageUp(); e.Handled = true; }
    }

    private sealed record ReaderRunRange(int Start, int Length, Run Run);
}
