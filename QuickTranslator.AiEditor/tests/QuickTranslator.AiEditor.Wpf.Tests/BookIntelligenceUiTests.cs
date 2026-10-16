using System.Windows.Controls;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class BookIntelligenceUiTests
{
    [Fact]
    public Task Character_panel_exposes_explicit_ai_survey_action_without_starting_work() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();
        var button = Assert.IsType<Button>(fixture.Window.FindName("SurveyBookButton"));
        Assert.Equal("Khảo sát bằng AI", button.Content);
        Assert.True(button.IsEnabled);
        Assert.Null(fixture.Get<object>("_bookIntelligenceWindow"));
        return Task.CompletedTask;
    });
}
