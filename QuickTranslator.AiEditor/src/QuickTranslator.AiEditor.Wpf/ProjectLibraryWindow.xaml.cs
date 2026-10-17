using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

internal enum ProjectLibraryAction { Continue, Create, Locate, Remove, Delete }

internal sealed record ProjectLibraryCard(Guid ProjectId, string Title, string Author, ProjectStatus? Status,
    string ManifestPath, string? CoverPath, int ChapterCount, int CompletedChapterCount, double Progress,
    string? CurrentChapter, DateTimeOffset LastOpenedAt, bool Missing, string? Error)
{
    public double ProgressPercent => Math.Round(Progress * 100, 1);
    public string Metadata => Missing ? "Mất liên kết Project" :
        $"{(string.IsNullOrWhiteSpace(Author) ? "Chưa rõ tác giả" : Author)} · {StatusLabel}";
    public string StatusLabel => Status switch { ProjectStatus.Translating => "Đang dịch", ProjectStatus.Editing => "Biên tập",
        ProjectStatus.Completed => "Hoàn thành", ProjectStatus.Archived => "Lưu trữ", _ => "Chuẩn bị" };
    public string ProgressLabel => Missing ? "Không thể đọc tiến độ" :
        $"{ProgressPercent:0.#}% · {CompletedChapterCount}/{ChapterCount} chương duyệt";
    public string CurrentChapterLabel => string.IsNullOrWhiteSpace(CurrentChapter) ? "Chưa có chương đang đọc" : "Đang làm: " + CurrentChapter;
    public string UpdatedLabel => "Mở gần nhất " + LastOpenedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
    public string Warning => Missing ? "Chọn “Tìm lại vị trí” để nối đúng Project ID." : Error ?? "";
}

public partial class ProjectLibraryWindow : Window
{
    private readonly IReadOnlyList<ProjectLibraryCard> _projects;
    internal ProjectLibraryAction? RequestedAction { get; private set; }
    internal ProjectLibraryCard? RequestedProject { get; private set; }

    internal ProjectLibraryWindow(IReadOnlyList<ProjectLibraryCard> projects, Guid? activeProjectId)
    {
        _projects = projects;
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 1180, 760);
        RefreshItems();
        ProjectList.SelectedItem = _projects.FirstOrDefault(project => project.ProjectId == activeProjectId) ?? _projects.FirstOrDefault();
        UpdateButtons();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e) { if (ProjectList is not null) RefreshItems(); }
    private void RefreshItems()
    {
        string query = SearchInput?.Text.Trim() ?? "";
        string status = (StatusFilter?.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        ProjectList.ItemsSource = _projects.Where(project =>
            (query.Length == 0 || project.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             project.Author.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
             project.ManifestPath.Contains(query, StringComparison.OrdinalIgnoreCase)) &&
            (status == "All" || status == "Missing" && project.Missing || !project.Missing && project.Status?.ToString() == status)).ToArray();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();
    private void UpdateButtons()
    {
        ProjectLibraryCard? selected = ProjectList.SelectedItem as ProjectLibraryCard;
        bool has = selected is not null;
        ContinueButton.IsEnabled = has && !selected!.Missing;
        LocateButton.IsEnabled = has;
        RemoveButton.IsEnabled = has;
        DeleteButton.IsEnabled = has && !selected!.Missing;
        SelectionHint.Text = selected?.ManifestPath ?? "Chọn một Project để tiếp tục.";
    }

    private void Request(ProjectLibraryAction action, bool requiresSelection)
    {
        ProjectLibraryCard? selected = ProjectList.SelectedItem as ProjectLibraryCard;
        if (requiresSelection && selected is null) return;
        RequestedAction = action; RequestedProject = selected; DialogResult = true;
    }
    private void OnContinue(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Continue, true);
    private void OnCreate(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Create, false);
    private void OnLocate(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Locate, true);
    private void OnRemove(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Remove, true);
    private void OnDelete(object sender, RoutedEventArgs e) => Request(ProjectLibraryAction.Delete, true);
}
