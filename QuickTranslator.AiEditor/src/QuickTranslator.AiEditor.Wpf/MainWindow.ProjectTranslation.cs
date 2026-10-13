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
    private readonly List<IDisposable> _projectTranslationOwnedClients = [];

    private void InitializeProjectTranslationCoordinator(ProjectSession session)
    {
        _projectTranslationWindow?.Close();
        if (_projectTranslationCoordinator is not null)
            _projectTranslationCoordinator.ProgressChanged -= OnProjectTranslationProgressChanged;
        var coordinator = new ProjectTranslationCoordinator(session, _projectRepository, _translationClient,
            cancellationToken => LoadProjectTranslationEnvironmentAsync(session, cancellationToken));
        coordinator.ProgressChanged += OnProjectTranslationProgressChanged;
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

    private async Task<ProjectTranslationEnvironment> LoadProjectTranslationEnvironmentAsync(
        ProjectSession session, CancellationToken cancellationToken)
    {
        var settings = new ProjectSettingsStore(session);
        ProjectTranslationSettings translation = await settings.LoadTranslationAsync(cancellationToken);
        var presetStore = new PromptPresetStore(session.Resolve(session.Manifest.Files.PromptPresets));
        PresetLibrary library = await presetStore.LoadAsync(cancellationToken);
        var presets = new PromptPresetManager(library);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync(cancellationToken);
        OllamaBackendProfile profile = _ollamaBackends.GetProfile(translation.OllamaProfileId);
        if (_ollamaBackends.ActiveProfile.Id != profile.Id)
            throw new InvalidOperationException("Profile Ollama của Project chưa được kích hoạt; hãy mở AI & hiệu năng và chọn lại backend.");
        AiRuntimeProfile runtime = profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model);
        return new(runtime.ModelOptions with { }, presets.Selected, resources,
            BackendSnapshot(profile, runtime.ModelOptions.Model));
    }

    private async Task OpenProjectTranslationQueueAsync(IReadOnlyList<Guid> chapterIds)
    {
        if (_projectSession is null) throw new InvalidOperationException("Project đã đóng.");
        if (!BeginUiOperation(allowDuringTranslation: true))
            throw new InvalidOperationException("Ứng dụng đang xử lý thao tác khác; hãy thử lại sau.");
        try
        {
            ProjectTranslationCoordinator initial = await EnsureProjectTranslationCoordinatorAsync();
            ProjectTranslationJob? recovered = initial.CurrentJob;
            ProjectTranslationJob? resumable = recovered?.Status is ProjectTranslationJobStatus.Completed or
                ProjectTranslationJobStatus.CompletedWithErrors or ProjectTranslationJobStatus.Cancelled ? null : recovered;
            await ApplyTranslationModeAsync(resumable);
            EnsureOllamaBackendReady();
            await SaveCurrentAsync();
            await SaveProjectAssetsAsync();
            ProjectTranslationCoordinator coordinator = resumable?.Workers.Count > 0 ||
                resumable is null && _gpuPreferences.Mode == GpuTranslationMode.ParallelLocalRemote
                ? await RebuildParallelCoordinatorAsync(resumable)
                : await EnsureProjectTranslationCoordinatorAsync();
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

    private async Task ApplyTranslationModeAsync(ProjectTranslationJob? recovered)
    {
        Guid profileId;
        if (recovered?.Workers.Count > 0)
        {
            profileId = recovered.Workers.FirstOrDefault(worker => worker.Backend.Kind == OllamaBackendKind.Remote)?.ProfileId
                ?? OllamaBackendProfile.DefaultLocalId;
            _gpuPreferences.Mode = recovered.Workers.Count > 1
                ? GpuTranslationMode.ParallelLocalRemote
                : recovered.Workers[0].Backend.Kind == OllamaBackendKind.Remote
                    ? GpuTranslationMode.RemoteOnly : GpuTranslationMode.LocalOnly;
            if (profileId != OllamaBackendProfile.DefaultLocalId) _gpuPreferences.RemoteProfileId = profileId;
        }
        else profileId = _gpuPreferences.Mode == GpuTranslationMode.LocalOnly
            ? OllamaBackendProfile.DefaultLocalId
            : _gpuPreferences.RemoteProfileId ?? _ollamaBackends.ActiveProfile.Id;
        OllamaBackendProfile profile = _ollamaBackends.GetProfile(profileId);
        if (_ollamaBackends.ActiveProfile.Id != profileId) await _ollamaBackends.ActivateAsync(profileId);
        _selectedOllamaProfileId = profileId;
        _projectOllamaProfileMissing = false;
        SelectOllamaProfileInUi(profileId);
    }

    private async Task<ProjectTranslationCoordinator> RebuildParallelCoordinatorAsync(ProjectTranslationJob? recovered)
    {
        if (_projectSession is null) throw new InvalidOperationException("Project đã đóng.");
        _projectTranslationWindow?.Close(); _projectTranslationWindow = null;
        if (_projectTranslationCoordinator is not null)
            _projectTranslationCoordinator.ProgressChanged -= OnProjectTranslationProgressChanged;
        foreach (IDisposable client in _projectTranslationOwnedClients) client.Dispose();
        _projectTranslationOwnedClients.Clear();
        ProjectTranslationEnvironment environment = await LoadProjectTranslationEnvironmentAsync(_projectSession, CancellationToken.None);
        var definitions = new List<ProjectTranslationWorkerDefinition>();
        if (recovered?.Workers.Count > 0)
        {
            foreach (ProjectTranslationWorkerState worker in recovered.Workers)
                definitions.Add(CreateWorkerDefinition(worker.WorkerId, worker.Backend, worker.ModelOptions,
                    worker.HourlyPrice, worker.BackendIdentity));
        }
        else
        {
            OllamaBackendProfile local = _ollamaBackends.GetProfile(OllamaBackendProfile.DefaultLocalId);
            Guid remoteId = _gpuPreferences.RemoteProfileId
                ?? throw new InvalidOperationException("Mở GPU Manager, kết nối và chọn một profile Remote trước khi dùng Parallel.");
            OllamaBackendProfile remote = _ollamaBackends.GetProfile(remoteId);
            definitions.Add(CreateWorkerDefinition(StableWorkerId(local.Id), BackendSnapshot(local, local.Model),
                environment.ModelOptions with { Model = local.Model }, local.HourlyPrice));
            definitions.Add(CreateWorkerDefinition(StableWorkerId(remote.Id), BackendSnapshot(remote, remote.Model),
                environment.ModelOptions with { Model = remote.Model }, remote.HourlyPrice));
        }
        var coordinator = new ProjectTranslationCoordinator(_projectSession, _projectRepository, definitions,
            cancellationToken => LoadProjectTranslationEnvironmentAsync(_projectSession, cancellationToken));
        coordinator.ProgressChanged += OnProjectTranslationProgressChanged;
        _projectTranslationCoordinator = coordinator;
        _projectTranslationLoadTask = coordinator.LoadAsync();
        await _projectTranslationLoadTask;
        _projectTranslationLoadTask = null;
        return coordinator;
    }

    private ProjectTranslationWorkerDefinition CreateWorkerDefinition(Guid workerId, OllamaBackendSnapshot backend,
        ModelOptions options, decimal? hourlyPrice, string? expectedIdentity = null)
    {
        if (backend.Kind == OllamaBackendKind.Local)
        {
            string identity = "local:" + backend.ProfileId.ToString("N");
            EnsureBackendIdentity(expectedIdentity, identity, backend.ProfileName);
            IOllamaBackendClient client = OllamaClient.CreateLocal();
            _projectTranslationOwnedClients.Add(client);
            return new(workerId, backend, client, options with { }, hourlyPrice, identity);
        }
        if (!_gpuConnections.TryGetConnection(backend.ProfileId, out GpuConnectionHandle? connection) ||
            connection is null || !connection.IsConnected)
            throw new InvalidOperationException($"GPU Remote “{backend.ProfileName}” chưa có SSH tunnel. Mở GPU Manager và bấm Kết nối.");
        string remoteIdentity = SshBackendIdentity(connection.Profile);
        EnsureBackendIdentity(expectedIdentity, remoteIdentity, backend.ProfileName);
        return new(workerId, backend, connection.Client, options with { }, hourlyPrice, remoteIdentity);
    }

    private static void EnsureBackendIdentity(string? expected, string current, string name)
    {
        if (!string.IsNullOrEmpty(expected) && !string.Equals(expected, current, StringComparison.Ordinal))
            throw new InvalidOperationException($"Danh tính SSH của “{name}” đã đổi sau khi tạo Queue Plan. Job bị chặn để tránh gửi nguyên văn sang máy khác.");
    }

    private static string SshBackendIdentity(OllamaBackendProfile profile)
    {
        GpuSshSettings ssh = profile.Ssh ?? throw new InvalidOperationException("Profile Remote thiếu cấu hình SSH.");
        string value = $"{profile.Id:N}|{ssh.Host.ToLowerInvariant()}|{ssh.Port}|{ssh.Username}|{ssh.RemoteOllamaPort}|{ssh.HostKeyFingerprint}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    }

    private static Guid StableWorkerId(Guid profileId)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(profileId.ToByteArray());
        return new Guid(hash.AsSpan(0, 16));
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
        if (_projectTranslationCoordinator is not null)
            _projectTranslationCoordinator.ProgressChanged -= OnProjectTranslationProgressChanged;
        _projectTranslationCoordinator = null;
        _projectTranslationLoadTask = null;
        foreach (IDisposable client in _projectTranslationOwnedClients) client.Dispose();
        _projectTranslationOwnedClients.Clear();
        RefreshShellQueueStatus();
    }
}
