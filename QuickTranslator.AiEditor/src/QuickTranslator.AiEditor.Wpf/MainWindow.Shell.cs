using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using QuickTranslator.AiEditor.Core.Editing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

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
    private bool _navRailCollapsed;
    private string _lastLiveSelectionChinese = "";

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
        // SystemEvents holds its handlers statically: subscribe while shown only.
        Loaded += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged;
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
        ApplyTwoPaneEditorModeLayout();
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
            if (_sourceEditMode || _service is null)
                _workspaceLayout.TwoPaneSourceStar = StarValue(ChapterTwoPaneLeftColumn.Width, 1);
            _workspaceLayout.SegmentNavigatorWidth = Math.Clamp(SegmentNavigatorColumn.ActualWidth > 0
                ? SegmentNavigatorColumn.ActualWidth : _workspaceLayout.SegmentNavigatorWidth, 180, 520);
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

    private void OnToggleNavRail(object sender, RoutedEventArgs e)
    {
        _navRailCollapsed = !_navRailCollapsed;
        ShellNavigation.Width = _navRailCollapsed ? 56 : (_compactShell ? 148 : 176);
        if (FindName("NavRailToggleButton") is Button btn)
            btn.Content = _navRailCollapsed ? "»" : "«";
    }

    private void OnSwitchLeftPanelToChapters(object sender, RoutedEventArgs e)
    {
        _workspaceLayout.ProjectSidebarVisible = true;
        ApplyProjectSidebarVisibility();
    }

    private void OnSwitchLeftPanelToSegments(object sender, RoutedEventArgs e)
    {
        if (EditorTabs.SelectedIndex == 1)
        {
            _workspaceLayout.ProjectSidebarVisible = false;
            ApplyProjectSidebarVisibility();
        }
        else
        {
            ShowTranslationWorkspace(true);
        }
    }

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateResponsiveShell(e.NewSize.Width);

    private void UpdateResponsiveShell(double width)
    {
        if (ShellNavigation is null) return;
        _compactShell = width > 0 && width < 1180;
        _responsiveSidebarCollapsed = width > 0 && width < 1320;
        _responsiveInspectorCollapsed = width > 0 && width < 1100;
        ShellNavigation.Width = _navRailCollapsed ? 56 : (_compactShell ? 148 : 176);
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
            : "Ẩn/Hiện mục lục chương (Ctrl+B)";
    }

    // BL-14: In Two-Pane mode, show Aligned Bilingual Segment Grid when a chapter is segmented and not in _sourceEditMode;
    // show monolithic ChapterInput when _service is null or _sourceEditMode == true.
    internal void ApplyTwoPaneEditorModeLayout()
    {
        if (ChapterTwoPaneLeftColumn is null || ChapterTwoPaneSplitter is null) return;
        bool showRawSourceBox = _service is null || _sourceEditMode;
        if (FindName("ChapterSourcePaneBorder") is Border sourceBorder)
            sourceBorder.Visibility = showRawSourceBox ? Visibility.Visible : Visibility.Collapsed;
        ChapterTwoPaneSplitter.Visibility = showRawSourceBox ? Visibility.Visible : Visibility.Collapsed;
        ChapterTwoPaneLeftColumn.Width = showRawSourceBox
            ? new GridLength(Math.Clamp(_workspaceLayout.TwoPaneSourceStar, 0.25, 4), GridUnitType.Star)
            : new GridLength(0);
        if (FindName("ChapterSplitterColumn") is ColumnDefinition splitterCol)
            splitterCol.Width = showRawSourceBox ? new GridLength(5) : new GridLength(0);
        if (FindName("BilingualGridHeaderText") is TextBlock headerText)
            headerText.Text = showRawSourceBox
                ? "VIỆT · BẢN ĐANG BIÊN TẬP"
                : "LƯỚI SONG NGỮ ĐỒNG BỘ TỪNG ĐOẠN (TRUNG ↔ VIỆT · CUỘN ĐỒNG BỘ 100%)";
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
            : "Ẩn/Hiện Inspector (Ctrl+Shift+I)";
    }

    private async void OnToggleShellTheme(object sender, RoutedEventArgs e)
    {
        // The toggle switches what is on screen; it leaves "Theo Windows" for an explicit choice.
        _workspaceLayout.Theme = ThemeManager.Current == ShellTheme.Dark ? ShellTheme.Light : ShellTheme.Dark;
        ApplyShellTheme(_workspaceLayout.Theme);
        await SaveWorkspaceLayoutAsync();
    }

    private void ApplyShellTheme(ShellTheme theme)
    {
        ThemeManager.Apply(theme);
        // Segment rows hold their status brush as a value, not a resource key: give them the new theme's brush.
        if (_service is not null)
            foreach (var segment in _service.GetSnapshot().Segments)
                _chapterBlocks.FirstOrDefault(b => b.Id == segment.Id)?.UpdateMetadataOnly(segment, segment.Id == _selectedId);
        ThemeToggleButton.Content = ThemeManager.Current == ShellTheme.Dark ? "Light" : "Dark";
        SyncThemeChoice(theme);
        SetResourceReference(BackgroundProperty, "AppBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
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
        CurrentWorkspace = WorkspaceId.Editor;
        CurrentEditorMode = fourPane ? EditorMode.Review : EditorMode.Translate;
        EditorContextHeader.Visibility = Visibility.Visible;
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
        FocusSelectedEditor(fourPane);
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            ChapterBlocksItems.ApplyTemplate();
            ChapterBlocksItems.UpdateLayout();
            FocusSelectedEditor(fourPane);
        });
        _ = SaveWorkspaceLayoutAsync();
        MarkActiveNavigation(TranslateNavButton);
    }

    private void OnEditorLayoutChanged(object sender, RoutedEventArgs e)
    {
        if (!_initialized || TwoPaneModeButton is null || FourPaneModeButton is null) return;
        ShowTranslationWorkspace(FourPaneModeButton.IsChecked == true);
    }

    private void OnShowTwoPane(object sender, RoutedEventArgs e) => ShowTranslationWorkspace(false);

    private bool _syncingThemeChoice;

    // Settings › Giao diện shows the saved choice (Theo Windows / Sáng / Tối), not the resolved palette.
    private void SyncThemeChoice(ShellTheme choice)
    {
        if (ThemeSystemRadio is null) return;
        _syncingThemeChoice = true;
        try
        {
            ThemeSystemRadio.IsChecked = choice == ShellTheme.System;
            ThemeLightRadio.IsChecked = choice == ShellTheme.Light;
            ThemeDarkRadio.IsChecked = choice == ShellTheme.Dark;
        }
        finally { _syncingThemeChoice = false; }
    }

    private async void OnThemeChoiceChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingThemeChoice || sender is not RadioButton { IsChecked: true, Tag: string tag } || !Enum.TryParse(tag, out ShellTheme choice)) return;
        _workspaceLayout.Theme = choice;
        ApplyShellTheme(choice);
        await SaveWorkspaceLayoutAsync();
    }

    // "Theo Windows" follows a Windows theme change while the app is open.
    private void OnWindowsPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General || _workspaceLayout.Theme != ShellTheme.System) return;
        Dispatcher.BeginInvoke(() => { if (!_closing) ApplyShellTheme(ShellTheme.System); });
    }

    // A command chosen from the "Thêm" popup closes it.
    private void OnCommandOverflowClick(object sender, RoutedEventArgs e) { CommandOverflowToggle.IsChecked = false; StructureOverflowToggle.IsChecked = false; }

    private void OnPopupContentPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CommandOverflowToggle.IsChecked = false;
        StructureOverflowToggle.IsChecked = false;
        ReaderAppearancePopup.IsOpen = false;
        e.Handled = true;
    }

    // Only in-window workspaces are destinations; the other navigation items open separate windows.
    private void MarkActiveNavigation(Button active)
    {
        foreach (Button button in new[] { TranslateNavButton, ReaderNavButton, SettingsNavButton })
            NavState.SetIsActive(button, ReferenceEquals(button, active));
    }

    private void ShowReaderWorkspace()
    {
        CurrentWorkspace = WorkspaceId.Reader;
        EditorContextHeader.Visibility = Visibility.Collapsed;
        EditorTabs.SelectedIndex = 0;
        ReaderModeButton.IsChecked = true;
        SetTranslationInspectorVisibility(false);
        ChapterModeHint.Text = "Đọc bản dịch · chọn đoạn để mở đúng vị trí trong Editor";
        MarkActiveNavigation(ReaderNavButton);
    }

    private void ShowSettingsWorkspace(string? section = null)
    {
        CurrentWorkspace = WorkspaceId.Settings;
        EditorContextHeader.Visibility = Visibility.Collapsed;
        EditorTabs.SelectedIndex = 2;
        MarkActiveNavigation(SettingsNavButton);
        SetTranslationInspectorVisibility(false);
        if (section is not null)
            SettingsNavigation.SelectedItem = SettingsNavigation.Items.OfType<ListBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, section, StringComparison.Ordinal));
    }

    internal void ActivateShellSection(string section)
    {
        switch ((section ?? "").Trim().ToLowerInvariant())
        {
            case "translate":
                ShowTranslationWorkspace(_workspaceLayout.FourPaneMode);
                break;
            case "reader":
                ShowReaderWorkspace();
                break;
            case "character":
            case "characters":
                ShowSettingsWorkspace("Characters");
                break;
            case "dictionary":
                ShowSettingsWorkspace("Dictionary");
                break;
            case "quality":
                NavigateTo(WorkspaceId.Editor, EditorMode.Review);
                ReviewChecksPanel.IsExpanded = true;
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
                {
                    ReviewChecksPanel.Focus();
                    Keyboard.Focus(ReviewChecksPanel);
                });
                break;
            case "settings":
                ShowSettingsWorkspace();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(section), section, "Khu vực workspace không được hỗ trợ.");
        }
    }

    private async void OnOpenQueueStatus(object sender, RoutedEventArgs e) => await OpenQueueStatusAsync();

    private async Task OpenQueueStatusAsync()
    {
        if (_projectSession is null) { SetStatus("Hãy mở Project trước khi xem hàng đợi."); return; }
        ProjectTranslationCoordinator coordinator = await EnsureProjectTranslationCoordinatorAsync();
        if (coordinator.CurrentJob is null)
        {
            SetStatus("Chưa có hàng đợi. Hãy chọn các chương cần dịch trong Quản lý chương.");
            OnOpenChapterManager(this, new RoutedEventArgs());
            return;
        }
        ShowProjectTranslationWindow(coordinator, null);
    }

    private async void OnResumeProjectTranslationFromShell(object sender, RoutedEventArgs e)
    {
        try
        {
            ProjectTranslationCoordinator coordinator = await EnsureProjectTranslationCoordinatorAsync();
            ProjectTranslationProgress progress = coordinator.GetProgress();
            if (progress.Status is not (ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted))
            {
                RefreshShellQueueStatus();
                return;
            }
            ShowProjectTranslationWindow(coordinator, null);
            if (_projectTranslationWindow is not null)
                await _projectTranslationWindow.ResumeNowAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async Task OpenProjectPublicationAsync()
    {
        if (_projectSession is null) { SetStatus("Hãy mở Project trước khi xuất bản."); return; }
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
            if (_service is not null) await SaveCurrentAsync();
            var service = new ProjectPublicationService(_projectRepository, _projectCatalog);
            var window = new ProjectPublicationWindow(_projectSession, service, LoadProjectChapterAsync) { Owner = this };
            ClampWindowToWorkArea(window, 760, 570);
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
        foreach (var b in _chapterBlocks) b.IsSelected = b.Id == id;
        var seg = _service.GetSnapshot().Segments.Single(segment => segment.Id == id);
        RefreshTraditionalSegment(seg);
        RefreshTranslationInspector(seg);
        UpdateButtons();
    }

    private void OnChapterBlockSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: Guid id } input && id != _selectedId) SelectChapterBlock(input);
    }

    private void EnsureSelectedSegmentFromChapterCaret()
    {
        ChapterDocument? chapter = _service?.GetSnapshot();
        if (chapter is null) return;
        if (_selectedId is Guid existing && chapter.Segments.Any(s => s.Id == existing))
        {
            _service!.SelectSegment(existing);
            return;
        }
        int offset = Math.Clamp(ChapterInput.SelectionStart, 0, chapter.OriginalText.Length);
        TranslationSegment? segment = chapter.Segments.FirstOrDefault(item =>
            offset >= item.StartUtf16 && offset <= item.StartUtf16 + item.SourceText.Length);
        _selectedId = segment?.Id ?? _selectedId ?? chapter.Segments.FirstOrDefault()?.Id;
        if (_selectedId is Guid id) _service!.SelectSegment(id);
    }

    private void OnChapterSourceSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_rendering || _service is null || ChapterInput.SelectionLength == 0)
        {
            if (ChapterInput.SelectionLength == 0) UpdateInspectorLiveSelection("");
            return;
        }
        UpdateInspectorLiveSelection(ChapterInput.SelectedText);
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

    // BL-13: Live Selection Lookup in TranslationInspector
    internal void UpdateInspectorLiveSelection(string selectedText)
    {
        string trimmed = (selectedText ?? "").Trim();
        _lastLiveSelectionChinese = trimmed;
        if (FindName("InspectorLiveSelectionBorder") is not Border card ||
            FindName("InspectorLiveSelectionText") is not TextBlock textBlock)
            return;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            card.Visibility = Visibility.Collapsed;
            textBlock.Text = "Bôi đen từ/cụm từ trong Editor để tra nhanh Hán Việt & VietPhrase.";
            return;
        }
        card.Visibility = Visibility.Visible;
        string hv = SuggestTraditionalVietnamese(trimmed, titleCase: true);
        string vp = SuggestTraditionalVietnamese(trimmed, titleCase: false);
        var existingChar = _characterProject.Profiles.FirstOrDefault(p =>
            string.Equals(p.ChineseName, trimmed, StringComparison.Ordinal) ||
            p.Aliases.Any(a => string.Equals(a, trimmed, StringComparison.Ordinal)));
        var existingTerm = _glossaryRows.FirstOrDefault(t => string.Equals(t.Chinese, trimmed, StringComparison.Ordinal));
        var lines = new List<string> { $"Cụm chọn: 【{trimmed}】" };
        if (!string.IsNullOrWhiteSpace(hv)) lines.Add($"Hán Việt: {hv}");
        if (!string.IsNullOrWhiteSpace(vp) && !string.Equals(vp, hv, StringComparison.OrdinalIgnoreCase))
            lines.Add($"VietPhrase: {vp}");
        if (existingChar is not null)
            lines.Add($"Nhân vật đã có: {existingChar.VietnameseName} ({existingChar.PrimaryNarrativePronoun})");
        if (existingTerm is not null)
            lines.Add($"Glossary đã có: {existingTerm.Vietnamese}");
        if (lines.Count == 1)
            lines.Add("Chưa có trong từ điển cục bộ · bấm nút bên dưới để thêm.");
        textBlock.Text = string.Join("\n", lines);
    }

    private async void OnInspectorQuickAddCharacter(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastLiveSelectionChinese) || WorkActive || _uiOperation) return;
        SourceText.Text = string.IsNullOrEmpty(SourceText.Text) ? _lastLiveSelectionChinese : SourceText.Text;
        _sourceSelections[SourceText] = new TextSelectionSnapshot(0, 0, _lastLiveSelectionChinese);
        if (!BeginUiOperation()) return;
        try
        {
            string chinese = SourceContextActions.ValidateCharacterName(_lastLiveSelectionChinese);
            var quick = ShowQuickCharacterEditor(chinese);
            if (quick is null) return;
            _characterProject.Profiles.Add(quick.Profile);
            var merged = ReadTerms().Where(t => !string.Equals(t.Chinese, quick.Profile.ChineseName, StringComparison.Ordinal)).ToList();
            merged.Add(new(quick.Profile.ChineseName, quick.Profile.VietnameseName));
            SetGlossaryTerms(merged);
            await SaveCharacterProjectAsync();
            RefreshCharacterGrid();
            UpdateInspectorLiveSelection(_lastLiveSelectionChinese);
            SetStatus($"Đã thêm nhanh nhân vật “{quick.Profile.VietnameseName}” từ Inspector.");
        }
        catch (Exception ex) { _settingsDirty = true; ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private async void OnInspectorQuickAddGlossary(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastLiveSelectionChinese) || WorkActive || _uiOperation) return;
        if (!BeginUiOperation()) return;
        try
        {
            string chinese = SourceContextActions.ValidateGlossaryTerm(_lastLiveSelectionChinese);
            var existing = _glossaryRows.FirstOrDefault(t => string.Equals(t.Chinese, chinese, StringComparison.Ordinal));
            string suggested = existing?.Vietnamese ?? SuggestTraditionalVietnamese(chinese, titleCase: false);
            var edited = ShowGlossaryEditor(existing is null && !string.IsNullOrWhiteSpace(suggested)
                ? new GlossaryRow(chinese, suggested) : existing, chinese);
            if (edited is null) return;
            if (existing is null) _glossaryRows.Add(edited);
            else _glossaryRows[_glossaryRows.IndexOf(existing)] = edited;
            GlossaryChanged();
            await PersistCurrentGlossaryAsync();
            UpdateInspectorLiveSelection(_lastLiveSelectionChinese);
            SetStatus($"Đã lưu thuật ngữ “{edited.Chinese} = {edited.Vietnamese}” từ Inspector.");
        }
        catch (Exception ex) { _settingsDirty = true; ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void FocusSelectedEditor(bool fourPane)
    {
        if (fourPane)
        {
            DraftText.Focus();
            Keyboard.Focus(DraftText);
            return;
        }
        TextBox? target = FindVisualChildren<TextBox>(ChapterBlocksItems)
            .FirstOrDefault(input => !input.IsReadOnly && input.Tag is Guid id && id == _selectedId);
        if (target is not null)
        {
            target.Focus();
            Keyboard.Focus(target);
        }
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
            if (FindName("InspectorConsistencyStatusText") is TextBlock statusEmpty)
                statusEmpty.Text = "Chưa chọn segment.";
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

        CharacterConsistencySelection selectedRules = _service is not null
            ? _service.PreviewCharacterConsistency(segment.Id)
            : new CharacterConsistencyRuleSelector().Select(_characterProject, segment.SourceText);

        if (FindName("InspectorConsistencyStatusText") is TextBlock consistencyStatus)
        {
            if (!_characterProject.ConsistencyLockEnabled)
            {
                consistencyStatus.Text = "Khóa nhất quán Project đang tắt. Chỉ hiển thị hồ sơ khớp tên trong đoạn.";
            }
            else if (string.IsNullOrWhiteSpace(selectedRules.PromptBlock))
            {
                consistencyStatus.Text = "Khóa nhất quán bật · Không có quy tắc khóa nào được chọn cho đoạn này.";
            }
            else
            {
                consistencyStatus.Text = $"Khóa nhất quán bật · {selectedRules.NarrativeRuleCount} đại từ khóa · {selectedRules.DialogueRuleCount} quy tắc thoại" +
                    (selectedRules.OmittedRuleCount > 0 ? $" · {selectedRules.OmittedRuleCount} lược bỏ do giới hạn" : "");
            }
        }

        if (_characterProject.ConsistencyLockEnabled && !string.IsNullOrWhiteSpace(selectedRules.PromptBlock))
        {
            string[] ruleLines = selectedRules.PromptBlock
                .ReplaceLineEndings("\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(line => line.StartsWith("- CHARACTER ", StringComparison.Ordinal) ||
                               line.StartsWith("- DIALOGUE ", StringComparison.Ordinal))
                .Select(line => line[2..])
                .ToArray();
            InspectorCharacterItems.ItemsSource = ruleLines;
        }
        else
        {
            InspectorCharacterItems.ItemsSource = _characterProject.Profiles.Where(profile =>
                (!string.IsNullOrWhiteSpace(profile.ChineseName) && segment.SourceText.Contains(profile.ChineseName, StringComparison.Ordinal)) ||
                profile.Aliases.Any(alias => !string.IsNullOrWhiteSpace(alias) && segment.SourceText.Contains(alias, StringComparison.Ordinal)))
                .Select(profile => $"{profile.ChineseName}  →  {profile.VietnameseName}\n{profile.PrimaryNarrativePronoun}").ToArray();
        }
        FourPaneSelectionText.Text = $"Đoạn {segment.Order} · {StatusLabel(segment.Status)}";
    }

    private void UpdateShellChrome() => UpdateShellChrome(_service?.GetSnapshot());

    private void UpdateShellChrome(ChapterDocument? chapter)
    {
        ShellChapterText.Text = chapter is null ? "Chưa mở chương" : chapter.Title;
        ApplyTwoPaneEditorModeLayout();
        RefreshTranslationInspector(chapter?.Segments.SingleOrDefault(segment => segment.Id == _selectedId));
        RefreshShellQueueStatus();
        ApplyProjectSidebarVisibility();
    }

    private void RefreshShellQueueStatus()
    {
        ProjectTranslationProgress? progress = _projectTranslationCoordinator?.CurrentJob is null
            ? null : _projectTranslationCoordinator.GetProgress();
        bool resumable = progress?.Status is ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted;
        if (QueueResumeBanner is not null)
        {
            QueueResumeBanner.Visibility = resumable ? Visibility.Visible : Visibility.Collapsed;
            if (resumable && progress is not null)
                QueueResumeSummaryText.Text = $"Đã dịch {progress.CompletedChapters:N0}/{progress.TotalChapters:N0} chương. " +
                    $"Còn {Math.Max(0, progress.TotalChapters - progress.CompletedChapters - progress.SkippedChapters):N0} chương.";
        }
        if (_compactShell)
        {
            ShellQueueButton.Content = progress is null ? "Queue" : resumable ? "Tiếp tục dịch" :
                $"Queue {progress.CompletedChapters}/{progress.TotalChapters}";
            return;
        }
        ShellQueueButton.Content = progress is null ? "Hàng đợi · chưa chạy" : resumable ? "Hàng đợi · có thể tiếp tục" :
            $"Hàng đợi · {progress.CompletedChapters}/{progress.TotalChapters}" +
            (progress.FailedChapters > 0 ? $" · {progress.FailedChapters} lỗi" : "");
    }

    private void OnProjectTranslationProgressChanged(object? sender, EventArgs e)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(RefreshShellQueueStatus);
    }
}
