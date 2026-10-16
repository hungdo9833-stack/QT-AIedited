using System.Windows.Controls;
using System.Windows;
using System.Reflection;
using QuickTranslator.AiEditor.Wpf;
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

    [Fact]
    public Task Review_window_opens_modeless_with_filters_multiselect_and_feature_toggle() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.TaskCall("EnsureOllamaBackendsLoadedAsync");
        var button = Assert.IsType<Button>(fixture.Window.FindName("SurveyBookButton"));
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(80);
        var window = Assert.IsType<BookIntelligenceReviewWindow>(fixture.Get<object>("_bookIntelligenceWindow"));
        Assert.True(window.IsVisible);
        Assert.Equal(DataGridSelectionMode.Extended, Field<DataGrid>(window, "_queue").SelectionMode);
        Assert.NotNull(Field<TextBox>(window, "_search"));
        Assert.NotNull(Field<ComboBox>(window, "_kindFilter"));
        Assert.Contains("Knowledge Context", Field<CheckBox>(window, "_translationContext").Content?.ToString());
        window.Close();

        static T Field<T>(BookIntelligenceReviewWindow window, string name) where T : class =>
            (T)typeof(BookIntelligenceReviewWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
    });
}
