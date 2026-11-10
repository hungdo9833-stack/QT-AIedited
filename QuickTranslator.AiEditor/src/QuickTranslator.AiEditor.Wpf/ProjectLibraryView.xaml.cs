using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ProjectLibraryView : UserControl
{
    private List<ProjectLibraryCard> _projects = [];
    private bool _attached;
    private bool _startScreenMode;

    internal ProjectLibraryView(IReadOnlyList<ProjectLibraryCard> projects, Guid? activeProjectId)
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SetProjects(projects, activeProjectId);
    }

    internal event EventHandler<ProjectLibraryRequest>? ActionRequested;

    internal bool StartScreenMode
    {
        get => _startScreenMode;
        set
        {
            if (_startScreenMode == value) return;
            _startScreenMode = value;
            UpdateHeaderMode();
            UpdateButtons();
            QuickTranslateButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    internal void Attach()
    {
        if (_attached) return;
        _attached = true;
    }

    internal void Detach()
    {
        if (!_attached) return;
        _attached = false;
    }

    /// <summary>Every project the view holds now (filters aside).</summary>
    internal IReadOnlyList<ProjectLibraryCard> Projects => _projects.ToArray();

    /// <summary>The projects currently selected, in the order they are shown.</summary>
    internal IReadOnlyList<ProjectLibraryCard> SelectedProjects =>
        ProjectList.Items.Cast<ProjectLibraryCard>().Where(card => ProjectList.SelectedItems.Contains(card)).ToArray();

    internal void SetProjects(IReadOnlyList<ProjectLibraryCard> projects, Guid? activeProjectId)
    {
        _projects = (projects ?? throw new ArgumentNullException(nameof(projects))).ToList();
        HashSet<Guid> keep = SelectedProjects.Select(card => card.ProjectId).ToHashSet();
        if (keep.Count == 0 && (activeProjectId ?? _projects.FirstOrDefault()?.ProjectId) is Guid first) keep.Add(first);
        RefreshItems(keep);
        UpdateButtons();
    }

    /// <summary>Takes removed or deleted projects off the list in place: the window stays where it was.</summary>
    internal void RemoveProjects(IReadOnlyCollection<Guid> projectIds, string status)
    {
        int index = ProjectList.SelectedIndex;
        _projects.RemoveAll(card => projectIds.Contains(card.ProjectId));
        RefreshItems(new HashSet<Guid>());
        // Select the card that took the place of the first removed one, so Delete can be pressed again.
        if (ProjectList.Items.Count > 0) ProjectList.SelectedIndex = Math.Clamp(index, 0, ProjectList.Items.Count - 1);
        ShowStatus(status);
        UpdateButtons();
    }

    /// <summary>Replaces one card (a project found again at a new place) and keeps it selected.</summary>
    internal void ReplaceProject(ProjectLibraryCard card, string status)
    {
        int index = _projects.FindIndex(item => item.ProjectId == card.ProjectId);
        if (index >= 0) _projects[index] = card; else _projects.Add(card);
        RefreshItems(new HashSet<Guid> { card.ProjectId });
        ShowStatus(status);
        UpdateButtons();
    }

    internal void ShowStatus(string? status)
    {
        ActionStatusText.Text = status ?? "";
        ActionStatusText.Visibility = string.IsNullOrWhiteSpace(status) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Attach();
    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (ProjectList is not null) RefreshItems(SelectedProjects.Select(card => card.ProjectId).ToHashSet());
    }

    private void RefreshItems(IReadOnlySet<Guid> select)
    {
        if (ProjectList is null) return;
        string query = SearchInput?.Text.Trim() ?? "";
        string status = (StatusFilter?.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        string sort = (SortInput?.SelectedItem as ComboBoxItem)?.Tag as string ?? "Recent";
        IEnumerable<ProjectLibraryCard> filtered = _projects.Where(project =>
            (query.Length == 0 || project.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             project.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             project.ManifestPath.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (status == "All" || status == "Missing" && project.Missing || status == "Web" && project.IsOnWeb ||
             !project.Missing && project.Status?.ToString() == status));
        ProjectLibraryCard[] shown = (sort switch
        {
            "Title" => filtered.OrderBy(project => project.Title, StringComparer.CurrentCultureIgnoreCase),
            "Progress" => filtered.OrderByDescending(project => project.Progress).ThenByDescending(project => project.LastOpenedAt),
            "Chapters" => filtered.OrderByDescending(project => project.ChapterCount).ThenByDescending(project => project.LastOpenedAt),
            _ => filtered.OrderByDescending(project => project.LastOpenedAt)
        }).ToArray();
        ProjectList.ItemsSource = shown;
        foreach (ProjectLibraryCard card in shown.Where(card => select.Contains(card.ProjectId))) ProjectList.SelectedItems.Add(card);
        if (ProjectList.SelectedItem is { } first) ProjectList.ScrollIntoView(first);
        CountText.Text = shown.Length == _projects.Count ? $"{_projects.Count} truyện" : $"{shown.Length}/{_projects.Count} truyện";
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateHeaderMode()
    {
        HeaderTitleText.Text = _startScreenMode ? "Bắt đầu" : "Thư viện truyện";
        HeaderSubtitleText.Text = _startScreenMode
            ? "Mở dự án gần đây, tạo dự án mới hoặc tìm lại vị trí dự án. Giữ Ctrl hoặc Shift khi nhấp để chọn nhiều."
            : "Mở nhanh dự án, xem tiến độ và tiếp tục biên dịch. Giữ Ctrl hoặc Shift khi nhấp để chọn nhiều.";
    }

    private void UpdateButtons()
    {
        if (ContinueButton is null) return;
        IReadOnlyList<ProjectLibraryCard> selected = SelectedProjects;
        ProjectLibraryCard? one = selected.Count == 1 ? selected[0] : null;
        ContinueButton.IsEnabled = one is { Missing: false };
        LocateButton.IsEnabled = one is not null;
        RemoveButton.IsEnabled = selected.Count > 0;
        DeleteButton.IsEnabled = selected.Any(card => !card.Missing);
        SelectionHint.Text = selected.Count switch
        {
            0 => "Chọn một dự án để tiếp tục. Giữ Ctrl hoặc Shift khi nhấp để chọn nhiều.",
            1 => one!.ManifestPath,
            _ => $"Đã chọn {selected.Count} dự án: {string.Join(", ", selected.Take(3).Select(card => card.Title))}" +
                 (selected.Count > 3 ? $" và {selected.Count - 3} dự án khác" : "")
        };
        DeleteButton.Content = selected.Count > 1 ? $"Thùng rác ({selected.Count})" : "Thùng rác";
        RemoveButton.Content = selected.Count > 1 ? $"Bỏ liên kết ({selected.Count})" : "Bỏ liên kết";

        Style primary = (Style)FindResource("PrimaryButton");
        if (_startScreenMode && _projects.Count == 0)
        {
            CreateButton.Visibility = Visibility.Visible;
            CreateButton.Style = primary;
            ContinueButton.Visibility = Visibility.Collapsed;
            // ClearValue, not null: a local null Style also blocks the app's implicit Button style (system chrome, no glyph).
            ContinueButton.ClearValue(StyleProperty);
        }
        else
        {
            CreateButton.Visibility = Visibility.Visible;
            CreateButton.ClearValue(StyleProperty);
            ContinueButton.Visibility = Visibility.Visible;
            ContinueButton.Style = primary;
            ContinueButton.Content = _startScreenMode ? "Tiếp tục" : "Tiếp tục biên dịch";
        }
    }

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<ProjectLibraryCard> selected = SelectedProjects;
        MenuContinue.IsEnabled = ContinueButton.IsEnabled;
        MenuLocate.IsEnabled = LocateButton.IsEnabled;
        MenuOpenFolder.IsEnabled = selected.Count == 1 && !selected[0].Missing;
        MenuRemove.IsEnabled = RemoveButton.IsEnabled;
        MenuDelete.IsEnabled = DeleteButton.IsEnabled;
        MenuRemove.Header = selected.Count > 1 ? $"Bỏ liên kết {selected.Count} dự án khỏi thư viện" : "Bỏ liên kết khỏi thư viện";
        MenuDelete.Header = selected.Count > 1 ? $"Chuyển {selected.Count} dự án vào Thùng rác" : "Chuyển vào Thùng rác";
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && DeleteButton.IsEnabled) { e.Handled = true; OnDelete(sender, e); }
        else if (e.Key == Key.Enter && ContinueButton.IsEnabled) { e.Handled = true; OnContinue(sender, e); }
    }

    private void Request(ProjectLibraryAction action, bool requiresSelection)
    {
        IReadOnlyList<ProjectLibraryCard> selected = SelectedProjects;
        if (requiresSelection && selected.Count == 0) return;
        ShowStatus(null);
        ActionRequested?.Invoke(this, new ProjectLibraryRequest(action, selected.FirstOrDefault(), selected));
    }

    // A right-click on a card that is not selected acts on that card alone, as in Explorer.
    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ProjectList, e.OriginalSource as DependencyObject) is ListBoxItem { IsSelected: false } item)
        {
            ProjectList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        base.OnPreviewMouseRightButtonDown(e);
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) => ProjectList.SelectAll();
    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (SelectedProjects.Count == 1) Request(ProjectLibraryAction.Continue, true);
    }
    private void OnCreate(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Create, false);
    private void OnLocate(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Locate, true);
    private void OnOpenFolder(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.OpenFolder, true);
    private void OnRemove(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Remove, true);
    private void OnDelete(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Delete, true);
    private void OnQuickTranslate(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.QuickTranslate, false);
}
