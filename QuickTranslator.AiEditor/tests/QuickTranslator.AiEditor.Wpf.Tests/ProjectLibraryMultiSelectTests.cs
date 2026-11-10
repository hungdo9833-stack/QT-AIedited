using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Projects;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ProjectLibraryMultiSelectTests
{
    [Fact]
    public Task Several_selected_projects_go_out_in_one_request_and_buttons_follow_the_count() => StaHost.Run(() =>
    {
        ProjectLibraryCard[] cards = [Card("Một"), Card("Hai"), Card("Ba")];
        var view = new ProjectLibraryView(cards, cards[0].ProjectId);
        var list = (ListBox)view.FindName("ProjectList");
        var requests = new List<ProjectLibraryRequest>();
        view.ActionRequested += (_, request) => requests.Add(request);

        Assert.Equal(SelectionMode.Extended, list.SelectionMode);
        list.SelectAll();

        Assert.False(((Button)view.FindName("ContinueButton")).IsEnabled);
        Assert.False(((Button)view.FindName("LocateButton")).IsEnabled);
        Assert.Equal("Thùng rác (3)", ((Button)view.FindName("DeleteButton")).Content);
        ((Button)view.FindName("DeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        ProjectLibraryRequest request = Assert.Single(requests);
        Assert.Equal(ProjectLibraryAction.Delete, request.Action);
        Assert.Equal(3, request.Selected.Count);
        Assert.Equal("3 truyện", ((TextBlock)view.FindName("CountText")).Text);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Deleting_inside_the_library_keeps_the_window_open_and_takes_the_cards_off() => StaHost.Run(async () =>
    {
        ProjectLibraryCard[] cards = [Card("Một"), Card("Hai"), Card("Ba")];
        var window = new ProjectLibraryWindow(cards, cards[0].ProjectId) { ShowInTaskbar = false };
        window.InPlaceHandler = request => Task.FromResult(new ProjectLibraryOutcome(
            request.Selected.Select(card => card.ProjectId).ToArray(), null, $"Đã chuyển {request.Selected.Count} dự án vào Thùng rác."));
        try
        {
            window.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var list = (ListBox)window.FindName("ProjectList");
            list.SelectedItems.Clear();
            list.SelectedItems.Add(list.Items[0]);
            list.SelectedItems.Add(list.Items[2]);
            ((Button)window.FindName("DeleteButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

            Assert.True(window.IsVisible);
            Assert.Null(window.RequestedAction);
            Assert.Equal("Hai", Assert.Single(list.Items.Cast<ProjectLibraryCard>()).Title);
            Assert.Equal("Đã chuyển 2 dự án vào Thùng rác.", ((TextBlock)window.FindName("ActionStatusText")).Text);
            Assert.Same(list.Items[0], list.SelectedItem);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Sorting_by_title_reorders_the_cards() => StaHost.Run(() =>
    {
        ProjectLibraryCard[] cards = [Card("Cúc"), Card("An"), Card("Bình")];
        var view = new ProjectLibraryView(cards, null);
        var sort = (ComboBox)view.FindName("SortInput");
        sort.SelectedItem = sort.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "Title");
        Assert.Equal(["An", "Bình", "Cúc"], ((ListBox)view.FindName("ProjectList")).Items.Cast<ProjectLibraryCard>().Select(card => card.Title));
        return Task.CompletedTask;
    });

    private static ProjectLibraryCard Card(string title) => new(Guid.NewGuid(), title, "Tác giả",
        ProjectStatus.Editing, $"C:\\Projects\\{title}.qtproj", null, 5, 2, 0.4, "Chương 3", DateTimeOffset.Now, false, null);
}
