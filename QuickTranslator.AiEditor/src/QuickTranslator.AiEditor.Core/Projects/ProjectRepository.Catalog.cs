using System.Diagnostics;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed partial class ProjectRepository
{
    public event EventHandler<ProjectCatalogChangedEventArgs>? ManifestCommitted;

    private void PublishManifestCommitted(ProjectSession session, string operation)
    {
        var change = new ProjectCatalogChangedEventArgs(session.Manifest.ProjectId, operation);
        EventHandler<ProjectCatalogChangedEventArgs>? handlers = ManifestCommitted;
        if (handlers is null) return;
        foreach (EventHandler<ProjectCatalogChangedEventArgs> handler in handlers.GetInvocationList().Cast<EventHandler<ProjectCatalogChangedEventArgs>>())
        {
            try { handler(this, change); }
            catch (Exception ex) { Trace.TraceError($"Manifest commit subscriber failed after {operation}: {ex}"); }
        }
    }
}
