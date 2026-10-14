using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class CharacterConsistencyUiTests
{
    [Fact]
    public Task Character_panel_exposes_project_lock_and_quick_edit_columns() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var project = new CharacterProject
        {
            Name = "Synthetic", ConsistencyLockEnabled = true,
            Profiles = [new CharacterProfile
            {
                ChineseName = "林风", VietnameseName = "Lâm Phong", PrimaryNarrativePronoun = "hắn",
                SecondaryNarrativePronoun = "hắn ta", NarrativePronounLocked = true,
                DisallowedNarrativePronouns = ["anh ấy"]
            }]
        };
        fixture.Call("LoadCharacterProject", project);

        Assert.True(((CheckBox)fixture.Window.FindName("ConsistencyLockCheck")).IsChecked);
        var grid = (DataGrid)fixture.Window.FindName("CharactersGrid");
        Assert.False(grid.IsReadOnly);
        Assert.Contains(grid.Columns, column => Equals(column.Header, "Đại từ chính") && !column.IsReadOnly);
        Assert.Contains(grid.Columns, column => Equals(column.Header, "Khóa") && !column.IsReadOnly);
        Assert.Single(grid.Items);
        return Task.CompletedTask;
    });
}
