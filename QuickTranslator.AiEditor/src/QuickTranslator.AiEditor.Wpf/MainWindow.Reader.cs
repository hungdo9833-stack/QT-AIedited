using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Reading;
using QuickTranslator.AiEditor.Core.TextCleaning;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private ReaderPreferencesStore _readerStore = new(ReaderPreferencesStore.DefaultPath);
    private readonly DispatcherTimer _readerSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(800) };
    private ReaderPreferences _readerPreferences = new();
    private Exception? _readerLoadFailure;
    private ReaderDocument? _readerDocument;
    private readonly List<ReaderRunRange> _readerRuns = [];
    private bool _readerReady, _applyingReaderAppearance, _restoringReaderScroll, _focusMode, _syncingReaderCatalog;
    private Guid? _restoredReaderChapterId;
    private Guid? _renderedReaderChapterId;
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
            if (!ReferenceEquals(store, _readerStore)) return;
            _readerLoadFailure = null;
            _readerPreferences = preferences;
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(store, _readerStore)) return;
            _readerLoadFailure = ex; _readerReady = false; ShowError(ex);
            return; // A display default must never become a writable replacement for unread data.
        }
        _readerReady = true;
        LoadReaderAppearanceControls();
        ApplyReaderAppearance();
        ApplyReaderCatalogVisibility();
        RefreshReader(_service?.GetSnapshot());
    }

    private void RefreshReader(ChapterDocument? chapter)
    {
        if (!_readerReady) return;
        ReaderTitle.Text = chapter?.Title ?? TitleInput.Text;
        if (chapter is null)
        {
            _readerDocument = null;
            _renderedReaderChapterId = null;
            ReaderWarning.Text = "Hãy nhập và chia chương trong chế độ Biên tập để bắt đầu đọc.";
            if (_renderedReaderText.Length > 0) BuildReaderFlowDocument("");
            return;
        }

        var next = ReaderDocumentComposer.Compose(chapter);
        if (_renderedReaderChapterId != chapter.Id ||
            !string.Equals(next.Text, _renderedReaderText, StringComparison.Ordinal))
        {
            double offset = ReaderScroll.VerticalOffset;
            _readerDocument = next;
            _renderedReaderChapterId = chapter.Id;
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
        TwoPaneModeButton.Visibility = FourPaneModeButton.Visibility = reader
            ? Visibility.Collapsed : Visibility.Visible;
        ApplyProjectSidebarVisibility();
        ApplyReaderChrome();
        if (reader)
        {
            RefreshReaderCatalog();
            RefreshReader(_service?.GetSnapshot());
        }
    }

    private void RefreshReaderCatalog()
    {
        if (ReaderChapterList is null || ReaderChapterSearchInput is null) return;
        _syncingReaderCatalog = true;
        try
        {
            if (_projectSession is null)
            {
                ReaderChapterList.ItemsSource = null;
                ReaderChapterCountText.Text = "0 chương";
                return;
            }
            var chapters = _projectCatalog.Query(_projectSession, new(
                Search: ReaderChapterSearchInput.Text, Sort: ProjectCatalogSort.ReadingOrder));
            ReaderChapterItem[] items = chapters.Select(chapter => new ReaderChapterItem(chapter.ChapterId,
                $"{chapter.Number:0000} · {chapter.VietnameseTitle}", ProjectStateLabel(chapter.TranslationState))).ToArray();
            ReaderChapterList.ItemsSource = items;
            ReaderChapterList.SelectedItem = items.FirstOrDefault(item =>
                item.ChapterId == _projectSession.Manifest.CurrentChapterId);
            ReaderChapterCountText.Text = $"{items.Length:N0} chương";
        }
        finally { _syncingReaderCatalog = false; }
    }

    private void OnReaderChapterSearchChanged(object sender, RoutedEventArgs e)
    {
        if (_initialized) RefreshReaderCatalog();
    }

    private async void OnReaderChapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingReaderCatalog || ReaderChapterList.SelectedItem is not ReaderChapterItem item ||
            item.ChapterId == _service?.GetSnapshot().Id) return;
        try { await LoadProjectChapterAsync(item.ChapterId); }
        catch (Exception ex) { ShowError(ex); }
        finally { RefreshReaderCatalog(); }
    }

    private async void OnReaderPreviousChapter(object sender, RoutedEventArgs e)
    {
        try { await NavigateReaderChapterAsync(-1); } catch (Exception ex) { ShowError(ex); }
    }

    private async void OnReaderNextChapter(object sender, RoutedEventArgs e)
    {
        try { await NavigateReaderChapterAsync(1); } catch (Exception ex) { ShowError(ex); }
    }

    private async Task NavigateReaderChapterAsync(int delta)
    {
        if (_projectSession is null || _service is null) return;
        CaptureReaderPosition(_service.GetSnapshot().Id);
        ProjectCatalogChapter[] chapters = _projectCatalog.Query(_projectSession,
            new(Sort: ProjectCatalogSort.ReadingOrder)).ToArray();
        int current = Array.FindIndex(chapters, chapter => chapter.ChapterId == _service.GetSnapshot().Id);
        int target = current + delta;
        if (current < 0 || target < 0 || target >= chapters.Length)
        {
            SetStatus(delta < 0 ? "Đang ở chương đầu tiên." : "Đang ở chương cuối cùng.");
            return;
        }
        await LoadProjectChapterAsync(chapters[target].ChapterId);
        RefreshReaderCatalog();
    }

    private void OnToggleReaderCatalog(object sender, RoutedEventArgs e)
    {
        _readerPreferences.TableOfContentsVisible = !_readerPreferences.TableOfContentsVisible;
        ApplyReaderCatalogVisibility();
        QueueReaderPreferencesSave();
    }

    private void ApplyReaderCatalogVisibility()
    {
        if (ReaderCatalogPanel is null) return;
        bool visible = _readerPreferences.TableOfContentsVisible && !_focusMode;
        ReaderCatalogPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ReaderCatalogSplitter.Visibility = ReaderCatalogPanel.Visibility;
        ReaderCatalogColumn.Width = visible ? new GridLength(_compactShell ? 235 : 270) : new GridLength(0);
        ReaderCatalogSplitterColumn.Width = visible ? new GridLength(5) : new GridLength(0);
        ReaderCatalogToggleButton.Content = visible ? "Ẩn mục lục" : "Hiện mục lục";
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
        ApplyReaderPalette(palette);
        ReaderBackground.SetResourceReference(Border.BackgroundProperty, "ReaderPageBrush");
        ReaderText.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
        ReaderTitle.SetResourceReference(TextElement.ForegroundProperty, "ReaderTextBrush");
        try { ReaderText.FontFamily = new FontFamily(_readerPreferences.FontFamily); }
        catch (ArgumentException ex)
        {
            ReaderText.FontFamily = new FontFamily("Georgia");
            SetStatus("Không nạp được font Reader đã lưu; đang dùng Georgia: " + ex.Message);
        }
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

    private static void ApplyReaderPalette(ReaderThemePalette palette)
    {
        ResourceDictionary resources = Application.Current.Resources;
        Color page = ParseReaderColor(palette.PageBackground);
        Color text = ParseReaderColor(palette.TextForeground);
        resources["ReaderPageBrush"] = new SolidColorBrush(page);
        resources["ReaderTextBrush"] = new SolidColorBrush(text);
        // ReaderThemePalette has no muted role. Blend 80% text over the page so the
        // secondary text remains distinct while retaining WCAG AA contrast in every palette.
        resources["ReaderMutedBrush"] = new SolidColorBrush(Color.FromRgb(
            BlendReaderChannel(text.R, page.R), BlendReaderChannel(text.G, page.G), BlendReaderChannel(text.B, page.B)));
        resources["ReaderAccentBrush"] = new SolidColorBrush(ParseReaderColor(palette.Warning));
        resources["ReaderPanelBrush"] = new SolidColorBrush(ParseReaderColor(palette.ControlBackground));
        resources["ReaderBorderBrush"] = new SolidColorBrush(ParseReaderColor(palette.Border));
    }

    private static Color ParseReaderColor(string value) => (Color)ColorConverter.ConvertFromString(value);

    private static byte BlendReaderChannel(byte text, byte page) =>
        (byte)Math.Round((text * 0.8) + (page * 0.2), MidpointRounding.AwayFromZero);

    private void ApplyReaderChrome()
    {
        if (ChapterWorkspace is null) return;
        bool active = ReaderModeButton.IsChecked == true;
        if (!active)
        {
            ChapterWorkspace.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "AppBackgroundBrush");
            ChapterModeBar.SetResourceReference(System.Windows.Controls.Panel.BackgroundProperty, "AppBackgroundBrush");
            ChapterModeHint.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "MutedBrush");
            return;
        }
        ChapterWorkspace.SetResourceReference(Panel.BackgroundProperty, "ReaderPageBrush");
        ChapterModeBar.SetResourceReference(Panel.BackgroundProperty, "ReaderPageBrush");
        ReaderToolbar.SetResourceReference(Panel.BackgroundProperty, "ReaderPageBrush");
        ChapterModeHint.SetResourceReference(TextElement.ForegroundProperty, "ReaderTextBrush");
        ReaderSearchPanel.SetResourceReference(Border.BackgroundProperty, "ReaderPanelBrush");
        ReaderAppearancePanel.SetResourceReference(Border.BackgroundProperty, "ReaderPanelBrush");
        ReaderAppearancePanel.SetResourceReference(TextElement.ForegroundProperty, "ReaderTextBrush");
        ReaderSearchInput.SetResourceReference(Control.BackgroundProperty, "ReaderPanelBrush");
        ReaderSearchInput.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
        ReaderWarning.SetResourceReference(TextElement.ForegroundProperty, "ReaderAccentBrush");
        ReaderChineseIssuesButton.SetResourceReference(Control.BackgroundProperty, "ReaderPanelBrush");
        ReaderChineseIssuesButton.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
        ReaderChineseIssuesButton.SetResourceReference(Control.BorderBrushProperty, "ReaderBorderBrush");
        ReaderProgressText.SetResourceReference(TextElement.ForegroundProperty, "ReaderMutedBrush");
        foreach (var button in FindVisualChildren<Button>(ReaderToolbar))
        {
            button.SetResourceReference(Control.BackgroundProperty, "ReaderPanelBrush");
            button.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
            button.SetResourceReference(Control.BorderBrushProperty, "ReaderBorderBrush");
        }
        foreach (var button in FindVisualChildren<Button>(ReaderSearchPanel))
        {
            button.SetResourceReference(Control.BackgroundProperty, "ReaderPanelBrush");
            button.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
            button.SetResourceReference(Control.BorderBrushProperty, "ReaderBorderBrush");
        }
        foreach (var combo in FindVisualChildren<ComboBox>(ReaderAppearancePanel))
        {
            combo.SetResourceReference(Control.BackgroundProperty, "ReaderPanelBrush");
            combo.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
        }
        ReaderModeButton.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
        EditorModeButton.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
        ReaderModeButton.SetResourceReference(Control.BackgroundProperty, "ReaderPanelBrush");
        EditorModeButton.SetResourceReference(Control.BackgroundProperty, "ReaderPanelBrush");
        ReaderModeButton.SetResourceReference(Control.BorderBrushProperty, "ReaderBorderBrush");
        EditorModeButton.SetResourceReference(Control.BorderBrushProperty, "ReaderBorderBrush");
        ReaderCatalogPanel.SetResourceReference(Border.BackgroundProperty, "ReaderPanelBrush");
        ReaderCatalogPanel.SetResourceReference(Border.BorderBrushProperty, "ReaderBorderBrush");
        ReaderChapterSearchInput.SetResourceReference(Control.BackgroundProperty, "ReaderPanelBrush");
        ReaderChapterSearchInput.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
        ReaderChapterList.SetResourceReference(Control.BackgroundProperty, "ReaderPanelBrush");
        ReaderChapterList.SetResourceReference(Control.ForegroundProperty, "ReaderTextBrush");
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
        if (!_readerReady || _restoringReaderScroll) return;
        if (FindRun(ReaderText.Selection.Start) is { } range)
        {
            _readerSelectionOffset = Math.Clamp(range.Start + range.Run.ContentStart.GetOffsetToPosition(ReaderText.Selection.Start), range.Start, range.Start + range.Length);
            if (_service?.GetSnapshot() is { } chapter)
            {
                CaptureReaderPosition(chapter.Id);
                QueueReaderPreferencesSave();
            }
        }
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
        _service?.SelectSegment(id.Value);
        ShowTranslationWorkspace(_workspaceLayout.FourPaneMode);
        SetStatus("Đã mở đúng đoạn đang đọc trong Editor.");
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
            CaptureReaderPosition(chapter.Id);
            QueueReaderPreferencesSave();
        }
    }

    private void CaptureReaderPosition(Guid chapterId)
    {
        var position = GetReaderPosition(chapterId);
        position.ScrollOffset = ReaderScroll.VerticalOffset;
        position.CharacterOffset = _readerSelectionOffset;
        ReaderSegmentSpan? span = _readerDocument?.Spans.LastOrDefault(item =>
            _readerSelectionOffset >= item.Start && _readerSelectionOffset <= item.Start + item.Length);
        position.SegmentId = span?.SegmentId;
        position.SegmentOffset = span is null ? null : Math.Clamp(_readerSelectionOffset - span.Start, 0, span.Length);
        position.UpdatedAt = DateTimeOffset.UtcNow;
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
        _readerSelectionOffset = _readerDocument?.ResolveCharacterOffset(
            position.SegmentId, position.SegmentOffset, position.CharacterOffset) ?? 0;
        _restoringReaderScroll = true;
        try
        {
            if (position.SegmentOffset is not null) SelectReaderCaret(_readerSelectionOffset);
            else ReaderScroll.ScrollToVerticalOffset(position.ScrollOffset);
        }
        finally { _restoringReaderScroll = false; }
    }

    private void SelectReaderCaret(int offset)
    {
        ReaderRunRange? range = _readerRuns.LastOrDefault(item =>
            offset >= item.Start && offset <= item.Start + item.Length);
        if (range is null) return;
        TextPointer? pointer = range.Run.ContentStart.GetPositionAtOffset(
            Math.Clamp(offset - range.Start, 0, range.Length));
        if (pointer is null) return;
        ReaderText.Selection.Select(pointer, pointer);
        pointer.Paragraph?.BringIntoView();
    }

    private void QueueReaderPreferencesSave() { _readerSaveTimer.Stop(); _readerSaveTimer.Start(); }
    private async Task SaveReaderPreferencesAsync()
    {
        if (_readerLoadFailure is not null)
            throw new System.IO.IOException("Không đọc được Reader preferences; file lỗi được giữ nguyên và giá trị mặc định chưa được lưu đè. Hãy sửa file rồi thử lại.", _readerLoadFailure);
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
        ApplyReaderCatalogVisibility();
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
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11) { OnToggleFullScreen(sender, e); e.Handled = true; return; }
        if (key == Key.Escape && _focusMode) { OnToggleFocusMode(sender, e); e.Handled = true; return; }
        if (HandleShortcut(key, Keyboard.Modifiers)) { e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.S)
        { OnSaveProgress(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.H)
        { OnOpenBatchReplace(sender, e); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.PageUp)
        {
            OnPreviousProjectChapter(sender, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Alt && key == Key.PageDown)
        {
            OnNextProjectChapter(sender, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.R)
        {
            OnTranslate(sender, e);
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.M)
        {
            ToggleMaximizeActiveTraditionalPane();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.C)
        {
            TriggerQuickCharacterFromKeyboard();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.G)
        {
            TriggerQuickGlossaryFromKeyboard();
            e.Handled = true;
            return;
        }
        if (EditorTabs.SelectedIndex != 0 || ReaderModeButton.IsChecked != true) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F) { OnOpenReaderSearch(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (key is Key.Add or Key.OemPlus)) { ReaderFontSizeInput.Value = Math.Min(30, ReaderFontSizeInput.Value + 1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && (key is Key.Subtract or Key.OemMinus)) { ReaderFontSizeInput.Value = Math.Max(14, ReaderFontSizeInput.Value - 1); e.Handled = true; }
        else if (!ReaderSearchInput.IsKeyboardFocusWithin && key is Key.PageDown or Key.Space) { ReaderScroll.PageDown(); e.Handled = true; }
        else if (!ReaderSearchInput.IsKeyboardFocusWithin && key == Key.PageUp) { ReaderScroll.PageUp(); e.Handled = true; }
    }

    internal bool? LastPaletteCommandsMode { get; private set; }

    internal bool HandleShortcut(Key key, ModifierKeys modifiers, bool showPalette = true)
    {
        if (modifiers == ModifierKeys.Control && key == Key.D1) { ShowTranslationWorkspace(false); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.D2) { ShowTranslationWorkspace(true); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.B) { OnToggleProjectSidebar(this, new RoutedEventArgs()); return true; }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.I) { OnToggleInspector(this, new RoutedEventArgs()); return true; }
        if (modifiers == ModifierKeys.Control && key == Key.Return) { OnApproveAndAdvance(this, new RoutedEventArgs()); return true; }
        if (modifiers == ModifierKeys.Alt && key == Key.Up) { NavigateSegment(-1); return true; }
        if (modifiers == ModifierKeys.Alt && key == Key.Down) { NavigateSegment(1); return true; }
        if (modifiers == ModifierKeys.None && key == Key.F8) { JumpToNextIssueSegment(); return true; }
        if (key == Key.P && modifiers is ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift))
        {
            LastPaletteCommandsMode = modifiers.HasFlag(ModifierKeys.Shift);
            if (showPalette) ShowQuickPalette(LastPaletteCommandsMode.Value);
            return true;
        }
        return false;
    }

    private void ToggleMaximizeActiveTraditionalPane()
    {
        if (EditorTabs.SelectedIndex != 1) return;
        if (_maximizedTraditionalPane is not null || _hiddenTraditionalPanes.Count == 3)
        {
            OnRestoreTraditionalPanes(this, new RoutedEventArgs());
            return;
        }
        string tag = SourceText.IsKeyboardFocusWithin ? "Chinese" :
            HanVietText.IsKeyboardFocusWithin ? "HanViet" :
            VietPhraseText.IsKeyboardFocusWithin ? "VietPhrase" : "Ai";
        OnMaximizeTraditionalPane(new FrameworkElement { Tag = tag }, new RoutedEventArgs());
    }

    private async void TriggerQuickCharacterFromKeyboard()
    {
        TextBox target = SourceText.SelectionLength > 0 ? SourceText : ChapterInput;
        CaptureSourceSelection(target);
        if (!BeginUiOperation()) return;
        try { await AddQuickCharacterAsync(target); }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void TriggerQuickGlossaryFromKeyboard()
    {
        TextBox target = SourceText.SelectionLength > 0 ? SourceText : ChapterInput;
        CaptureSourceSelection(target);
        if (!BeginUiOperation()) return;
        try { await AddSelectedGlossaryAsync(target); }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    internal sealed record QuickPaletteItem(string Id, string Title, string Subtitle, Func<Task> Execute)
    {
        public string Label => string.IsNullOrWhiteSpace(Subtitle) ? Title : $"{Title} — {Subtitle}";
        public override string ToString() => Label;
    }

    internal IReadOnlyList<QuickPaletteItem> GetCommandPaletteItems(bool commandsMode, string query)
    {
        string trimmed = (query ?? "").Trim();
        if (!commandsMode && !trimmed.StartsWith(">", StringComparison.Ordinal))
        {
            if (_projectSession is null) return [];
            return _projectCatalog.Query(_projectSession, new(Search: trimmed))
                .Select(c => new QuickPaletteItem(
                    c.ChapterId.ToString("N"),
                    $"{ProjectStateDot(c.TranslationState)} Chương {c.Number:0000} · {c.VietnameseTitle}",
                    $"{ProjectStateLabel(c.TranslationState)} · {c.CompletedSegmentCount}/{c.SegmentCount} đoạn",
                    () => LoadProjectChapterAsync(c.ChapterId)))
                .ToArray();
        }

        string cmdQuery = trimmed.StartsWith(">", StringComparison.Ordinal) ? trimmed[1..].Trim() : trimmed;
        var commands = new QuickPaletteItem[]
        {
            new("approve-next", "Duyệt đoạn & sang đoạn kế tiếp", "Ctrl+Enter", () => ApproveSegmentCoreAsync(advanceToNext: true)),
            new("next-issue", "Nhảy tới đoạn lỗi / cần kiểm tra kế tiếp", "F8", () => { JumpToNextIssueSegment(); return Task.CompletedTask; }),
            new("translate-seg", "Dịch / Dịch lại đoạn đang chọn", "Ctrl+Shift+R", () => { OnTranslate(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("translate-all", "Dịch toàn bộ chương hiện tại", "Queue 1 chương", () => { OnTranslateAll(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("approve-chapter", "Duyệt toàn bộ chương hiện tại", "Duyệt tất cả đoạn đã có bản dịch", () => { OnApproveAllSegments(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("scan-chinese", "Kiểm tra & Duyệt · Quét chữ Hán còn sót", "Ctrl+H", () => { NavigateTo(WorkspaceId.Editor, EditorMode.Review); ReviewChecksPanel.IsExpanded = true; OnScanChineseText(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("scan-pronouns", "Kiểm tra & Duyệt · Quét xưng hô", "Checks", () => { NavigateTo(WorkspaceId.Editor, EditorMode.Review); ReviewChecksPanel.IsExpanded = true; OnScanPronouns(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("mode-two", "Biên tập", "Ctrl+1 · Lưới song ngữ", () => { NavigateTo(WorkspaceId.Editor, EditorMode.Translate); return Task.CompletedTask; }),
            new("mode-four", "Kiểm tra & Duyệt", "Ctrl+2 · Biên tập Segment", () => { NavigateTo(WorkspaceId.Editor, EditorMode.Review); ReviewChecksPanel.IsExpanded = true; return Task.CompletedTask; }),
            new("mode-reader", "Chuyển sang Chế độ Đọc (Reader)", "Đọc thử toàn chương", () => { ActivateShellSection("reader"); return Task.CompletedTask; }),
            new("open-editor", "Mở Biên tập · Hai ô", "Ctrl+1", () => { NavigateTo(WorkspaceId.Editor, EditorMode.Translate); return Task.CompletedTask; }),
            new("open-review", "Mở Kiểm tra & Duyệt · Bốn ô", "Ctrl+2", () => { NavigateTo(WorkspaceId.Editor, EditorMode.Review); return Task.CompletedTask; }),
            new("open-resources-dictionary", "Tài nguyên · Từ điển & Knowledge Context", "Tài nguyên", () => { ShowResourcesWorkspace("Dictionary"); return Task.CompletedTask; }),
            new("open-resources-characters", "Tài nguyên · Nhân vật & xưng hô", "Tài nguyên", () => { ShowResourcesWorkspace("Characters"); return Task.CompletedTask; }),
            new("open-resources-prompts", "Tài nguyên · Prompt Presets", "Tài nguyên", () => { ShowResourcesWorkspace("Prompts"); return Task.CompletedTask; }),
            new("open-settings-ai", "Thiết lập · AI Provider & GPU", "Thiết lập", () => { ShowSettingsWorkspace("Ai"); return Task.CompletedTask; }),
            new("open-settings-data", "Thiết lập · Data QuickTranslator", "Thiết lập", () => { ShowSettingsWorkspace("Traditional"); return Task.CompletedTask; }),
            new("open-settings-appearance", "Thiết lập · Giao diện", "Thiết lập", () => { ShowSettingsWorkspace("Appearance"); return Task.CompletedTask; }),
            new("open-library", "Mở Thư viện Project", "Project", () => { OnShowProjectLibrary(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("open-queue", "Mở Hàng đợi Project", "Cửa sổ riêng", OpenQueueStatusAsync),
            new("toggle-sidebar", "Ẩn / Hiện cột Mục lục bên trái", "Ctrl+B", () => { OnToggleProjectSidebar(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("toggle-inspector", "Ẩn / Hiện bảng Inspector bên phải", "Ctrl+Shift+I", () => { OnToggleInspector(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("open-chapters", "Mở Quản lý chương (Chapter Manager)", "Chọn dải chương, đổi tên, lọc", () => { OnOpenChapterManager(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("open-import", "Nhập nhiều chương từ tệp TXT", "Bulk Import Preview", () => OpenDetectedBulkImportAsync()),
            new("open-gpu", "Mở Quản lý GPU (Local + Remote SSH)", "Dual-GPU Runtime", () => { OnOpenGpuManager(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("open-publish", "Xuất bản toàn bộ truyện (TXT / PDF)", "Kiểm tra & ghép chương", OpenProjectPublicationAsync),
            new("edit-source", "Sửa nguyên văn chương", "Hành động nâng cao", () => { OnBeginSourceEdit(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("split-segment", "Tách đoạn", "Cấu trúc đoạn", () => { OnSplitSegment(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("merge-segment", "Gộp đoạn", "Cấu trúc đoạn", () => { OnMergeSegments(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("move-boundary", "Dời ranh giới", "Cấu trúc đoạn", () => { OnMoveBoundary(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("structure-history", "Lịch sử cấu trúc", "Cấu trúc đoạn", () => { OnShowSegmentationArchive(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("restore-structure", "Khôi phục cấu trúc", "Cấu trúc đoạn", () => { OnRestoreStructure(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("segment-history", "Lịch sử đoạn", "Kiểm tra & Duyệt", () => { OnShowCurrentHistory(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("show-prompt", "Xem prompt / cấu hình", "Attempt hiện tại", () => { OnShowPrompt(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("batch-replace", "Thay thế hàng loạt", "Ctrl+H", () => { OnOpenBatchReplace(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("export-chapter", "Xuất chương TXT", "Chương hiện tại", () => { OnExportChapter(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("toggle-ai-compare", "So sánh AI gốc", "Kiểm tra & Duyệt", () => { OnToggleAiComparison(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("undo-pronouns", "Hoàn tác chuẩn hóa xưng hô", "Kiểm tra", () => { OnUndoPronouns(this, new RoutedEventArgs()); return Task.CompletedTask; }),
            new("apply-pronouns", "Áp dụng đề xuất xưng hô", "Kiểm tra", () => { OnApplyPronouns(this, new RoutedEventArgs()); return Task.CompletedTask; })
        };
        if (cmdQuery.Length == 0) return commands;
        return commands.Where(c =>
            c.Title.Contains(cmdQuery, StringComparison.CurrentCultureIgnoreCase) ||
            c.Subtitle.Contains(cmdQuery, StringComparison.CurrentCultureIgnoreCase)).ToArray();
    }

    internal void ShowQuickPalette(bool commandsMode)
    {
        var input = new TextBox
        {
            Text = commandsMode ? ">" : "",
            ToolTip = "Nhập số/tên chương để nhảy nhanh, hoặc gõ > để tìm lệnh (Enter để thực thi)",
            Margin = new Thickness(0, 0, 0, 8)
        };
        var list = new ListBox { Height = 320, DisplayMemberPath = nameof(QuickPaletteItem.Label) };
        System.Windows.Automation.AutomationProperties.SetName(input, commandsMode ? "Tìm lệnh" : "Tìm chương để chuyển nhanh");
        System.Windows.Automation.AutomationProperties.SetHelpText(input, (string)input.ToolTip);
        System.Windows.Automation.AutomationProperties.SetName(list, commandsMode ? "Danh sách lệnh" : "Danh sách chương chuyển nhanh");
        var hint = new TextBlock
        {
            Text = commandsMode
                ? "Command Palette (Ctrl+Shift+P) · Gõ tên lệnh rồi bấm Enter"
                : "Chuyển nhanh chương (Ctrl+P) · Gõ số hoặc tên chương (hoặc gõ > để tìm lệnh)",
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(2, 0, 2, 6)
        };
        var panel = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        DockPanel.SetDock(input, Dock.Top); panel.Children.Add(input);
        panel.Children.Add(list);
        var dialog = new Window
        {
            Title = commandsMode ? "QT Studio · Bảng lệnh nhanh (Command Palette)" : "QT Studio · Chuyển nhanh tới chương",
            Width = 640, Height = 440, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)FindResource("PanelBrush"), Foreground = (Brush)FindResource("TextBrush"),
            Content = panel
        };
        if (IsVisible) dialog.Owner = this;
        ClampWindowToWorkArea(dialog, 640, 440);

        void RefreshItems()
        {
            var items = GetCommandPaletteItems(commandsMode, input.Text);
            list.ItemsSource = items;
            if (items.Count > 0) list.SelectedIndex = 0;
        }

        async Task ExecuteSelectedAsync()
        {
            if (list.SelectedItem is not QuickPaletteItem selected) return;
            dialog.Close();
            try { await selected.Execute(); }
            catch (Exception ex) { ShowError(ex); }
        }

        input.TextChanged += (_, _) => RefreshItems();
        input.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Down && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Min(list.Items.Count - 1, list.SelectedIndex + 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Up && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex - 1);
                list.ScrollIntoView(list.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                await ExecuteSelectedAsync();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                dialog.Close();
            }
        };
        list.MouseDoubleClick += async (_, _) => await ExecuteSelectedAsync();
        RefreshItems();
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.CaretIndex = input.Text.Length;
        };
        dialog.ShowDialog();
    }

    private sealed record ReaderRunRange(int Start, int Length, Run Run);
    private sealed record ReaderChapterItem(Guid ChapterId, string Title, string Status)
    {
        public string Label => $"{Title}\n{Status}";
        public override string ToString() => Label;
    }
}
