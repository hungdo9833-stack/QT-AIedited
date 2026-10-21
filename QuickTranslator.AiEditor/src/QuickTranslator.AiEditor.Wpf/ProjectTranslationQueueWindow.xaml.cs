using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ProjectTranslationQueueWindow : Window
{
    internal ProjectTranslationQueueWindow(string projectTitle, ProjectTranslationCoordinator coordinator,
        ProjectTranslationJob? plan, Func<ProjectTranslationCoordinator,
            Task<(ProjectTranslationCoordinator Coordinator, ProjectTranslationJob Plan)>>? replan = null,
        Func<Guid, Task>? openChapter = null,
        Func<ProjectTranslationCoordinator, Task<ProjectTranslationCoordinator>>? refreshCoordinator = null)
    {
        InitializeComponent();
        View = new ProjectTranslationQueueView(projectTitle, coordinator, plan, replan, openChapter, refreshCoordinator);
        ViewHost.Content = View;
        View.EditorActivationRequested += OnEditorActivationRequested;
        Closed += OnClosed;
        MainWindow.ClampWindowToWorkArea(this, 1120, 760);
        View.Attach();
    }

    internal ProjectTranslationQueueView View { get; }
    internal IReadOnlyList<ProjectTranslationQueueView.QueueRow> CurrentRows => View.CurrentRows;
    internal int RowObjectCreationCount => View.RowObjectCreationCount;
    internal ProjectTranslationQueueView.QueueRefreshMeasurement MeasureRefresh() => View.MeasureRefresh();
    internal Task OpenChapterRowAsync(Guid chapterId) => View.OpenChapterRowAsync(chapterId);
    internal Task ResumeNowAsync() => View.ResumeNowAsync();

    public new object FindName(string name) => View.FindName(name) ?? base.FindName(name) ??
        throw new ArgumentException($"Không tìm thấy điều khiển '{name}'.", nameof(name));

    private void OnEditorActivationRequested(object? sender, EventArgs e) => Owner?.Activate();

    private void OnClosed(object? sender, EventArgs e)
    {
        View.EditorActivationRequested -= OnEditorActivationRequested;
        View.Detach();
    }
}
