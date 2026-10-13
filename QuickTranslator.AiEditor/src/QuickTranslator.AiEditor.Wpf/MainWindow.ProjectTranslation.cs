using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private ProjectTranslationCoordinator? _projectTranslationCoordinator;
    private ProjectTranslationQueueWindow? _projectTranslationWindow;
    private Task<ProjectTranslationJob?>? _projectTranslationLoadTask;

    private void InitializeProjectTranslationCoordinator(ProjectSession session)
    {
        _projectTranslationWindow?.Close();
        var coordinator = new ProjectTranslationCoordinator(session, _projectRepository, _translationClient,
            cancellationToken => LoadProjectTranslationEnvironmentAsync(session, cancellationToken));
        _projectTranslationCoordinator = coordinator;
        _projectTranslationLoadTask = coordinator.LoadAsync();
    }

    private async Task<ProjectTranslationCoordinator> EnsureProjectTranslationCoordinatorAsync()
    {
        if (_projectSession is null) throw new InvalidOperationException("Hãy mở Project trước khi tạo translation queue.");
        if (_projectTranslationCoordinator is null) InitializeProjectTranslationCoordinator(_projectSession);
        Task<ProjectTranslationJob?>? loading = _projectTranslationLoadTask;
        if (loading is not null)
        {
            await loading;
            if (ReferenceEquals(loading, _projectTranslationLoadTask)) _projectTranslationLoadTask = null;
        }
        return _projectTranslationCoordinator!;
    }

    private static async Task<ProjectTranslationEnvironment> LoadProjectTranslationEnvironmentAsync(
        ProjectSession session, CancellationToken cancellationToken)
    {
        var settings = new ProjectSettingsStore(session);
        ProjectTranslationSettings translation = await settings.LoadTranslationAsync(cancellationToken);
        var presetStore = new PromptPresetStore(session.Resolve(session.Manifest.Files.PromptPresets));
        PresetLibrary library = await presetStore.LoadAsync(cancellationToken);
        var presets = new PromptPresetManager(library);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync(cancellationToken);
        return new(translation.Model with { }, presets.Selected, resources);
    }

    private async Task OpenProjectTranslationQueueAsync(IReadOnlyList<Guid> chapterIds)
    {
        if (_projectSession is null) throw new InvalidOperationException("Project đã đóng.");
        if (!BeginUiOperation(allowDuringTranslation: true))
            throw new InvalidOperationException("Ứng dụng đang xử lý thao tác khác; hãy thử lại sau.");
        try
        {
            await SaveCurrentAsync();
            await SaveProjectAssetsAsync();
            ProjectTranslationCoordinator coordinator = await EnsureProjectTranslationCoordinatorAsync();
            ProjectTranslationJob? existing = coordinator.CurrentJob;
            if (coordinator.IsRunning || existing?.Status is ProjectTranslationJobStatus.Paused or
                ProjectTranslationJobStatus.Interrupted or ProjectTranslationJobStatus.Ready)
            {
                ShowProjectTranslationWindow(coordinator, null);
                return;
            }
            ProjectTranslationJob plan = await coordinator.PlanAsync(chapterIds);
            // The Project queue owns chapter writes while running. Remove the editor service before showing the plan,
            // so Start cannot race an open chapter service.
            DetachChapter();
            ClearChapterEditor();
            Refresh();
            ShowProjectTranslationWindow(coordinator, plan);
        }
        finally { EndUiOperation(); }
    }

    private void ShowProjectTranslationWindow(ProjectTranslationCoordinator coordinator, ProjectTranslationJob? plan)
    {
        if (_projectTranslationWindow is { IsVisible: true })
        {
            _projectTranslationWindow.Activate();
            return;
        }
        _projectTranslationWindow = new(_projectSession?.Manifest.VietnameseTitle ?? "Project", coordinator, plan);
        if (IsVisible) _projectTranslationWindow.Owner = this;
        _projectTranslationWindow.Closed += (_, _) => _projectTranslationWindow = null;
        _projectTranslationWindow.Show();
    }

    private async Task PauseProjectTranslationForContextChangeAsync()
    {
        ProjectTranslationCoordinator? coordinator = _projectTranslationCoordinator;
        if (coordinator?.IsRunning != true) return;
        SetStatus("Đang Pause translation queue và lưu checkpoint trước khi chuyển context…");
        await coordinator.PauseAsync();
    }

    private void ClearProjectTranslationCoordinator()
    {
        _projectTranslationWindow?.Close();
        _projectTranslationWindow = null;
        _projectTranslationCoordinator = null;
        _projectTranslationLoadTask = null;
    }
}
