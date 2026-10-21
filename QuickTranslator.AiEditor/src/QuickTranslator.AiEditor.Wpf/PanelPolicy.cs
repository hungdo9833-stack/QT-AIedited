namespace QuickTranslator.AiEditor.Wpf;

internal enum PanelId
{
    ProjectSidebar,
    SegmentNavigator,
    Inspector
}

internal enum PanelPreference
{
    Auto,
    PinnedShown,
    PinnedHidden
}

internal enum PanelPresentation
{
    Docked,
    Flyout,
    Hidden,
    NotApplicable
}

internal sealed record PanelContext(
    WorkspaceId Workspace,
    EditorMode EditorMode,
    bool SourceEditMode,
    bool ProjectOpen,
    bool SegmentSelected,
    double WindowWidth);

internal static class PanelPolicy
{
    internal static PanelPresentation Resolve(PanelId panel, PanelPreference preference, PanelContext context)
    {
        if (context.SourceEditMode)
            return PanelPresentation.Hidden;

        bool? autoShown = AutoVisibility(panel, context);
        if (autoShown is null)
            return PanelPresentation.NotApplicable;

        bool shown = preference switch
        {
            PanelPreference.PinnedShown => true,
            PanelPreference.PinnedHidden => false,
            _ => autoShown.Value
        };

        if (panel == PanelId.ProjectSidebar && !context.ProjectOpen)
            shown = false;
        if (!shown)
            return PanelPresentation.Hidden;

        double flyoutThreshold = panel switch
        {
            PanelId.ProjectSidebar => 1320,
            PanelId.Inspector => 1100,
            PanelId.SegmentNavigator => 980,
            _ => 0
        };
        return context.WindowWidth > 0 && context.WindowWidth < flyoutThreshold
            ? PanelPresentation.Flyout
            : PanelPresentation.Docked;
    }

    private static bool? AutoVisibility(PanelId panel, PanelContext context) => context.Workspace switch
    {
        WorkspaceId.Editor when context.EditorMode == EditorMode.Translate => panel switch
        {
            PanelId.ProjectSidebar => true,
            PanelId.SegmentNavigator => null,
            PanelId.Inspector => context.SegmentSelected,
            _ => null
        },
        WorkspaceId.Editor when context.EditorMode == EditorMode.Review => panel switch
        {
            PanelId.ProjectSidebar => false,
            PanelId.SegmentNavigator => true,
            PanelId.Inspector => true,
            _ => null
        },
        WorkspaceId.Reader or WorkspaceId.Resources or WorkspaceId.Settings => panel switch
        {
            PanelId.ProjectSidebar => false,
            _ => null
        },
        _ => null
    };
}
