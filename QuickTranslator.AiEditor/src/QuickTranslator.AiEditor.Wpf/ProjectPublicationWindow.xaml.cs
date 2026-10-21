using System.ComponentModel;
using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class ProjectPublicationWindow : Window
{
    internal ProjectPublicationWindow(ProjectSession session, ProjectPublicationService service,
        Func<Guid, Task>? openChapter = null)
    {
        InitializeComponent();
        View = new ProjectPublicationView(session, service, openChapter);
        ViewHost.Content = View;
        View.CloseRequested += OnCloseRequested;
        Closed += OnClosed;
        MainWindow.ClampWindowToWorkArea(this, 780, 640);
        View.Attach();
    }

    internal ProjectPublicationView View { get; }
    internal string? LastExportPath => View.LastExportPath;
    internal ProjectPublicationInspection? CurrentInspection => View.CurrentInspection;
    internal Task LoadInspectionAsync() => View.LoadInspectionAsync();
    internal Task RefreshAsync() => View.RefreshAsync();

    public new object FindName(string name) => View.FindName(name) ?? base.FindName(name) ??
        throw new ArgumentException($"Không tìm thấy điều khiển '{name}'.", nameof(name));

    private void OnCloseRequested(object? sender, EventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e) => View.HandleHostClosing(e);

    private void OnClosed(object? sender, EventArgs e)
    {
        View.CloseRequested -= OnCloseRequested;
        View.Detach();
    }
}
