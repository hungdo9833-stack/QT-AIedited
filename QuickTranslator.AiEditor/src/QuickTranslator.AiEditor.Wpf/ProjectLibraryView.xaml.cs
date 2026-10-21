using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ProjectLibraryView : UserControl
{
    private IReadOnlyList<ProjectLibraryCard> _projects = [];
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

    internal void SetProjects(IReadOnlyList<ProjectLibraryCard> projects, Guid? activeProjectId)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        RefreshItems();
        ProjectList.SelectedItem = _projects.FirstOrDefault(project => project.ProjectId == activeProjectId)
            ?? _projects.FirstOrDefault();
        UpdateButtons();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Attach();
    private void OnUnloaded(object sender, RoutedEventArgs e) => Detach();
    private void OnFilterChanged(object sender, RoutedEventArgs e) { if (ProjectList is not null) RefreshItems(); }

    private void RefreshItems()
    {
        if (ProjectList is null) return;
        string query = SearchInput?.Text.Trim() ?? "";
        string status = (StatusFilter?.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        ProjectLibraryCard? selected = ProjectList.SelectedItem as ProjectLibraryCard;
        ProjectLibraryCard[] filtered = _projects.Where(project =>
            (query.Length == 0 || project.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             project.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             project.ManifestPath.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (status == "All" || status == "Missing" && project.Missing || !project.Missing && project.Status?.ToString() == status)).ToArray();
        ProjectList.ItemsSource = filtered;
        if (selected is not null) ProjectList.SelectedItem = filtered.FirstOrDefault(project => project.ProjectId == selected.ProjectId);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateHeaderMode()
    {
        HeaderTitleText.Text = _startScreenMode ? "Bắt đầu" : "Thư viện truyện";
        HeaderSubtitleText.Text = _startScreenMode
            ? "Mở Project gần đây, tạo Project mới hoặc tìm lại vị trí Project."
            : "Mở nhanh Project, xem tiến độ và tiếp tục biên dịch";
    }

    private void UpdateButtons()
    {
        if (ContinueButton is null) return;
        ProjectLibraryCard? selected = ProjectList.SelectedItem as ProjectLibraryCard;
        bool has = selected is not null;
        ContinueButton.IsEnabled = has && !selected!.Missing;
        LocateButton.IsEnabled = has;
        RemoveButton.IsEnabled = has;
        DeleteButton.IsEnabled = has && !selected!.Missing;
        SelectionHint.Text = selected?.ManifestPath ?? "Chọn một Project để tiếp tục.";

        Style primary = (Style)FindResource("PrimaryButton");
        if (_startScreenMode && _projects.Count == 0)
        {
            CreateButton.Visibility = Visibility.Visible;
            CreateButton.Style = primary;
            ContinueButton.Visibility = Visibility.Collapsed;
            ContinueButton.Style = null;
        }
        else
        {
            CreateButton.Visibility = Visibility.Visible;
            CreateButton.Style = null;
            ContinueButton.Visibility = Visibility.Visible;
            ContinueButton.Style = primary;
            ContinueButton.Content = _startScreenMode ? "Tiếp tục" : "Tiếp tục biên dịch";
        }
    }

    private void Request(ProjectLibraryAction action, bool requiresSelection)
    {
        ProjectLibraryCard? selected = ProjectList.SelectedItem as ProjectLibraryCard;
        if (requiresSelection && selected is null) return;
        ActionRequested?.Invoke(this, new ProjectLibraryRequest(action, selected));
    }

    private void OnContinue(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Continue, true);
    private void OnCreate(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Create, false);
    private void OnLocate(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Locate, true);
    private void OnRemove(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Remove, true);
    private void OnDelete(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Delete, true);
}
