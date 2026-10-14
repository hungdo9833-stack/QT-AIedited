using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private WorkspaceLayoutStore _workspaceLayoutStore = new();
    private WorkspaceLayoutPreferences _workspaceLayout = new();
    private readonly SemaphoreSlim _workspaceLayoutSaveGate = new(1, 1);
    private bool _workspaceLayoutReady;
    private bool _responsiveSidebarCollapsed;
    private bool _responsiveInspectorCollapsed;
    private bool _compactShell;

    private void InitializeProductionShell()
    {
        ApplyShellTheme(ShellTheme.Dark);
        Loaded += async (_, _) =>
        {
            try
            {
                _workspaceLayout = await _workspaceLayoutStore.LoadAsync();
                _workspaceLayoutReady = true;
                ApplyWorkspaceLayout();
            }
            catch (Exception ex)
            {
                _workspaceLayoutReady = false;
                SetStatus("Không đọc được bố cục UI; giữ bố cục an toàn mặc định: " + ex.Message);
            }
            ShowTranslationWorkspace(_workspaceLayout.FourPaneMode);
            UpdateShellChrome();
        };
    }

    private void ApplyWorkspaceLayout()
    {
        ApplyShellTheme(_workspaceLayout.Theme);
        TranslationInspector.Width = _workspaceLayout.InspectorWidth;
        ProjectSidebar.Width = _workspaceLayout.ProjectSidebarWidth;
        ChapterTwoPaneLeftColumn.Width = new(_workspaceLayout.TwoPaneSourceStar, GridUnitType.Star);
        SegmentNavigatorColumn.Width = new(_workspaceLayout.SegmentNavigatorWidth);
        TraditionalLeftColumn.Width = new(_workspaceLayout.FourPaneLeftStar, GridUnitType.Star);
        TraditionalTopRow.Height = new(_workspaceLayout.FourPaneTopStar, GridUnitType.Star);
        SetTranslationInspectorVisibility(_workspaceLayout.InspectorVisible);
        ApplyProjectSidebarVisibility();
        TwoPaneModeButton.IsChecked = !_workspaceLayout.FourPaneMode;
        FourPaneModeButton.IsChecked = _workspaceLayout.FourPaneMode;
    }

    private async Task SaveWorkspaceLayoutAsync()
    {
        if (!_workspaceLayoutReady) return;
        await _workspaceLayoutSaveGate.WaitAsync();
        try
        {
            _workspaceLayout.InspectorWidth = Math.Clamp(TranslationInspector.ActualWidth > 0
                ? TranslationInspector.ActualWidth : TranslationInspector.Width, 240, 600);
            _workspaceLayout.ProjectSidebarWidth = Math.Clamp(ProjectSidebar.ActualWidth > 0
                ? ProjectSidebar.ActualWidth : ProjectSidebar.Width, 220, 520);
            _workspaceLayout.TwoPaneSourceStar = StarValue(ChapterTwoPaneLeftColumn.Width, 1);
            _workspaceLayout.SegmentNavigatorWidth = Math.Clamp(SegmentNavigatorColumn.ActualWidth, 180, 520);
            _workspaceLayout.FourPaneLeftStar = StarValue(TraditionalLeftColumn.Width, 1);
            _workspaceLayout.FourPaneTopStar = StarValue(TraditionalTopRow.Height, 1);
            await _workspaceLayoutStore.SaveAsync(_workspaceLayout);
        }
        catch (Exception ex) { SetStatus("Chưa lưu được bố cục UI: " + ex.Message); }
        finally { _workspaceLayoutSaveGate.Release(); }
    }

    private static double StarValue(GridLength length, double fallback) =>
        length.IsStar ? Math.Clamp(length.Value, 0.25, 4) : fallback;

    private async void OnWorkspaceSplitterDragCompleted(object sender,
        System.Windows.Controls.Primitives.DragCompletedEventArgs e) => await SaveWorkspaceLayoutAsync();

    private async void OnToggleInspector(object sender, RoutedEventArgs e)
    {
        _workspaceLayout.InspectorVisible = !_workspaceLayout.InspectorVisible;
        SetTranslationInspectorVisibility(_workspaceLayout.InspectorVisible);
        await SaveWorkspaceLayoutAsync();
    }

    private async void OnToggleProjectSidebar(object sender, RoutedEventArgs e)
    {
        _workspaceLayout.ProjectSidebarVisible = !_workspaceLayout.ProjectSidebarVisible;
        ApplyProjectSidebarVisibility();
        await SaveWorkspaceLayoutAsync();
    }

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveShell(e.NewSize.Width);

    private void UpdateResponsiveShell(double width)
    {
        if (ShellNavigation is null) return;
        _compactShell = width > 0 && width < 1180;
        _responsiveSidebarCollapsed = width > 0 && width < 1320;
        _responsiveInspectorCollapsed = width > 0 && width < 1100;
        ShellNavigation.Width = _compactShell ? 148 : 176;
        BrandBlock.Width = _compactShell ? 150 : 190;
        ProjectSwitcher.Width = _compactShell ? 190 : 300;
        TitleInput.Width = _compactShell ? 250 : 360;
        ShellChapterText.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        ChapterModeHint.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        ChapterSummary.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        ProjectSidebarToggleButton.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        ReaderBookmarkButton.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        FocusModeButton.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        ReaderFullScreenButton.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        ApplyProjectSidebarVisibility();
        ApplyReaderCatalogVisibility();
        SetTranslationInspectorVisibility(_workspaceLayout.InspectorVisible);
        RefreshShellQueueStatus();
    }

    private void ApplyProjectSidebarVisibility()
    {
        if (ProjectSidebar is null || ProjectSidebarToggleButton is null) return;
        bool projectOpen = _projectSession is not null;
        bool readerActive = ReaderModeButton?.IsChecked == true;
        bool visible = projectOpen && _workspaceLayout.ProjectSidebarVisible && !_responsiveSidebarCollapsed && !readerActive;
        ProjectSidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ProjectSidebarSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ProjectSidebarToggleButton.Visibility = readerActive || _compactShell
            ? Visibility.Collapsed : Visibility.Visible;
        ProjectSidebarToggleButton.Content = visible ? "Ẩn mục lục" : "Hiện mục lục";
        ProjectSidebarToggleButton.ToolTip = _responsiveSidebarCollapsed
            ? "Mục lục tự thu gọn ở cửa sổ hẹp; dùng màn Chương để quản lý đầy đủ."
            : null;
    }

    private void SetTranslationInspectorVisibility(bool visible)
    {
        bool workspace = EditorTabs.SelectedIndex is 0 or 1 && ReaderModeButton.IsChecked != true;
        TranslationInspector.Visibility = visible && workspace && !_responsiveInspectorCollapsed
            ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = TranslationInspector.Visibility;
        InspectorToggleButton.Content = _compactShell ? "Inspector" :
            TranslationInspector.Visibility == Visibility.Visible ? "Ẩn Inspector" : "Hiện Inspector";
        InspectorToggleButton.ToolTip = _responsiveInspectorCollapsed
            ? "Inspector tự thu gọn ở mức DPI cao hoặc cửa sổ hẹp."
            : null;
    }

    private async void OnToggleShellTheme(object sender, RoutedEventArgs e)
    {
        _workspaceLayout.Theme = _workspaceLayout.Theme == ShellTheme.Dark ? ShellTheme.Light : ShellTheme.Dark;
        ApplyShellTheme(_workspaceLayout.Theme);
        await SaveWorkspaceLayoutAsync();
    }

    private void ApplyShellTheme(ShellTheme theme)
    {
        bool dark = theme == ShellTheme.Dark;
        SetBrush("AppBackgroundBrush", dark ? "#07111F" : "#F4F7FB");
        SetBrush("PanelBrush", dark ? "#0E1B2D" : "#FFFFFF");
        SetBrush("PanelAltBrush", dark ? "#12243A" : "#EAF0F7");
        SetBrush("EditorSurfaceBrush", dark ? "#0A1626" : "#FFFFFF");
        SetBrush("TextBrush", dark ? "#E8EEF7" : "#142033");
        SetBrush("BorderBrush", dark ? "#263D59" : "#CBD5E1");
        SetBrush("MutedBrush", dark ? "#91A4BC" : "#5B6B7F");
        SetBrush("SurfaceBrush", dark ? "#0E1B2D" : "#F7F9FC");
        SetBrush("SelectionBrush", dark ? "#193B63" : "#D9EAFE");
        SetBrush("DangerBrush", dark ? "#F97066" : "#B42318");
        SetBrush("SuccessBrush", dark ? "#32D583" : "#067647");
        SetBrush("WarningBrush", dark ? "#FDB022" : "#B54708");
        ThemeToggleButton.Content = dark ? "Light" : "Dark";
        Background = (Brush)FindResource("AppBackgroundBrush");
        Foreground = (Brush)FindResource("TextBrush");
    }

    private static void SetBrush(string key, string color)
    {
        ResourceDictionary resources = Application.Current.Resources;
        if (resources[key] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = (Color)ColorConverter.ConvertFromString(color);
        else resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private async void OnShellNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string target }) return;
        switch (target)
        {
            case "Translate": ShowTranslationWorkspace(_workspaceLayout.FourPaneMode); break;
            case "Chapters": OnOpenChapterManager(sender, e); break;
            case "Import": OnAddChapterFromText(sender, e); break;
            case "Queue": await OpenQueueStatusAsync(); break;
            case "Library": ShowProjectLibraryWindow(); break;
            case "Publish": await OpenProjectPublicationAsync(); break;
            case "Reader": ShowReaderWorkspace(); break;
            case "Settings": ShowSettingsWorkspace(); break;
        }
    }

    private void ShowTranslationWorkspace(bool fourPane)
    {
        EditorModeButton.IsChecked = true;
        _workspaceLayout.FourPaneMode = fourPane;
        if (fourPane)
        {
            EnsureSelectedSegmentFromChapterCaret();
            EditorTabs.SelectedIndex = 1;
            FourPaneModeButton.IsChecked = true;
        }
        else
        {
            EditorTabs.SelectedIndex = 0;
            TwoPaneModeButton.IsChecked = true;
        }
        SetTranslationInspectorVisibility(_workspaceLayout.InspectorVisible);
        Refresh();
        Dispatcher.BeginInvoke(() => FocusSelectedEditor(fourPane));
        _ = SaveWorkspaceLayoutAsync();
    }

    private void OnEditorLayoutChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || TwoPaneModeButton is null || FourPaneModeButton is null) return;
        ShowTranslationWorkspace(FourPaneModeButton.IsChecked == true);
    }

    private void OnShowTwoPane(object sender, RoutedEventArgs e) => ShowTranslationWorkspace(false);

    private void ShowReaderWorkspace()
    {
        EditorTabs.SelectedIndex = 0;
        ReaderModeButton.IsChecked = true;
        SetTranslationInspectorVisibility(false);
        ChapterModeHint.Text = "Đọc bản dịch · chọn đoạn để mở đúng vị trí trong Editor";
    }

    private void ShowSettingsWorkspace(string? section = null)
    {
        EditorTabs.SelectedIndex = 2;
        SetTranslationInspectorVisibility(false);
        if (section is not null)
            SettingsNavigation.SelectedItem = SettingsNavigation.Items.OfType<ListBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, section, StringComparison.Ordinal));
    }

    private async void OnOpenQueueStatus(object sender, RoutedEventArgs e) => await OpenQueueStatusAsync();

    private async Task OpenQueueStatusAsync()
    {
        if (_projectSession is null) { SetStatus("Hãy mở Project trước khi xem hàng đợi."); return; }
        ProjectTranslationCoordinator coordinator = await EnsureProjectTranslationCoordinatorAsync();
        if (coordinator.CurrentJob is null)
        {
            SetStatus("Project chưa có queue. Chọn nhiều chương trong Chapter Manager để tạo Queue Plan.");
            OnOpenChapterManager(this, new RoutedEventArgs());
            return;
        }
        ShowProjectTranslationWindow(coordinator, null);
    }

    private async Task OpenProjectPublicationAsync()
    {
        if (_projectSession is null) { SetStatus("Hãy mở Project trước khi xuất bản."); return; }
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
            if (_service is not null) await SaveCurrentAsync();
            var service = new ProjectPublicationService(_projectRepository, _projectCatalog);
            var window = new ProjectPublicationWindow(_projectSession, service) { Owner = this };
            window.ShowDialog();
            if (window.LastExportPath is string path)
                SetStatus("Đã xuất toàn bộ chương thành một file: " + path);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void OnOpenInspectorSettings(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section }) ShowSettingsWorkspace(section);
    }

    private void OnChapterBlockFocused(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox input) SelectChapterBlock(input);
    }

    private void SelectChapterBlock(TextBox input)
    {
        if (_rendering || input.Tag is not Guid id || _service is null) return;
        _selectedId = id;
        _shownAttemptId = null;
        _service.SelectSegment(id);
        RefreshTranslationInspector(_service.GetSnapshot().Segments.Single(segment => segment.Id == id));
    }

    private void OnChapterBlockSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: Guid id } input && id != _selectedId) SelectChapterBlock(input);
    }

    private void EnsureSelectedSegmentFromChapterCaret()
    {
        ChapterDocument? chapter = _service?.GetSnapshot();
        if (chapter is null) return;
        int offset = Math.Clamp(ChapterInput.SelectionStart, 0, chapter.OriginalText.Length);
        TranslationSegment? segment = chapter.Segments.FirstOrDefault(item =>
            offset >= item.StartUtf16 && offset <= item.StartUtf16 + item.SourceText.Length);
        _selectedId = segment?.Id ?? _selectedId ?? chapter.Segments.FirstOrDefault()?.Id;
        if (_selectedId is Guid id) _service!.SelectSegment(id);
    }

    private void OnChapterSourceSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering || _service is null || ChapterInput.SelectionLength == 0) return;
        EnsureSelectedSegmentFromChapterCaret();
        ChapterDocument chapter = _service.GetSnapshot();
        TranslationSegment? segment = chapter.Segments.SingleOrDefault(item => item.Id == _selectedId);
        _rendering = true;
        try
        {
            SegmentList.SelectedItem = SegmentList.Items.Cast<SegmentItem>()
                .SingleOrDefault(item => item.Id == _selectedId);
        }
        finally { _rendering = false; }
        RefreshTranslationInspector(segment);
    }

    private void FocusSelectedEditor(bool fourPane)
    {
        if (fourPane) { DraftText.Focus(); return; }
        TextBox? target = FindVisualChildren<TextBox>(ChapterBlocksItems)
            .FirstOrDefault(input => input.Tag is Guid id && id == _selectedId);
        target?.Focus();
    }

    private void OnShowCurrentHistory(object sender, RoutedEventArgs e)
    {
        ShowTranslationWorkspace(true);
        HistoryList.Focus();
    }

    private void RefreshTranslationInspector(TranslationSegment? segment)
    {
        InspectorSelectionText.Text = segment is null ? "Chọn một segment" :
            $"Đoạn {segment.Order} · {StatusLabel(segment.Status)}";
        if (segment is null)
        {
            InspectorDictionaryText.Text = "Chưa chọn segment.";
            InspectorGlossaryItems.ItemsSource = null;
            InspectorCharacterItems.ItemsSource = null;
            InspectorContextText.Text = "Project chưa có ngữ cảnh cho segment đang chọn.";
            return;
        }
        string hanViet = HanVietText.Text;
        string vietPhrase = VietPhraseText.Text;
        InspectorDictionaryText.Text = $"Trung: {Preview(segment.SourceText)}\n\nHán Việt: {Preview(hanViet)}\n\nVP: {Preview(vietPhrase)}";
        InspectorGlossaryItems.ItemsSource = _glossaryRows.Where(term =>
            segment.SourceText.Contains(term.Chinese, StringComparison.Ordinal))
            .Select(term => $"{term.Chinese}  →  {term.Vietnamese}").ToArray();
        InspectorContextText.Text = string.IsNullOrWhiteSpace(ContextNotesInput.Text)
            ? "Chưa có ghi chú ngữ cảnh Project." : ContextNotesInput.Text;
        InspectorCharacterItems.ItemsSource = _characterProject.Profiles.Where(profile =>
            !string.IsNullOrWhiteSpace(profile.ChineseName) && segment.SourceText.Contains(profile.ChineseName, StringComparison.Ordinal))
            .Select(profile => $"{profile.ChineseName}  →  {profile.VietnameseName}\n{profile.PrimaryNarrativePronoun}").ToArray();
        FourPaneSelectionText.Text = $"Đoạn {segment.Order} · {StatusLabel(segment.Status)}";
    }

    private void UpdateShellChrome()
    {
        ChapterDocument? chapter = _service?.GetSnapshot();
        ShellChapterText.Text = chapter is null ? "Chưa mở chương" : chapter.Title;
        RefreshTranslationInspector(chapter?.Segments.SingleOrDefault(segment => segment.Id == _selectedId));
        RefreshShellQueueStatus();
        ApplyProjectSidebarVisibility();
    }

    private void RefreshShellQueueStatus()
    {
        ProjectTranslationProgress? progress = _projectTranslationCoordinator?.CurrentJob is null
            ? null : _projectTranslationCoordinator.GetProgress();
        if (_compactShell)
        {
            ShellQueueButton.Content = progress is null ? "Queue" :
                $"Queue {progress.CompletedChapters}/{progress.TotalChapters}";
            return;
        }
        ShellQueueButton.Content = progress is null ? "Hàng đợi · chưa chạy" :
            $"Hàng đợi · {progress.CompletedChapters}/{progress.TotalChapters}" +
            (progress.FailedChapters > 0 ? $" · {progress.FailedChapters} lỗi" : "");
    }

    private void OnProjectTranslationProgressChanged(object? sender, EventArgs e)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(RefreshShellQueueStatus);
    }
}
