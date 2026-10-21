using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using System.Net.Http;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private ProjectTranslationCoordinator? _projectTranslationCoordinator;
    private Task<ProjectTranslationJob?>? _projectTranslationLoadTask;
    private readonly List<IDisposable> _projectTranslationOwnedClients = [];

    private void InitializeProjectTranslationCoordinator(ProjectSession session)
    {
        DropHostedQueueView();
        if (_projectTranslationCoordinator is not null)
            _projectTranslationCoordinator.ProgressChanged -= OnProjectTranslationProgressChanged;
        var coordinator = new ProjectTranslationCoordinator(session, _projectRepository, _translationClient,
            cancellationToken => LoadProjectTranslationEnvironmentAsync(session, cancellationToken));
        coordinator.ProgressChanged += OnProjectTranslationProgressChanged;
        _projectTranslationCoordinator = coordinator;
        _projectTranslationLoadTask = LoadProjectTranslationStateAsync(coordinator);
    }

    private async Task<ProjectTranslationJob?> LoadProjectTranslationStateAsync(ProjectTranslationCoordinator coordinator)
    {
        ProjectTranslationJob? job = await coordinator.LoadAsync();
        if (!_closing) await Dispatcher.InvokeAsync(RefreshShellQueueStatus);
        return job;
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
        // Queue uses the same application-wide presets as the Editor, while
        // each Project independently remembers which shared preset it selected.
        PresetLibrary library = await _presetStore.LoadAsync(cancellationToken);
        var presets = new PromptPresetManager(library);
        Guid presetId = translation.SelectedPromptPresetId is Guid selectedId &&
            presets.Presets.Any(p => p.Id == selectedId)
            ? selectedId : PromptPreset.Default.Id;
        presets.Select(presetId);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync(cancellationToken);
        OllamaBackendProfile profile = _ollamaBackends.GetProfile(translation.OllamaProfileId);
        // A persisted multi-worker queue pins each backend independently. The globally active
        // editor profile is not required to match a particular worker after restart.
        bool pinnedWorkers = _projectTranslationCoordinator?.CurrentJob?.Workers.Count > 0;
        if (!pinnedWorkers && _ollamaBackends.ActiveProfile.Id != profile.Id)
            throw new InvalidOperationException("Profile Ollama của Project chưa được kích hoạt; hãy mở AI & hiệu năng và chọn lại backend.");
        AiRuntimeProfile runtime = profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model);
        IBookKnowledgeContextProvider? knowledge = null;
        if (translation.BookKnowledgeContextEnabled)
            knowledge = new BookKnowledgeContextProvider(await new BookIntelligenceStore(session).LoadAsync(cancellationToken), resources);
        return new(runtime.ModelOptions with { }, presets.Selected, resources,
            BackendSnapshot(profile, runtime.ModelOptions.Model), knowledge);
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
            await EnsureOllamaBackendReadyAsync();
            await SaveCurrentAsync();
            await SaveProjectAssetsAsync();
            ProjectTranslationCoordinator coordinator = resumable?.Workers.Count > 0 || resumable is null
                ? await RebuildParallelCoordinatorAsync(resumable, loadExisting: resumable is not null)
                : await EnsureProjectTranslationCoordinatorAsync();
            ProjectTranslationJob? existing = coordinator.CurrentJob;
            if (coordinator.IsRunning || existing?.Status is ProjectTranslationJobStatus.Paused or
                ProjectTranslationJobStatus.Interrupted or ProjectTranslationJobStatus.Ready)
            {
                HostProjectTranslationView(coordinator, null);
                return;
            }
            ProjectTranslationJob plan = await coordinator.PlanAsync(chapterIds);
            // Only detach the open chapter if it is part of the planned queue items.
            if (_service?.GetSnapshot().Id is Guid openId && chapterIds.Contains(openId))
            {
                DetachChapter();
                ClearChapterEditor();
                Refresh();
            }
            HostProjectTranslationView(coordinator, plan);
        }
        finally { EndUiOperation(); }
    }

    private async Task ApplyTranslationModeAsync(ProjectTranslationJob? recovered)
    {
        Guid profileId;
        if (recovered?.Workers.Count > 0 && !_gpuPreferences.ModeExplicitlySelected)
        {
            profileId = recovered.Workers.FirstOrDefault(worker => worker.Backend.Kind == OllamaBackendKind.Remote)?.ProfileId
                ?? OllamaBackendProfile.DefaultLocalId;
            _gpuPreferences.Mode = recovered.Workers.Count > 1
                ? GpuTranslationMode.ParallelLocalRemote
                : recovered.Workers[0].Backend.Kind == OllamaBackendKind.Remote
                    ? GpuTranslationMode.RemoteOnly : GpuTranslationMode.LocalOnly;
            if (profileId != OllamaBackendProfile.DefaultLocalId) _gpuPreferences.RemoteProfileId = profileId;
        }
        else
        {
            if (recovered?.Workers.FirstOrDefault(worker => worker.Backend.Kind == OllamaBackendKind.Remote) is { } remote &&
                _gpuPreferences.RemoteProfileId is null)
                _gpuPreferences.RemoteProfileId = remote.ProfileId;
            profileId = _gpuPreferences.Mode == GpuTranslationMode.LocalOnly
            ? OllamaBackendProfile.DefaultLocalId
            : _gpuPreferences.RemoteProfileId ?? _ollamaBackends.ActiveProfile.Id;
        }
        OllamaBackendProfile profile = _ollamaBackends.GetProfile(profileId);
        if (_ollamaBackends.ActiveProfile.Id != profileId) await _ollamaBackends.ActivateAsync(profileId);
        _selectedOllamaProfileId = profileId;
        _projectOllamaProfileMissing = false;
        SelectOllamaProfileInUi(profileId);
    }

    private async Task<ProjectTranslationCoordinator> RebuildParallelCoordinatorAsync(ProjectTranslationJob? recovered,
        bool closeWindow = true, bool loadExisting = true, bool useCurrentWorkers = false)
    {
        if (_projectSession is null) throw new InvalidOperationException("Project đã đóng.");
        if (_translationClient is OllamaBackendManager &&
            (recovered?.Workers.Any(worker => worker.Backend.Kind == OllamaBackendKind.Local) == true ||
             _gpuPreferences.Mode is GpuTranslationMode.LocalOnly or GpuTranslationMode.ParallelLocalRemote))
        {
            OllamaBackendProfile localProfile = _ollamaBackends.GetProfile(OllamaBackendProfile.DefaultLocalId);
            await _localLlama.StartAsync(localProfile);
        }
        if (closeWindow) DropHostedQueueView();
        if (_projectTranslationCoordinator is not null)
            _projectTranslationCoordinator.ProgressChanged -= OnProjectTranslationProgressChanged;
        foreach (IDisposable client in _projectTranslationOwnedClients) client.Dispose();
        _projectTranslationOwnedClients.Clear();
        var definitions = new List<ProjectTranslationWorkerDefinition>();
        if (recovered?.Workers.Count > 0 && !useCurrentWorkers)
        {
            foreach (ProjectTranslationWorkerState worker in recovered.Workers)
                definitions.Add(CreateWorkerDefinition(worker.WorkerId, worker.Backend, worker.ModelOptions,
                    worker.SegmentationOptions, worker.HourlyPrice, worker.BackendIdentity));
        }
        else
        {
            OllamaBackendProfile local = _ollamaBackends.GetProfile(OllamaBackendProfile.DefaultLocalId);
            if (_gpuPreferences.Mode is GpuTranslationMode.LocalOnly or GpuTranslationMode.ParallelLocalRemote)
            {
                AiRuntimeProfile runtime = local.Runtime ?? AiRuntimeProfile.CreateDefault(local.Model);
                definitions.Add(CreateWorkerDefinition(StableWorkerId(local.Id),
                    BackendSnapshot(local, runtime.ModelOptions.Model), runtime.ModelOptions,
                    runtime.SegmentationOptions, local.HourlyPrice));
            }
            if (_gpuPreferences.Mode is GpuTranslationMode.RemoteOnly or GpuTranslationMode.ParallelLocalRemote)
            {
                Guid remoteId = _gpuPreferences.RemoteProfileId
                    ?? throw new InvalidOperationException("Mở GPU Manager, kết nối và chọn một profile Remote trước khi dùng Remote/Parallel.");
                OllamaBackendProfile remote = _ollamaBackends.GetProfile(remoteId);
                AiRuntimeProfile runtime = remote.Runtime ?? AiRuntimeProfile.CreateDefault(remote.Model);
                definitions.Add(CreateWorkerDefinition(StableWorkerId(remote.Id),
                    BackendSnapshot(remote, runtime.ModelOptions.Model), runtime.ModelOptions,
                    runtime.SegmentationOptions, remote.HourlyPrice));
            }
        }
        var coordinator = new ProjectTranslationCoordinator(_projectSession, _projectRepository, definitions,
            cancellationToken => LoadProjectTranslationEnvironmentAsync(_projectSession, cancellationToken));
        coordinator.ProgressChanged += OnProjectTranslationProgressChanged;
        _projectTranslationCoordinator = coordinator;
        if (loadExisting)
        {
            _projectTranslationLoadTask = coordinator.LoadAsync();
            await _projectTranslationLoadTask;
            _projectTranslationLoadTask = null;
        }
        return coordinator;
    }

    private ProjectTranslationWorkerDefinition CreateWorkerDefinition(Guid workerId, OllamaBackendSnapshot backend,
        ModelOptions options, SegmenterOptions segmentation, decimal? hourlyPrice, string? expectedIdentity = null)
    {
        if (backend.Kind == OllamaBackendKind.Local)
        {
            string identity = "local:" + backend.ProfileId.ToString("N");
            EnsureBackendIdentity(expectedIdentity, identity, backend.ProfileName);
            IOllamaClient client;
            if (_translationClient is OllamaBackendManager)
            {
                Uri endpoint = _localLlama.Endpoint ?? throw new InvalidOperationException("llama-server Local chưa chạy.");
                IOllamaBackendClient localClient = new OpenAiCompatibleClient(new HttpClient(), endpoint,
                    _ollamaBackends.GetProfile(backend.ProfileId).ParallelRequests, ownsHttpClient: true);
                _projectTranslationOwnedClients.Add(localClient);
                client = localClient;
            }
            else client = _translationClient; // Deterministic injected provider used by integration tests.
            int localSlots = _translationClient is OllamaBackendManager
                ? _ollamaBackends.GetProfile(backend.ProfileId).ParallelRequests : 1;
            return new(workerId, backend, client, options with { }, segmentation with { }, hourlyPrice, identity, localSlots);
        }
        if (!_gpuConnections.TryGetConnection(backend.ProfileId, out GpuConnectionHandle? connection) ||
            connection is null || !connection.IsConnected)
            throw new InvalidOperationException($"GPU Remote “{backend.ProfileName}” chưa có SSH tunnel. Mở GPU Manager và bấm Kết nối.");
        string remoteIdentity = SshBackendIdentity(connection.Profile);
        EnsureBackendIdentity(expectedIdentity, remoteIdentity, backend.ProfileName);
        // Slots follow the connected profile: its client was built with the same number of parallel requests. Ollama
        // serves one request at a time, so extra slots there would only queue up behind each other.
        int slots = connection.Profile.Api == BackendApi.OpenAiCompatible ? connection.Profile.ParallelRequests : 1;
        return new(workerId, backend, connection.Client, options with { }, segmentation with { }, hourlyPrice, remoteIdentity, slots);
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

    private async Task<(ProjectTranslationCoordinator Coordinator, ProjectTranslationJob Plan)>
        ReplanProjectTranslationAsync(ProjectTranslationCoordinator previous)
    {
        ProjectTranslationJob unfinished = await previous.ReplanUnfinishedAsync();
        Guid[] chapterIds = unfinished.Items.OrderBy(item => item.Order).Select(item => item.ChapterId).ToArray();
        ProjectTranslationCoordinator current = await RebuildParallelCoordinatorAsync(null,
            closeWindow: false, loadExisting: false);
        ProjectTranslationJob plan = await current.PlanAsync(chapterIds);
        return (current, plan);
    }

    private async Task<ProjectTranslationCoordinator> RefreshProjectTranslationCoordinatorAsync(
        ProjectTranslationCoordinator previous)
    {
        ProjectTranslationJob? recovered = previous.CurrentJob;
        if (recovered?.Workers.Count is not > 0) return previous;

        // Resume is an explicit user action. On a fresh app launch the GPU mode is not
        // selected yet: preserve the workers saved with the job rather than defaulting
        // to Local-only and silently dropping the Remote worker.
        bool changedMode = _gpuPreferences.ModeExplicitlySelected;
        if (!changedMode)
        {
            await ApplyTranslationModeAsync(recovered);
            foreach (ProjectTranslationWorkerState worker in recovered.Workers
                         .Where(value => value.Backend.Kind == OllamaBackendKind.Remote)
                         .DistinctBy(value => value.ProfileId))
            {
                OllamaBackendProfile remote = _ollamaBackends.GetProfile(worker.ProfileId);
                if (remote.Kind != OllamaBackendKind.Remote || remote.Ssh is null ||
                    !remote.RemoteDisclosureAccepted)
                    throw new InvalidOperationException(
                        $"GPU Remote “{worker.Name}” cần được xác nhận và kết nối lại trong AI & hiệu năng.");
                // Refuse changed host/fingerprint BEFORE opening a remote connection.
                EnsureBackendIdentity(worker.BackendIdentity, SshBackendIdentity(remote), remote.Name);
                if (_gpuConnections.TryGetConnection(remote.Id, out GpuConnectionHandle? existing) &&
                    existing is { IsConnected: true })
                    continue;
                GpuConnectionHandle connection;
                try
                {
                    connection = await _gpuConnections.ConnectAsync(remote);
                }
                catch (SshHostKeyVerificationException)
                {
                    throw;
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"Không thể khôi phục GPU Remote “{remote.Name}”. Job vẫn được giữ nguyên để tiếp tục sau khi kết nối lại.",
                        error);
                }
                await _ollamaBackends.AttachRuntimeClientAsync(remote.Id, connection.Client);
            }
        }
        return await RebuildParallelCoordinatorAsync(recovered, closeWindow: false, loadExisting: true,
            useCurrentWorkers: changedMode);
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
        DropHostedQueueView();
        if (_projectTranslationCoordinator is not null)
            _projectTranslationCoordinator.ProgressChanged -= OnProjectTranslationProgressChanged;
        _projectTranslationCoordinator = null;
        _projectTranslationLoadTask = null;
        foreach (IDisposable client in _projectTranslationOwnedClients) client.Dispose();
        _projectTranslationOwnedClients.Clear();
        RefreshShellQueueStatus();
    }
}
