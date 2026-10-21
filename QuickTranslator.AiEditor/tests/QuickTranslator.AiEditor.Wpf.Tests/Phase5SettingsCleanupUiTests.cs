using System.Windows;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class Phase5SettingsCleanupUiTests
{
    private static readonly (string Tag, string Panel)[] SettingsSections =
    [
        ("Ai", "SettingsAiPanel"), ("Gemini", "SettingsGeminiPanel"), ("Traditional", "SettingsTraditionalPanel"),
        ("Appearance", "SettingsAppearancePanel"), ("Advanced", "SettingsAdvancedPanel")
    ];

    private static readonly (string Tag, string Panel)[] ResourceSections =
    [
        ("Dictionary", "SettingsDictionaryPanel"), ("Characters", "SettingsCharactersPanel"), ("Prompts", "SettingsPromptPanel")
    ];

    [Fact]
    public Task Each_settings_and_resources_section_shows_only_its_panel_and_one_primary_action() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        object primary = fixture.Window.FindResource("PrimaryButton");

        foreach ((string tag, string panel) in SettingsSections)
        {
            fixture.Call("ShowSettingsWorkspace", tag);
            Assert.Equal(WorkspaceId.Settings, fixture.Window.CurrentWorkspace);
            AssertOnlyPanelVisible(fixture, SettingsSections, panel);
            AssertSinglePrimary(fixture, primary, panel, "SettingsSaveButton");
        }
        foreach ((string tag, string panel) in ResourceSections)
        {
            fixture.Call("ShowResourcesWorkspace", tag);
            Assert.Equal(WorkspaceId.Resources, fixture.Window.CurrentWorkspace);
            AssertOnlyPanelVisible(fixture, ResourceSections, panel);
            AssertSinglePrimary(fixture, primary, panel, "ResourcesSaveButton");
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Project_data_lives_only_in_resources_and_configuration_only_in_settings() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var resources = (FrameworkElement)fixture.Window.FindName("ResourcesWorkspaceRoot");
        var settings = (FrameworkElement)fixture.Window.FindName("SettingsWorkspaceRoot");
        foreach (string name in new[] { "GlossaryGrid", "ContextNotesInput", "CharactersGrid", "AddressingLedgerButton",
                     "PresetCombo", "ImportGlossaryButton", "ImportCharactersButton" })
        {
            var element = (DependencyObject)fixture.Window.FindName(name);
            Assert.True(IsInside(element, resources), name + " phải nằm trong Tài nguyên");
            Assert.False(IsInside(element, settings), name + " không được nằm trong Thiết lập");
        }
        foreach (string name in new[] { "GeminiApiKeyInput", "TraditionalDataDirectoryInput", "ThemeSystemRadio",
                     "TraditionalAlignmentDiagnosticsInput", "TemperatureInput", "ModelInput" })
        {
            var element = (DependencyObject)fixture.Window.FindName(name);
            Assert.True(IsInside(element, settings), name + " phải nằm trong Thiết lập");
            Assert.False(IsInside(element, resources), name + " không được nằm trong Tài nguyên");
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Resource_and_configuration_edits_mark_only_their_own_scope_and_survive_navigation() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        fixture.Call("ShowResourcesWorkspace", "Dictionary");
        fixture.Text("ContextNotesInput").Text = "Ngữ cảnh mới";
        Assert.True(fixture.Get<bool>("_resourcesDirtyValue"));
        Assert.False(fixture.Get<bool>("_configurationDirtyValue"));

        fixture.Call("MarkSettingsSaved");
        fixture.Call("ShowSettingsWorkspace", "Ai");
        // InitializeSettingsUi (which wires runtime inputs) does not run in the test host: raise the shared handler.
        TextBox temperature = fixture.Text("TemperatureInput");
        temperature.Text = "0.3";
        fixture.Call("OnSettingsInputChanged", temperature, new RoutedEventArgs());
        Assert.True(fixture.Get<bool>("_configurationDirtyValue"));
        Assert.False(fixture.Get<bool>("_resourcesDirtyValue"));
        Assert.Equal("Có thay đổi chưa lưu", ((TextBlock)fixture.Window.FindName("SettingsDirtyText")).Text);
        Assert.Equal("Mọi thay đổi đã được lưu", ((TextBlock)fixture.Window.FindName("ResourcesDirtyText")).Text);

        // Leaving a section or workspace never saves and never clears the flag.
        fixture.Call("ShowSettingsWorkspace", "Advanced");
        fixture.Call("ShowResourcesWorkspace", "Prompts");
        fixture.Window.NavigateTo(WorkspaceId.Editor);
        fixture.Call("ShowSettingsWorkspace", "Ai");
        Assert.True(fixture.Get<bool>("_configurationDirtyValue"));
        Assert.Equal("0.3", fixture.Text("TemperatureInput").Text);

        fixture.Call("MarkSettingsSaved");
        Assert.False(fixture.Get<bool>("_configurationDirtyValue"));
        Assert.False(fixture.Get<bool>("_resourcesDirtyValue"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Settings_routes_and_resource_import_export_stay_reachable_from_the_palette() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        string[] ids = fixture.Window.GetCommandPaletteItems(true, "").Select(item => item.Id).ToArray();
        foreach (string id in new[] { "open-settings-ai", "open-settings-gemini", "open-settings-data", "open-settings-appearance",
                     "open-settings-advanced", "import-glossary", "export-glossary", "import-characters", "export-characters" })
            Assert.Contains(id, ids);
        return Task.CompletedTask;
    });

    private static void AssertOnlyPanelVisible(WorkspaceFixture fixture, (string Tag, string Panel)[] sections, string visible)
    {
        foreach ((_, string panel) in sections)
            Assert.Equal(panel == visible ? Visibility.Visible : Visibility.Collapsed,
                ((UIElement)fixture.Window.FindName(panel)).Visibility);
    }

    private static void AssertSinglePrimary(WorkspaceFixture fixture, object primary, string panel, string saveButton)
    {
        int count = LogicalDescendants((DependencyObject)fixture.Window.FindName(panel)).OfType<Button>()
            .Count(button => ReferenceEquals(button.Style, primary));
        if (ReferenceEquals(((Button)fixture.Window.FindName(saveButton)).Style, primary)) count++;
        Assert.True(count == 1, $"{panel}: {count} nút chính");
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject root)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject element) continue;
            yield return element;
            foreach (DependencyObject descendant in LogicalDescendants(element)) yield return descendant;
        }
    }

    private static bool IsInside(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? current = element; current is not null; current = LogicalTreeHelper.GetParent(current))
            if (ReferenceEquals(current, root)) return true;
        return false;
    }
}
