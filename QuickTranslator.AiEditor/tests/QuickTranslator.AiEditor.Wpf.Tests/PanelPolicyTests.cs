using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class PanelPolicyTests
{
    [Fact]
    public void Auto_matches_the_workspace_and_editor_mode_table()
    {
        AssertAuto(WorkspaceId.Editor, EditorMode.Translate, selected: false,
            PanelPresentation.Docked, PanelPresentation.NotApplicable, PanelPresentation.Hidden);
        AssertAuto(WorkspaceId.Editor, EditorMode.Translate, selected: true,
            PanelPresentation.Docked, PanelPresentation.NotApplicable, PanelPresentation.Docked);
        AssertAuto(WorkspaceId.Editor, EditorMode.Review, selected: true,
            PanelPresentation.Hidden, PanelPresentation.Docked, PanelPresentation.Docked);
        AssertAuto(WorkspaceId.Chapters, EditorMode.Translate, selected: true,
            PanelPresentation.NotApplicable, PanelPresentation.NotApplicable, PanelPresentation.NotApplicable);
        AssertAuto(WorkspaceId.Queue, EditorMode.Translate, selected: true,
            PanelPresentation.NotApplicable, PanelPresentation.NotApplicable, PanelPresentation.NotApplicable);
        AssertAuto(WorkspaceId.Publish, EditorMode.Translate, selected: true,
            PanelPresentation.NotApplicable, PanelPresentation.NotApplicable, PanelPresentation.NotApplicable);
        AssertAuto(WorkspaceId.Reader, EditorMode.Translate, selected: true,
            PanelPresentation.Hidden, PanelPresentation.NotApplicable, PanelPresentation.NotApplicable);
        AssertAuto(WorkspaceId.Resources, EditorMode.Translate, selected: true,
            PanelPresentation.Hidden, PanelPresentation.NotApplicable, PanelPresentation.NotApplicable);
        AssertAuto(WorkspaceId.Settings, EditorMode.Translate, selected: true,
            PanelPresentation.Hidden, PanelPresentation.NotApplicable, PanelPresentation.NotApplicable);
    }

    [Fact]
    public void Pinned_preferences_override_auto_only_for_applicable_panels()
    {
        var translate = Context(WorkspaceId.Editor, EditorMode.Translate, selected: true);
        Assert.Equal(PanelPresentation.Hidden,
            PanelPolicy.Resolve(PanelId.ProjectSidebar, PanelPreference.PinnedHidden, translate));
        Assert.Equal(PanelPresentation.Docked,
            PanelPolicy.Resolve(PanelId.Inspector, PanelPreference.PinnedShown, translate with { SegmentSelected = false }));
        Assert.Equal(PanelPresentation.NotApplicable,
            PanelPolicy.Resolve(PanelId.SegmentNavigator, PanelPreference.PinnedShown, translate));

        var review = Context(WorkspaceId.Editor, EditorMode.Review, selected: true);
        Assert.Equal(PanelPresentation.Docked,
            PanelPolicy.Resolve(PanelId.ProjectSidebar, PanelPreference.PinnedShown, review));
        Assert.Equal(PanelPresentation.Hidden,
            PanelPolicy.Resolve(PanelId.SegmentNavigator, PanelPreference.PinnedHidden, review));
        Assert.Equal(PanelPresentation.Hidden,
            PanelPolicy.Resolve(PanelId.Inspector, PanelPreference.PinnedHidden, review));
    }

    [Fact]
    public void Source_edit_hides_every_panel_regardless_of_preferences()
    {
        foreach (WorkspaceId workspace in Enum.GetValues<WorkspaceId>())
        foreach (PanelId panel in Enum.GetValues<PanelId>())
            Assert.Equal(PanelPresentation.Hidden, PanelPolicy.Resolve(panel, PanelPreference.PinnedShown,
                Context(workspace, EditorMode.Review, selected: true) with { SourceEditMode = true }));
    }

    [Theory]
    [InlineData((int)PanelId.ProjectSidebar, 1319, (int)PanelPresentation.Flyout)]
    [InlineData((int)PanelId.ProjectSidebar, 1320, (int)PanelPresentation.Docked)]
    [InlineData((int)PanelId.Inspector, 1099, (int)PanelPresentation.Flyout)]
    [InlineData((int)PanelId.Inspector, 1100, (int)PanelPresentation.Docked)]
    [InlineData((int)PanelId.SegmentNavigator, 979, (int)PanelPresentation.Flyout)]
    [InlineData((int)PanelId.SegmentNavigator, 980, (int)PanelPresentation.Docked)]
    public void Shown_panels_use_flyouts_below_their_threshold(
        int panelValue, double width, int expectedValue)
    {
        PanelId panel = (PanelId)panelValue;
        PanelPresentation expected = (PanelPresentation)expectedValue;
        EditorMode mode = panel == PanelId.SegmentNavigator ? EditorMode.Review : EditorMode.Translate;
        Assert.Equal(expected, PanelPolicy.Resolve(panel, PanelPreference.PinnedShown,
            Context(WorkspaceId.Editor, mode, selected: true) with { WindowWidth = width }));
    }

    [Fact]
    public void Project_sidebar_is_hidden_without_an_open_project()
    {
        Assert.Equal(PanelPresentation.Hidden, PanelPolicy.Resolve(PanelId.ProjectSidebar,
            PanelPreference.PinnedShown, Context(WorkspaceId.Editor, EditorMode.Translate, selected: true)
                with { ProjectOpen = false }));
    }

    private static void AssertAuto(WorkspaceId workspace, EditorMode mode, bool selected,
        PanelPresentation sidebar, PanelPresentation navigator, PanelPresentation inspector)
    {
        PanelContext context = Context(workspace, mode, selected);
        Assert.Equal(sidebar, PanelPolicy.Resolve(PanelId.ProjectSidebar, PanelPreference.Auto, context));
        Assert.Equal(navigator, PanelPolicy.Resolve(PanelId.SegmentNavigator, PanelPreference.Auto, context));
        Assert.Equal(inspector, PanelPolicy.Resolve(PanelId.Inspector, PanelPreference.Auto, context));
    }

    private static PanelContext Context(WorkspaceId workspace, EditorMode mode, bool selected) =>
        new(workspace, mode, SourceEditMode: false, ProjectOpen: true, SegmentSelected: selected, WindowWidth: 1600);
}
