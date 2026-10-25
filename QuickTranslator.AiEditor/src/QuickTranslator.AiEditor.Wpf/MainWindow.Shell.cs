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
    private bool _compactShell;
    private bool _navRailCollapsed;
    private bool _switchingEditorMode;
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
        ApplyPanelPolicy();
        ApplyTwoPaneEditorModeLayout();
        TwoPaneModeButton.IsChecked = !_workspaceLayout.FourPaneMode;
        FourPaneModeButton.IsChecked = _workspaceLayout.FourPaneMode;
        ApplyBilingualSourceViewSelection();
        RefreshBilingualSourceDisplay();
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

    private void OnToggleInspector(object sender, RoutedEventArgs e) =>
        TogglePanelPreference(PanelId.Inspector);

    private void OnToggleProjectSidebar(object sender, RoutedEventArgs e) =>
        TogglePanelPreference(PanelId.ProjectSidebar);

    // The rail also holds the shell tools (queue, panels, theme, save), so it is wide enough for their labels.
    private double NavRailWidth => _navRailCollapsed ? 56 : (_compactShell ? 160 : 184);

    private void OnToggleNavRail(object sender, RoutedEventArgs e)
    {
        _navRailCollapsed = !_navRailCollapsed;
        ShellNavigation.Width = NavRailWidth;
        if (FindName("NavRailToggleButton") is Button btn)
            btn.Content = _navRailCollapsed ? "»" : "«";
    }

    private void OnSwitchLeftPanelToChapters(object sender, RoutedEventArgs e)
    {
        SetPanelPreference(PanelId.ProjectSidebar, PanelPreference.PinnedShown);
        ApplyPanelPolicy();
    }

    private void OnSwitchLeftPanelToSegments(object sender, RoutedEventArgs e)
    {
        if (EditorTabs.SelectedIndex == 1)
        {
            SetPanelPreference(PanelId.ProjectSidebar, PanelPreference.PinnedHidden);
            ApplyPanelPolicy();
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
        _panelPolicyWidth = width;
        _compactShell = width > 0 && width < 1180;
        ShellNavigation.Width = NavRailWidth;
        BrandBlock.Width = _compactShell ? 162 : 194;
        ProjectSwitcher.Width = _compactShell ? 190 : 300;
        TitleInput.Width = _compactShell ? 250 : 360;
        ShellChapterText.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        UpdateChapterModeHint();
        ChapterSummary.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        ReaderBookmarkButton.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        FocusModeButton.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        ReaderFullScreenButton.Visibility = _compactShell ? Visibility.Collapsed : Visibility.Visible;
        ApplyPanelPolicy();
        ApplyReaderCatalogVisibility();
        RefreshShellQueueStatus();
    }

    private void ApplyProjectSidebarVisibility()
    {
        ApplyPanelPolicy();
    }

    // The one-line mode hint only helps the reader; in the editor it took a row above the grid.
    private void UpdateChapterModeHint()
    {
        if (ChapterModeHint is null || ReaderView is null) return;
        ChapterModeHint.Visibility = !_compactShell && ReaderView.Visibility == Visibility.Visible
            ? Visibility.Visible : Visibility.Collapsed;
    }

    // BL-14: In Two-Pane mode, show Aligned Bilingual Segment Grid when a chapter is segmented and not in _sourceEditMode;
    // show monolithic ChapterInput when _service is null or _sourceEditMode == true.
    internal void ApplyTwoPaneEditorModeLayout()
    {
        if (ChapterTwoPaneLeftColumn is null || ChapterTwoPaneSplitter is null) return;
        bool showRawSourceBox = _service is null || _sourceEditMode;
        // The title / paste / import / split strip only serves a chapter that is not segmented yet; an open chapter
        // gives its height to the bilingual grid (its tools sit under "Thêm" in the grid header).
        ChapterInitStrip.Visibility = showRawSourceBox ? Visibility.Visible : Visibility.Collapsed;
        UpdateChapterModeHint();
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
        ApplyPanelPolicy();
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

    private void OnShellNavigate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string target }) return;
        switch (target)
        {
            case "Translate": NavigateTo(WorkspaceId.Editor, CurrentEditorMode); break;
            case "Chapters": NavigateTo(WorkspaceId.Chapters); break;
            case "Queue": NavigateTo(WorkspaceId.Queue); break;
            case "Publish": NavigateTo(WorkspaceId.Publish); break;
            case "Reader": NavigateTo(WorkspaceId.Reader); break;
            case "Resources": NavigateTo(WorkspaceId.Resources); break;
            case "Settings": NavigateTo(WorkspaceId.Settings); break;
        }
    }

    private void ShowTranslationWorkspace(bool fourPane)
    {
        EditorMode targetMode = fourPane ? EditorMode.Review : EditorMode.Translate;
        if (CurrentWorkspace != WorkspaceId.Editor || CurrentEditorMode != targetMode)
            _pendingEditorFocus = true;
        _switchingEditorMode = true;
        try
        {
            CurrentWorkspace = WorkspaceId.Editor;
            CurrentEditorMode = targetMode;
            ApplyWorkspaceVisibility(CurrentWorkspace);
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
            ApplyPanelPolicy();
            Refresh();
        }
        catch
        {
            _switchingEditorMode = false;
            throw;
        }
        if (_pendingEditorFocus) FocusSelectedEditor(fourPane);
        if (_pendingEditorFocus) _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            try
            {
                ChapterBlocksItems.ApplyTemplate();
                ChapterBlocksItems.UpdateLayout();
                if (_pendingEditorFocus) FocusSelectedEditor(fourPane);
            }
            finally
            {
                _pendingEditorFocus = false;
                _switchingEditorMode = false;
            }
        });
        else _switchingEditorMode = false;
        OnReaderModeChanged(this, new RoutedEventArgs());
        _ = SaveWorkspaceLayoutAsync();
        MarkActiveNavigation(WorkspaceId.Editor);
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

    // A command chosen from any "Thêm" popup closes it; Esc closes them too.
    private void CloseCommandOverflows()
    {
        foreach (var toggle in new[] { CommandOverflowToggle, StructureOverflowToggle, GlossaryOverflowToggle, CharactersOverflowToggle })
            toggle.IsChecked = false;
    }

    private void OnCommandOverflowClick(object sender, RoutedEventArgs e) => CloseCommandOverflows();

    private void OnPopupContentPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseCommandOverflows();
        ReaderAppearancePopup.IsOpen = false;
        e.Handled = true;
    }

    private void MarkActiveNavigation(WorkspaceId workspace)
    {
        var routes = new Dictionary<WorkspaceId, Button>
        {
            [WorkspaceId.Editor] = TranslateNavButton,
            [WorkspaceId.Chapters] = ChaptersNavButton,
            [WorkspaceId.Queue] = QueueNavButton,
            [WorkspaceId.Publish] = PublishNavButton,
            [WorkspaceId.Reader] = ReaderNavButton,
            [WorkspaceId.Resources] = ResourcesNavButton,
            [WorkspaceId.Settings] = SettingsNavButton
        };
        foreach ((WorkspaceId id, Button button) in routes)
            NavState.SetIsActive(button, id == workspace || id == WorkspaceId.Editor && workspace == WorkspaceId.Reader);
    }

    private void ApplyWorkspaceVisibility(WorkspaceId workspace)
    {
        bool editorHost = workspace is WorkspaceId.Editor or WorkspaceId.Reader;
        EditorWorkspaceRoot.Visibility = editorHost ? Visibility.Visible : Visibility.Collapsed;
        ChaptersWorkspaceRoot.Visibility = workspace == WorkspaceId.Chapters ? Visibility.Visible : Visibility.Collapsed;
        QueueWorkspaceRoot.Visibility = workspace == WorkspaceId.Queue ? Visibility.Visible : Visibility.Collapsed;
        PublishWorkspaceRoot.Visibility = workspace == WorkspaceId.Publish ? Visibility.Visible : Visibility.Collapsed;
        ResourcesWorkspaceRoot.Visibility = workspace == WorkspaceId.Resources ? Visibility.Visible : Visibility.Collapsed;
        SettingsWorkspaceRoot.Visibility = workspace == WorkspaceId.Settings ? Visibility.Visible : Visibility.Collapsed;
        EditorContextHeader.Visibility = workspace == WorkspaceId.Editor ? Visibility.Visible : Visibility.Collapsed;
        ChapterModeBar.Visibility = editorHost ? Visibility.Visible : Visibility.Collapsed;
        if (workspace == WorkspaceId.Chapters) _hostedChapterManagerView?.Attach(); else _hostedChapterManagerView?.Detach();
        if (workspace == WorkspaceId.Queue) _hostedQueueView?.Attach(); else _hostedQueueView?.Detach();
        if (workspace == WorkspaceId.Publish) _hostedPublicationView?.Attach(); else _hostedPublicationView?.Detach();
        ChaptersWorkspaceEmpty.Visibility = _hostedChapterManagerView is null ? Visibility.Visible : Visibility.Collapsed;
        PublishWorkspaceEmpty.Visibility = _hostedPublicationView is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateQueueWorkspaceEmptyState();
        _ = RefreshStartScreenAsync();
        ApplyPanelPolicy();
    }

    private void ShowReaderWorkspace()
    {
        CurrentWorkspace = WorkspaceId.Reader;
        ApplyWorkspaceVisibility(CurrentWorkspace);
        EditorTabs.SelectedIndex = 0;
        ReaderModeButton.IsChecked = true;
        ApplyPanelPolicy();
        ChapterModeHint.Text = "Đọc bản dịch · chọn đoạn để mở đúng vị trí trong Editor";
        MarkActiveNavigation(WorkspaceId.Reader);
    }

    private void ShowSettingsWorkspace(string? section = null)
    {
        if (section is not null && section.Equals("Dictionary", StringComparison.OrdinalIgnoreCase) ||
            section is not null && section.Equals("Characters", StringComparison.OrdinalIgnoreCase) ||
            section is not null && section.Equals("Prompts", StringComparison.OrdinalIgnoreCase))
        {
            ShowResourcesWorkspace(section);
            return;
        }
        CurrentWorkspace = WorkspaceId.Settings;
        ApplyWorkspaceVisibility(CurrentWorkspace);
        MarkActiveNavigation(WorkspaceId.Settings);
        if (section is not null)
            SettingsNavigation.SelectedItem = SettingsNavigation.Items.OfType<ListBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, section, StringComparison.OrdinalIgnoreCase));
    }

    private void ShowResourcesWorkspace(string? section = null)
    {
        CurrentWorkspace = WorkspaceId.Resources;
        ApplyWorkspaceVisibility(CurrentWorkspace);
        MarkActiveNavigation(WorkspaceId.Resources);
        string target = section ?? "Characters";
        ResourcesNavigation.SelectedItem = ResourcesNavigation.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, target, StringComparison.OrdinalIgnoreCase));
        ApplyResourcesSelection();
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
                ShowResourcesWorkspace("Characters");
                break;
            case "dictionary":
                ShowResourcesWorkspace("Dictionary");
                break;
            case "prompts":
                ShowResourcesWorkspace("Prompts");
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

    private bool _shellQueueResumable;

    // "Hàng đợi / tiếp tục dịch" replaces the old resume banner: while the queue is paused or interrupted the button resumes it.
    private async void OnOpenQueueStatus(object sender, RoutedEventArgs e)
    {
        if (_shellQueueResumable) OnResumeProjectTranslationFromShell(sender, e);
        else await OpenQueueStatusAsync();
    }

    private async void OnShowQueueDetails(object sender, RoutedEventArgs e)
    {
        try { await OpenQueueStatusAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnQueueMiniToggle(object sender, RoutedEventArgs e)
    {
        try
        {
            ProjectTranslationCoordinator? coordinator = _projectTranslationCoordinator;
            if (coordinator?.CurrentJob is null) return;
            if (coordinator.IsRunning)
            {
                QueueMiniActionButton.IsEnabled = false;
                await coordinator.PauseAsync();
                RefreshShellQueueStatus();
            }
            else if (coordinator.CurrentJob.Status is ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted)
                OnResumeProjectTranslationFromShell(sender, e);
            else await OpenQueueStatusAsync();
        }
        catch (Exception ex) { ShowError(ex); }
        finally { QueueMiniActionButton.IsEnabled = true; }
    }

    private async Task OpenQueueStatusAsync()
    {
        if (_projectSession is null) { NavigateTo(WorkspaceId.Queue); return; }
        ProjectTranslationCoordinator coordinator = await EnsureProjectTranslationCoordinatorAsync();
        if (coordinator.CurrentJob is null)
        {
            NavigateTo(WorkspaceId.Queue);
            return;
        }
        HostProjectTranslationView(coordinator, null);
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
            HostProjectTranslationView(coordinator, null);
            if (_hostedQueueView is not null)
                await _hostedQueueView.ResumeNowAsync();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private Task OpenProjectPublicationAsync() => ShowPublicationWorkspaceAsync();

    private void OnOpenInspectorSettings(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section }) ShowResourcesWorkspace(section);
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
        catch (Exception ex) { _resourcesDirty = true; ShowError(ex); }
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
        catch (Exception ex) { _resourcesDirty = true; ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void FocusSelectedEditor(bool fourPane)
    {
        if (fourPane)
        {
            DraftText.Focus();
            Keyboard.Focus(DraftText);
            if (DraftText.IsKeyboardFocusWithin) _pendingEditorFocus = false;
            return;
        }
        TextBox? target = FindVisualChildren<TextBox>(ChapterBlocksItems)
            .FirstOrDefault(input => !input.IsReadOnly && input.Tag is Guid id && id == _selectedId);
        if (target is not null)
        {
            target.Focus();
            Keyboard.Focus(target);
            if (target.IsKeyboardFocusWithin) _pendingEditorFocus = false;
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
            ApplyPanelPolicy();
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
        ApplyPanelPolicy();
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
        _shellQueueResumable = resumable;
        // Compact progress follows the queue across all workspaces, including Reader.
        bool showQueue = progress is not null && progress.Status is
            (ProjectTranslationJobStatus.Ready or ProjectTranslationJobStatus.Running or
             ProjectTranslationJobStatus.PauseRequested or ProjectTranslationJobStatus.Paused or
             ProjectTranslationJobStatus.Interrupted or ProjectTranslationJobStatus.Cancelling or
             ProjectTranslationJobStatus.CompletedWithErrors);
        QueueMiniBar.Visibility = showQueue ? Visibility.Visible : Visibility.Collapsed;
        if (showQueue && progress is not null)
        {
            string state = progress.Status switch
            {
                ProjectTranslationJobStatus.Ready => "Sẵn sàng",
                ProjectTranslationJobStatus.Running => "Đang dịch",
                ProjectTranslationJobStatus.PauseRequested => "Đang tạm dừng",
                ProjectTranslationJobStatus.Paused => "Đã tạm dừng",
                ProjectTranslationJobStatus.Interrupted => "Chờ tiếp tục",
                ProjectTranslationJobStatus.Cancelling => "Đang dừng",
                ProjectTranslationJobStatus.CompletedWithErrors => "Hoàn tất có lỗi",
                _ => "Hàng đợi"
            };
            QueueMiniSummary.Text = $"{state} · {progress.CompletedChapters:N0}/{progress.TotalChapters:N0} chương" +
                (progress.FailedChapters > 0 ? $" · {progress.FailedChapters} lỗi" : "");
            int finished = progress.CompletedChapters + progress.FailedChapters + progress.SkippedChapters;
            QueueMiniProgress.Value = progress.TotalChapters == 0 ? 0 :
                Math.Clamp(100d * finished / progress.TotalChapters, 0, 100);
            QueueMiniActionButton.Content = resumable ? "Tiếp tục" : "Tạm dừng";
            QueueMiniActionButton.Visibility = resumable || progress.Status == ProjectTranslationJobStatus.Running
                ? Visibility.Visible : Visibility.Collapsed;
            QueueMiniActionButton.IsEnabled = resumable || progress.Status == ProjectTranslationJobStatus.Running;
            QueueMiniBar.ToolTip = progress.CurrentChapterTitle is null ? null :
                $"Đang xử lý: {progress.CurrentChapterTitle}";
        }
        ShellQueueButton.ToolTip = resumable && progress is not null
            ? $"Đã dịch {progress.CompletedChapters:N0}/{progress.TotalChapters:N0} chương. " +
              $"Còn {Math.Max(0, progress.TotalChapters - progress.CompletedChapters - progress.SkippedChapters):N0} chương. Bấm để tiếp tục dịch."
            : "Mở Hàng đợi";
        if (_compactShell)
        {
            ShellQueueButton.Content = progress is null ? "Hàng đợi" : resumable ? "Tiếp tục dịch" :
                $"Hàng đợi {progress.CompletedChapters}/{progress.TotalChapters}";
            return;
        }
        ShellQueueButton.Content = progress is null ? "Hàng đợi · chưa chạy" : resumable ? "Hàng đợi / tiếp tục dịch" :
            $"Hàng đợi · {progress.CompletedChapters}/{progress.TotalChapters}" +
            (progress.FailedChapters > 0 ? $" · {progress.FailedChapters} lỗi" : "");
    }

    private void OnProjectTranslationProgressChanged(object? sender, EventArgs e)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(RefreshShellQueueStatus);
    }
}
