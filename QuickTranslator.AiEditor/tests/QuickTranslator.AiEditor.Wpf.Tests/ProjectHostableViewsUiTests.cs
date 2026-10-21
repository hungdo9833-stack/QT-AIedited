using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using QuickTranslator.AiEditor.Core.Projects;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ProjectHostableViewsUiTests
{
    [Fact]
    public Task Publication_view_can_be_rehosted_without_reinspecting_until_refreshed() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        var view = new ProjectPublicationView(fixture.Project, service);
        var content = new ContentControl { Content = view };
        var host = TestWindow(content);
        try
        {
            host.Show();
            await view.LoadInspectionAsync();
            Assert.Equal(1, view.InspectionLoadCount);

            for (int index = 0; index < 2; index++)
            {
                content.Content = null;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                content.Content = view;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }

            await view.LoadInspectionAsync();
            Assert.Equal(1, view.InspectionLoadCount);
            await view.RefreshAsync();
            Assert.Equal(2, view.InspectionLoadCount);
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task Publication_view_has_one_primary_action_hides_close_and_explains_disabled_actions() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        var view = new ProjectPublicationView(fixture.Project, service) { ShowCloseButton = false };
        var host = TestWindow(view);
        try
        {
            host.Show();
            await view.LoadInspectionAsync();
            Style primary = (Style)Application.Current.FindResource("PrimaryButton");
            Button[] buttons = Descendants<Button>(view).Where(button => button.Visibility == Visibility.Visible).ToArray();
            Assert.Single(buttons, button => ReferenceEquals(button.Style, primary));
            Assert.Equal(Visibility.Collapsed, ((Button)view.FindName("CloseButton")).Visibility);
            foreach (Button button in buttons.Where(button => !button.IsEnabled))
            {
                Assert.True(ToolTipService.GetShowOnDisabled(button));
                Assert.False(string.IsNullOrWhiteSpace(button.ToolTip?.ToString()));
            }
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task Library_view_can_be_rehosted_and_each_action_emits_the_expected_request() => StaHost.Run(async () =>
    {
        ProjectLibraryCard card = Card("Một Project");
        var view = new ProjectLibraryView([card], card.ProjectId);
        var content = new ContentControl { Content = view };
        var host = TestWindow(content);
        var requests = new List<ProjectLibraryRequest>();
        view.ActionRequested += (_, request) => requests.Add(request);
        try
        {
            host.Show();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            for (int index = 0; index < 2; index++)
            {
                content.Content = null;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                content.Content = view;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            }

            foreach ((string name, ProjectLibraryAction action, bool hasProject) in new[]
            {
                ("CreateButton", ProjectLibraryAction.Create, true),
                ("ContinueButton", ProjectLibraryAction.Continue, true),
                ("LocateButton", ProjectLibraryAction.Locate, true),
                ("RemoveButton", ProjectLibraryAction.Remove, true),
                ("DeleteButton", ProjectLibraryAction.Delete, true)
            })
            {
                ((Button)view.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ProjectLibraryRequest request = requests[^1];
                Assert.Equal(action, request.Action);
                Assert.Equal(hasProject ? card : null, request.Project);
            }
        }
        finally { host.Close(); }
    });

    [Fact]
    public Task Library_window_preserves_modal_result_and_requested_action() => StaHost.Run(() =>
    {
        ProjectLibraryCard card = Card("Project modal");
        var window = new ProjectLibraryWindow([card], card.ProjectId)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -30000,
            Top = -30000
        };
        window.Dispatcher.BeginInvoke(() =>
            ((Button)window.FindName("ContinueButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

        Assert.True(window.ShowDialog());
        Assert.Equal(ProjectLibraryAction.Continue, window.RequestedAction);
        Assert.Equal(card, window.RequestedProject);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Library_start_screen_switches_its_single_primary_action_with_project_availability() => StaHost.Run(() =>
    {
        Style primary = (Style)Application.Current.FindResource("PrimaryButton");
        var view = new ProjectLibraryView([], null) { StartScreenMode = true };
        Button create = (Button)view.FindName("CreateButton");
        Button continueButton = (Button)view.FindName("ContinueButton");
        Assert.Equal("Bắt đầu", ((TextBlock)view.FindName("HeaderTitleText")).Text);
        Assert.Equal("Tạo Project", create.Content);
        Assert.Equal(Visibility.Visible, create.Visibility);
        Assert.Equal(Visibility.Collapsed, continueButton.Visibility);
        Assert.Single(VisiblePrimaryButtons(create, continueButton, primary));

        ProjectLibraryCard card = Card("Project gần đây");
        view.SetProjects([card], card.ProjectId);
        Assert.Equal(Visibility.Visible, continueButton.Visibility);
        Assert.Equal("Tiếp tục", continueButton.Content);
        Assert.Single(VisiblePrimaryButtons(create, continueButton, primary));
        Assert.Same(continueButton, VisiblePrimaryButtons(create, continueButton, primary).Single());
        return Task.CompletedTask;
    });

    private static ProjectLibraryCard Card(string title) => new(Guid.NewGuid(), title, "Tác giả",
        ProjectStatus.Editing, $"C:\\Projects\\{title}.qtproj", null, 5, 2, 0.4, "Chương 3",
        DateTimeOffset.Now, false, null);

    private static IEnumerable<Button> VisiblePrimaryButtons(Button create, Button continueButton, Style primary) =>
        new[] { create, continueButton }.Where(button =>
            button.Visibility == Visibility.Visible && ReferenceEquals(button.Style, primary));

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static Window TestWindow(object content) => new()
    {
        Content = content,
        Width = 1000,
        Height = 700,
        ShowInTaskbar = false,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Left = -30000,
        Top = -30000
    };
}
