using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private double _panelPolicyWidth = 1600;
    private bool _applyingPanelPolicy;

    internal void ApplyPanelPolicy()
    {
        if (ProjectSidebar is null || TranslationInspector is null || SegmentNavigator is null)
            return;

        _applyingPanelPolicy = true;
        try
        {
            var context = new PanelContext(CurrentWorkspace, CurrentEditorMode, _sourceEditMode,
                _projectSession is not null, _selectedId is not null,
                _panelPolicyWidth > 0 ? _panelPolicyWidth : ActualWidth);
            ApplyPanelPresentation(PanelId.ProjectSidebar,
                PanelPolicy.Resolve(PanelId.ProjectSidebar, GetPanelPreference(PanelId.ProjectSidebar), context));
            ApplyPanelPresentation(PanelId.SegmentNavigator,
                PanelPolicy.Resolve(PanelId.SegmentNavigator, GetPanelPreference(PanelId.SegmentNavigator), context));
            ApplyPanelPresentation(PanelId.Inspector,
                PanelPolicy.Resolve(PanelId.Inspector, GetPanelPreference(PanelId.Inspector), context));
        }
        finally { _applyingPanelPolicy = false; }
    }

    private PanelPreference GetPanelPreference(PanelId panel)
    {
        EditorMode? mode = CurrentWorkspace == WorkspaceId.Editor ? CurrentEditorMode : null;
        string key = PanelPreferenceKey.Create(CurrentWorkspace, mode, panel);
        if (_workspaceLayout.PanelPreferences?.TryGetValue(key, out PanelPreference preference) == true)
            return preference;
        if (CurrentWorkspace == WorkspaceId.Editor && CurrentEditorMode == EditorMode.Translate)
        {
            if (panel == PanelId.ProjectSidebar && !_workspaceLayout.ProjectSidebarVisible)
                return PanelPreference.PinnedHidden;
            if (panel == PanelId.Inspector && !_workspaceLayout.InspectorVisible)
                return PanelPreference.PinnedHidden;
        }
        return PanelPreference.Auto;
    }

    private void SetPanelPreference(PanelId panel, PanelPreference preference)
    {
        _workspaceLayout.PanelPreferences ??= new Dictionary<string, PanelPreference>();
        EditorMode? mode = CurrentWorkspace == WorkspaceId.Editor ? CurrentEditorMode : null;
        _workspaceLayout.PanelPreferences[PanelPreferenceKey.Create(CurrentWorkspace, mode, panel)] = preference;
    }

    private async void TogglePanelPreference(PanelId panel)
    {
        PanelPresentation current = CurrentPanelPresentation(panel);
        if (current == PanelPresentation.NotApplicable)
            return;
        bool visible = IsPanelDockedVisible(panel) || PanelPopup(panel).IsOpen;
        SetPanelPreference(panel, visible ? PanelPreference.PinnedHidden : PanelPreference.PinnedShown);
        ApplyPanelPolicy();
        if (!visible && CurrentPanelPresentation(panel) == PanelPresentation.Flyout)
            OpenPanelFlyout(panel);
        await SaveWorkspaceLayoutAsync();
    }

    private PanelPresentation CurrentPanelPresentation(PanelId panel)
    {
        var context = new PanelContext(CurrentWorkspace, CurrentEditorMode, _sourceEditMode,
            _projectSession is not null, _selectedId is not null, _panelPolicyWidth);
        return PanelPolicy.Resolve(panel, GetPanelPreference(panel), context);
    }

    private bool IsPanelDockedVisible(PanelId panel) => PanelElement(panel).Visibility == Visibility.Visible &&
        PanelDockHost(panel).Content is not null;

    private void ApplyPanelPresentation(PanelId panel, PanelPresentation presentation)
    {
        FrameworkElement element = PanelElement(panel);
        Popup popup = PanelPopup(panel);
        bool applicable = presentation != PanelPresentation.NotApplicable;
        Button toggle = PanelToggle(panel);
        toggle.Visibility = applicable ? Visibility.Visible : Visibility.Collapsed;
        toggle.IsEnabled = applicable && (panel != PanelId.ProjectSidebar || _projectSession is not null);

        if (presentation != PanelPresentation.Flyout && popup.IsOpen)
            popup.IsOpen = false;
        if (!popup.IsOpen)
            RestorePanelToDock(panel);

        bool docked = presentation == PanelPresentation.Docked;
        element.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        PanelSplitter(panel).Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
        if (panel == PanelId.SegmentNavigator)
            SegmentNavigatorColumn.Width = docked
                ? new GridLength(Math.Clamp(_workspaceLayout.SegmentNavigatorWidth, 180, 520))
                : new GridLength(0);
        UpdatePanelToggle(panel, presentation, popup.IsOpen);
    }

    private void UpdatePanelToggle(PanelId panel, PanelPresentation presentation, bool flyoutOpen)
    {
        bool visible = presentation == PanelPresentation.Docked || flyoutOpen;
        string label = panel switch
        {
            PanelId.ProjectSidebar => _compactShell ? "Mục lục" : visible ? "Ẩn mục lục" : "Hiện mục lục",
            PanelId.Inspector => _compactShell ? "Inspector" : visible ? "Ẩn Inspector" : "Hiện Inspector",
            _ => _compactShell ? "Đoạn" : visible ? "Ẩn danh sách đoạn" : "Hiện danh sách đoạn"
        };
        PanelToggle(panel).Content = label;
    }

    private void OpenPanelFlyout(PanelId panel)
    {
        if (CurrentPanelPresentation(panel) != PanelPresentation.Flyout)
            return;
        CloseOtherPanelFlyouts(panel);
        FrameworkElement element = PanelElement(panel);
        ContentControl dock = PanelDockHost(panel);
        ContentControl flyout = PanelFlyoutHost(panel);
        dock.Content = null;
        flyout.Content = element;
        element.Visibility = Visibility.Visible;
        Popup popup = PanelPopup(panel);
        double width = panel switch
        {
            PanelId.ProjectSidebar => Math.Clamp(_workspaceLayout.ProjectSidebarWidth, 220, 520),
            PanelId.Inspector => Math.Clamp(_workspaceLayout.InspectorWidth, 240, 600),
            _ => Math.Clamp(_workspaceLayout.SegmentNavigatorWidth, 180, 520)
        };
        Border border = PanelFlyoutBorder(panel);
        border.Width = width;
        border.MaxHeight = Math.Max(240, ActualHeight - GlobalToolbar.ActualHeight - 40);
        popup.VerticalOffset = GlobalToolbar.ActualHeight;
        popup.HorizontalOffset = panel == PanelId.Inspector ? Math.Max(0, ActualWidth - width) : 0;
        popup.IsOpen = true;
        UpdatePanelToggle(panel, PanelPresentation.Flyout, flyoutOpen: true);
        _pendingEditorFocus = false;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            border.Focus();
            Keyboard.Focus(border);
        });
    }

    private void CloseOtherPanelFlyouts(PanelId keep)
    {
        foreach (PanelId panel in Enum.GetValues<PanelId>())
            if (panel != keep) PanelPopup(panel).IsOpen = false;
    }

    private void RestorePanelToDock(PanelId panel)
    {
        ContentControl flyout = PanelFlyoutHost(panel);
        ContentControl dock = PanelDockHost(panel);
        FrameworkElement element = PanelElement(panel);
        if (flyout.Content is not null) flyout.Content = null;
        if (dock.Content is null) dock.Content = element;
    }

    private void OnToggleSegmentNavigator(object sender, RoutedEventArgs e) =>
        TogglePanelPreference(PanelId.SegmentNavigator);

    private async void OnPanelPreferenceMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag }) return;
        string[] parts = tag.Split('.');
        if (parts.Length != 2 || !Enum.TryParse(parts[0], out PanelId panel) ||
            !Enum.TryParse(parts[1], out PanelPreference preference)) return;
        SetPanelPreference(panel, preference);
        ApplyPanelPolicy();
        await SaveWorkspaceLayoutAsync();
    }

    private void OnPanelFlyoutPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        foreach (PanelId panel in Enum.GetValues<PanelId>())
            if (PanelPopup(panel).IsOpen) PanelPopup(panel).IsOpen = false;
        e.Handled = true;
    }

    private void OnPanelFlyoutClosed(object sender, EventArgs e)
    {
        PanelId panel = sender == ProjectSidebarFlyout ? PanelId.ProjectSidebar :
            sender == InspectorFlyout ? PanelId.Inspector : PanelId.SegmentNavigator;
        RestorePanelToDock(panel);
        PanelElement(panel).Visibility = Visibility.Collapsed;
        UpdatePanelToggle(panel, CurrentPanelPresentation(panel), flyoutOpen: false);
        if (!_applyingPanelPolicy && !_closing)
        {
            PanelToggle(panel).Focus();
            Keyboard.Focus(PanelToggle(panel));
        }
    }

    private void OnWindowGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_pendingEditorFocus || _switchingEditorMode || e.NewFocus is not DependencyObject target) return;
        if (!IsDescendantOf(target, EditorWorkspaceRoot)) _pendingEditorFocus = false;
    }

    private static bool IsDescendantOf(DependencyObject target, DependencyObject ancestor)
    {
        for (DependencyObject? current = target; current is not null; current =
            current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private FrameworkElement PanelElement(PanelId panel) => panel switch
    {
        PanelId.ProjectSidebar => ProjectSidebar,
        PanelId.SegmentNavigator => SegmentNavigator,
        _ => TranslationInspector
    };
    private ContentControl PanelDockHost(PanelId panel) => panel switch
    {
        PanelId.ProjectSidebar => ProjectSidebarDockHost,
        PanelId.SegmentNavigator => SegmentNavigatorDockHost,
        _ => InspectorDockHost
    };
    private ContentControl PanelFlyoutHost(PanelId panel) => panel switch
    {
        PanelId.ProjectSidebar => ProjectSidebarFlyoutHost,
        PanelId.SegmentNavigator => SegmentNavigatorFlyoutHost,
        _ => InspectorFlyoutHost
    };
    private Popup PanelPopup(PanelId panel) => panel switch
    {
        PanelId.ProjectSidebar => ProjectSidebarFlyout,
        PanelId.SegmentNavigator => SegmentNavigatorFlyout,
        _ => InspectorFlyout
    };
    private Border PanelFlyoutBorder(PanelId panel) => panel switch
    {
        PanelId.ProjectSidebar => ProjectSidebarFlyoutBorder,
        PanelId.SegmentNavigator => SegmentNavigatorFlyoutBorder,
        _ => InspectorFlyoutBorder
    };
    private FrameworkElement PanelSplitter(PanelId panel) => panel switch
    {
        PanelId.ProjectSidebar => ProjectSidebarSplitter,
        PanelId.SegmentNavigator => SegmentNavigatorSplitter,
        _ => InspectorSplitter
    };
    private Button PanelToggle(PanelId panel) => panel switch
    {
        PanelId.ProjectSidebar => ProjectSidebarToggleButton,
        PanelId.SegmentNavigator => SegmentNavigatorToggleButton,
        _ => InspectorToggleButton
    };
}
