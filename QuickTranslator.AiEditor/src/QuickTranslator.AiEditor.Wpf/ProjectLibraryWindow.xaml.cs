using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

internal enum ProjectLibraryAction { Continue, Create, Locate, Remove, Delete, QuickTranslate, OpenFolder }

/// <summary>What an action did inside the library: projects to take off the list, a card found again, a status line.</summary>
internal sealed record ProjectLibraryOutcome(IReadOnlyCollection<Guid> Removed, ProjectLibraryCard? Replaced, string? Status)
{
    public static ProjectLibraryOutcome None { get; } = new([], null, null);
}

internal sealed record ProjectLibraryCard(Guid ProjectId, string Title, string Author, ProjectStatus? Status,
    string ManifestPath, string? CoverPath, int ChapterCount, int CompletedChapterCount, double Progress,
    string? CurrentChapter, DateTimeOffset LastOpenedAt, bool Missing, string? Error,
    bool WebLinked = false, VanCacWebProgress? Web = null)
{
    /// <summary>The Sắc Viện line shows only for Projects linked to a novel on the website.</summary>
    public bool HasWebStatus => WebLinked && !Missing;
    public bool IsOnWeb => Web is { PublishedCount: > 0 };
    public string WebLabel => WebLabelFor(Web);
    internal static string WebLabelFor(VanCacWebProgress? progress) => progress switch
    {
        { PublishedCount: > 0 } web => $"Đã có trên web đến chương {web.PublishedThrough}",
        { DraftCount: > 0 } web => $"Trên web: {web.DraftCount} chương nháp",
        not null => "Trên web: chưa có chương",
        null => "Đã liên kết Sắc Viện · chưa kiểm tra",
    };
    public string WebToolTip => Web is null
        ? "Đăng nhập Sắc Viện rồi mở lại Thư viện để kiểm tra."
        : $"Sắc Viện: {Web.PublishedCount} chương công khai, {Web.DraftCount} chương nháp · kiểm tra lúc {Web.CheckedAt.LocalDateTime:dd/MM HH:mm}";
    public double ProgressPercent => Math.Round(Progress * 100, 1);
    public string Metadata => Missing ? "Mất liên kết dự án" :
        $"{(string.IsNullOrWhiteSpace(Author) ? "Chưa rõ tác giả" : Author)} · {StatusLabel}";
    public string StatusLabel => Status switch { ProjectStatus.Translating => "Đang dịch", ProjectStatus.Editing => "Biên tập",
        ProjectStatus.Completed => "Hoàn thành", ProjectStatus.Archived => "Lưu trữ", _ => "Chuẩn bị" };
    public string ProgressLabel => Missing ? "Không thể đọc tiến độ" :
        $"{ProgressPercent:0.#}% · {CompletedChapterCount}/{ChapterCount} chương duyệt";
    public string CurrentChapterLabel => string.IsNullOrWhiteSpace(CurrentChapter) ? "Chưa có chương đang đọc" : "Đang làm: " + CurrentChapter;
    public string UpdatedLabel => "Mở gần nhất " + LastOpenedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm");
    public string Warning => Missing ? "Chọn “Tìm lại vị trí” để nối đúng ID dự án." : Error ?? "";
}

/// <param name="Projects">Every selected project (Ctrl/Shift selection); <paramref name="Project"/> is the first.</param>
internal sealed record ProjectLibraryRequest(ProjectLibraryAction Action, ProjectLibraryCard? Project,
    IReadOnlyList<ProjectLibraryCard>? Projects = null)
{
    public IReadOnlyList<ProjectLibraryCard> Selected => Projects ?? (Project is null ? [] : [Project]);

    /// <summary>Opening or creating a project leaves the library; every other action is done in place.</summary>
    public bool LeavesLibrary => Action is ProjectLibraryAction.Continue or ProjectLibraryAction.Create or ProjectLibraryAction.QuickTranslate;
}

public partial class ProjectLibraryWindow : Window
{
    internal ProjectLibraryAction? RequestedAction { get; private set; }
    internal ProjectLibraryCard? RequestedProject { get; private set; }

    /// <summary>Runs an action that keeps the library open (delete, remove, locate, open folder) and says what changed.</summary>
    internal Func<ProjectLibraryRequest, Task<ProjectLibraryOutcome>>? InPlaceHandler { get; set; }
    private bool _busy;

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

    private async void OnActionRequested(object? sender, ProjectLibraryRequest request)
    {
        if (request.LeavesLibrary || InPlaceHandler is null)
        {
            RequestedAction = request.Action;
            RequestedProject = request.Project;
            DialogResult = true;
            return;
        }
        if (_busy) return;
        _busy = true;
        try { ApplyOutcome(View, await InPlaceHandler(request)); }
        finally { _busy = false; }
    }

    internal static void ApplyOutcome(ProjectLibraryView view, ProjectLibraryOutcome outcome)
    {
        if (outcome.Removed.Count > 0) view.RemoveProjects(outcome.Removed, outcome.Status ?? "");
        else if (outcome.Replaced is not null) view.ReplaceProject(outcome.Replaced, outcome.Status ?? "");
        else view.ShowStatus(outcome.Status);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        View.ActionRequested -= OnActionRequested;
        View.Detach();
    }
}
