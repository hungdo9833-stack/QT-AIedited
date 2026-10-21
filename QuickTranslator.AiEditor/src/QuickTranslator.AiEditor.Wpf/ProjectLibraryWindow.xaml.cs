using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

internal enum ProjectLibraryAction { Continue, Create, Locate, Remove, Delete, QuickTranslate }

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

internal sealed record ProjectLibraryRequest(ProjectLibraryAction Action, ProjectLibraryCard? Project);

public partial class ProjectLibraryWindow : Window
{
    internal ProjectLibraryAction? RequestedAction { get; private set; }
    internal ProjectLibraryCard? RequestedProject { get; private set; }

    internal ProjectLibraryWindow(IReadOnlyList<ProjectLibraryCard> projects, Guid? activeProjectId)
    {
        InitializeComponent();
        View = new ProjectLibraryView(projects, activeProjectId);
        ViewHost.Content = View;
        View.ActionRequested += OnActionRequested;
        Closed += OnClosed;
        MainWindow.ClampWindowToWorkArea(this, 1180, 760);
        View.Attach();
    }

    internal ProjectLibraryView View { get; }

    public new object FindName(string name) => View.FindName(name) ?? base.FindName(name) ??
        throw new ArgumentException($"Không tìm thấy điều khiển '{name}'.", nameof(name));

    private void OnActionRequested(object? sender, ProjectLibraryRequest request)
    {
        RequestedAction = request.Action;
        RequestedProject = request.Project;
        DialogResult = true;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        View.ActionRequested -= OnActionRequested;
        View.Detach();
    }
}
