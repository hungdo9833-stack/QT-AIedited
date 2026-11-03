using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private ChapterManagerView? _hostedChapterManagerView;
    private ProjectTranslationQueueView? _hostedQueueView;
    private ProjectPublicationView? _hostedPublicationView;
    private ProjectLibraryView? _startScreenView;
    private Guid? _hostedChapterProjectId;
    private Guid? _hostedPublicationProjectId;
    private bool _standaloneEditorRequested;

    internal ChapterManagerView? HostedChapterManagerView => _hostedChapterManagerView;
    internal ProjectTranslationQueueView? HostedQueueView => _hostedQueueView;
    internal ProjectPublicationView? HostedPublicationView => _hostedPublicationView;
    internal ProjectLibraryView? StartScreenView => _startScreenView;

    private void ShowChaptersWorkspace()
    {
        CurrentWorkspace = WorkspaceId.Chapters;
        EnsureHostedChapterManagerView();
        ApplyWorkspaceVisibility(CurrentWorkspace);
        MarkActiveNavigation(CurrentWorkspace);
    }

    private void EnsureHostedChapterManagerView()
    {
        if (_projectSession is null)
        {
            DropHostedChapterManagerView();
            return;
        }
        Guid projectId = _projectSession.Manifest.ProjectId;
        if (_hostedChapterManagerView is not null && _hostedChapterProjectId == projectId) return;
        DropHostedChapterManagerView();
        ProjectSession session = _projectSession;
        _hostedChapterManagerView = new(session, _projectCatalog, new(
            Open: OpenHostedChapterInEditorAsync,
            Rename: RenameProjectChapterAsync,
            SoftDelete: SoftDeleteFromManagerAsync,
            Restore: RestoreFromManagerAsync,
            Resegment: ResegmentFromManagerAsync,
            Queue: OpenProjectTranslationQueueAsync,
            TranslateTitles: TranslateChapterTitlesFromManagerAsync,
            ImportTxt: () => OpenDetectedBulkImportAsync()));
        _hostedChapterManagerView.CloseRequested += OnHostedChapterManagerCloseRequested;
        _hostedChapterProjectId = projectId;
        ChaptersWorkspaceHost.Content = _hostedChapterManagerView;
    }

    private async Task OpenHostedChapterInEditorAsync(Guid chapterId)
    {
        await LoadProjectChapterAsync(chapterId);
        if (_projectSession?.Manifest.CurrentChapterId == chapterId)
            NavigateTo(WorkspaceId.Editor, CurrentEditorMode);
    }

    private void OnHostedChapterManagerCloseRequested(object? sender, EventArgs e) =>
        NavigateTo(WorkspaceId.Editor, CurrentEditorMode);

    private void DropHostedChapterManagerView()
    {
        if (_hostedChapterManagerView is null) return;
        _hostedChapterManagerView.CloseRequested -= OnHostedChapterManagerCloseRequested;
        _hostedChapterManagerView.Detach();
        if (ReferenceEquals(ChaptersWorkspaceHost.Content, _hostedChapterManagerView))
            ChaptersWorkspaceHost.Content = null;
        _hostedChapterManagerView = null;
        _hostedChapterProjectId = null;
    }

    private void ShowQueueWorkspace()
    {
        ActivateQueueWorkspace();
        _ = EnsureQueueWorkspaceAsync();
    }

    private async Task EnsureQueueWorkspaceAsync()
    {
        try
        {
            if (_projectSession is null)
            {
                DropHostedQueueView();
                UpdateQueueWorkspaceEmptyState();
                return;
            }
            ProjectTranslationCoordinator coordinator = await EnsureProjectTranslationCoordinatorAsync();
            if (coordinator.CurrentJob is null)
            {
                DropHostedQueueView();
                UpdateQueueWorkspaceEmptyState();
                return;
            }
            HostProjectTranslationView(coordinator, null);
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void HostProjectTranslationView(ProjectTranslationCoordinator coordinator, ProjectTranslationJob? plan)
    {
        if (_hostedQueueView is not null && plan is null &&
            ReferenceEquals(_hostedQueueView.Coordinator, coordinator))
        {
            if (CurrentWorkspace == WorkspaceId.Queue) _hostedQueueView.Attach();
            UpdateQueueWorkspaceEmptyState();
            ActivateQueueWorkspace();
            return;
        }
        DropHostedQueueView();
        _hostedQueueView = new(_projectSession?.Manifest.VietnameseTitle ?? "Dự án", coordinator, plan,
            ReplanProjectTranslationAsync, LoadProjectChapterAsync, RefreshProjectTranslationCoordinatorAsync);
        _hostedQueueView.EditorActivationRequested += OnHostedQueueEditorActivationRequested;
        QueueWorkspaceHost.Content = _hostedQueueView;
        UpdateQueueWorkspaceEmptyState();
        ActivateQueueWorkspace();
        _ = _hostedQueueView.StartPendingPlanAsync();
    }

    private void ActivateQueueWorkspace() => HostedActivateWorkspace(WorkspaceId.Queue);

    private void HostedActivateWorkspace(WorkspaceId workspace)
    {
        CurrentWorkspace = workspace;
        ApplyWorkspaceVisibility(CurrentWorkspace);
        MarkActiveNavigation(CurrentWorkspace);
    }

    private void OnHostedQueueEditorActivationRequested(object? sender, EventArgs e) =>
        NavigateTo(WorkspaceId.Editor, CurrentEditorMode);

    private void DropHostedQueueView()
    {
        if (_hostedQueueView is null) return;
        _hostedQueueView.EditorActivationRequested -= OnHostedQueueEditorActivationRequested;
        _hostedQueueView.Detach();
        if (ReferenceEquals(QueueWorkspaceHost.Content, _hostedQueueView)) QueueWorkspaceHost.Content = null;
        _hostedQueueView = null;
        UpdateQueueWorkspaceEmptyState();
    }

    private void UpdateQueueWorkspaceEmptyState()
    {
        if (QueueWorkspaceEmpty is null) return;
        QueueWorkspaceEmpty.Visibility = ShellVisibleWhen(_hostedQueueView is null);
    }

    private async Task ShowPublicationWorkspaceAsync()
    {
        if (_projectSession is null)
        {
            HostedActivateWorkspace(WorkspaceId.Publish);
            return;
        }
        if (!BeginUiOperation(allowDuringTranslation: true)) return;
        try
        {
            if (_service is not null) await SaveCurrentAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex);
            return;
        }
        finally { EndUiOperation(); }

        if (_hostedPublicationView is null || _hostedPublicationProjectId != _projectSession.Manifest.ProjectId)
        {
            DropHostedPublicationView();
            var service = new ProjectPublicationService(_projectRepository, _projectCatalog);
            _hostedPublicationView = new(_projectSession, service, OpenHostedChapterInEditorAsync) { ShowCloseButton = false };
            _hostedPublicationView.Exported += OnHostedPublicationExported;
            PublishWorkspaceHost.Content = _hostedPublicationView;
            _hostedPublicationProjectId = _projectSession.Manifest.ProjectId;
        }
        HostedActivateWorkspace(WorkspaceId.Publish);
        await _hostedPublicationView.RefreshAsync();
    }

    private void OnHostedPublicationExported(object? sender, string path) =>
        SetStatus("Đã xuất toàn bộ chương thành một file: " + path);

    private void DropHostedPublicationView()
    {
        if (_hostedPublicationView is null) return;
        _hostedPublicationView.Exported -= OnHostedPublicationExported;
        _hostedPublicationView.Detach();
        if (ReferenceEquals(PublishWorkspaceHost.Content, _hostedPublicationView)) PublishWorkspaceHost.Content = null;
        _hostedPublicationView = null;
        _hostedPublicationProjectId = null;
    }

    private async Task RefreshStartScreenAsync()
    {
        bool visible = CurrentWorkspace == WorkspaceId.Editor && _projectSession is null && !_standaloneEditorRequested;
        if (!visible)
        {
            StartScreenHost.Visibility = Visibility.Collapsed;
            _startScreenWebRefresh?.Cancel();
            _startScreenView?.Detach();
            return;
        }
        IReadOnlyList<ProjectLibraryCard> cards = await BuildProjectLibraryCardsAsync();
        if (_projectSession is not null || _standaloneEditorRequested || CurrentWorkspace != WorkspaceId.Editor) return;
        if (_startScreenView is null)
        {
            _startScreenView = new(cards, null) { StartScreenMode = true };
            _startScreenView.ActionRequested += OnStartScreenActionRequested;
            StartScreenHost.Content = _startScreenView;
        }
        else _startScreenView.SetProjects(cards, null);
        StartScreenHost.Visibility = Visibility.Visible;
        _startScreenView.Attach();
        StartScreenWebRefresh(_startScreenView, cards);
    }

    private async void OnStartScreenActionRequested(object? sender, ProjectLibraryRequest request)
    {
        if (request.Action == ProjectLibraryAction.QuickTranslate)
        {
            _standaloneEditorRequested = true;
            StartScreenHost.Visibility = Visibility.Collapsed;
            _startScreenView?.Detach();
            NavigateTo(WorkspaceId.Editor, CurrentEditorMode);
            return;
        }
        await HandleProjectLibraryRequestAsync(request);
        if (_projectSession is null) await RefreshStartScreenAsync();
    }

    private void DropHostedProjectViews()
    {
        DropHostedChapterManagerView();
        DropHostedPublicationView();
    }
}
