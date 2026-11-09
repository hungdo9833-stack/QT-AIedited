using System.IO;
using System.Windows;
using QuickTranslator.AiEditor.Core.AutoTranslate;
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
    private static readonly Guid GeminiTranslationProfileId = new("a6194896-3e9e-4f68-8725-74a9fe9bbd6c");
    /// <summary>The Codex (ChatGPT) translation worker: one per app, using the auto-translate window's Codex workspace and model.</summary>
    private static readonly Guid CodexTranslationProfileId = new("5c0d3c1a-7f2e-4b8e-9a61-0c0de7ea4a11");
    /// <summary>Chapters a book translates with Codex at once (each is its own codex exec).</summary>
    private const int CodexTranslationSlots = 3;
    /// <summary>Workspace and model for Codex translation: the last ones the auto-translate window used, at low effort
    /// (translating needs no long reasoning, and every token counts against the plan's 5-hour window).</summary>
    private CodexSurveyOptions _codexTranslationOptions = new() { Effort = "low", Timeout = TimeSpan.FromMinutes(20) };
    private GeminiTranslationClient? _activeGeminiTranslationClient;

    internal static ModelOptions GeminiTranslationModelOptions() => new()
    {
        Model = GeminiTranslationClient.PreferredModel,
        Temperature = 0.1,
        ContextSize = 131_072,
        MaximumOutputTokens = 65_536,
        RequestTimeout = TimeSpan.FromMinutes(10)
    };

    internal static SegmenterOptions GeminiTranslationSegmentationOptions() => new() { WholeChapter = true };

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
        if (_projectSession is null) throw new InvalidOperationException("Hãy mở dự án trước khi tạo hàng đợi dịch.");
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
        // A persisted multi-worker queue pins each backend independently. Otherwise (or when the project's saved
        // profile was deleted) the queue follows the profile that is active now.
        bool pinnedWorkers = _projectTranslationCoordinator?.CurrentJob?.Workers.Count > 0;
        Guid projectProfileId = translation.GpuProfileId ?? GpuBackendProfile.DefaultLocalId;
        GpuBackendProfile profile = pinnedWorkers && GpuProfileExists(projectProfileId)
            ? _gpuBackends.GetProfile(projectProfileId) : _gpuBackends.ActiveProfile;
        AiRuntimeProfile runtime = profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model);
        IBookKnowledgeContextProvider? knowledge = null;
        if (translation.BookKnowledgeContextEnabled)
            knowledge = new BookKnowledgeContextProvider(await new BookIntelligenceStore(session).LoadAsync(cancellationToken), resources);
        return new(runtime.ModelOptions with { }, presets.Selected, resources,
            BackendSnapshot(profile, runtime.ModelOptions.Model), knowledge);
    }

    /// <summary>
    /// The environment of a book translating in the background on its own lane: the project's prompt preset, resources and
    /// knowledge as usual, with the model and backend of the lane's first worker instead of the window's active GPU.
    /// </summary>
    private async Task<ProjectTranslationEnvironment> LoadLaneTranslationEnvironmentAsync(ProjectSession session,
        ProjectTranslationWorkerDefinition first, CancellationToken cancellationToken)
    {
        ProjectTranslationSettings translation = await new ProjectSettingsStore(session).LoadTranslationAsync(cancellationToken);
        var presets = new PromptPresetManager(await _presetStore.LoadAsync(cancellationToken));
        presets.Select(translation.SelectedPromptPresetId is Guid selectedId && presets.Presets.Any(p => p.Id == selectedId)
            ? selectedId : PromptPreset.Default.Id);
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync(cancellationToken);
        IBookKnowledgeContextProvider? knowledge = translation.BookKnowledgeContextEnabled
            ? new BookKnowledgeContextProvider(await new BookIntelligenceStore(session).LoadAsync(cancellationToken), resources)
            : null;
        return new(first.ModelOptions with { }, presets.Selected, resources, first.Backend with { }, knowledge);
    }

    private Task OpenProjectTranslationQueueAsync(IReadOnlyList<Guid> chapterIds) =>
        OpenProjectTranslationQueueCoreAsync(chapterIds, askRetranslation: true);

    /// <param name="askRetranslation">False (automatic runs): translate only what is missing without asking.</param>
    private async Task OpenProjectTranslationQueueCoreAsync(IReadOnlyList<Guid> chapterIds, bool askRetranslation)
    {
        if (_projectSession is null) throw new InvalidOperationException("Dự án đã đóng.");
        if (!BeginUiOperation(allowDuringTranslation: true))
            throw new InvalidOperationException("Ứng dụng đang xử lý thao tác khác; hãy thử lại sau.");
        try
        {
            ProjectTranslationCoordinator initial = await EnsureProjectTranslationCoordinatorAsync();
            if (initial.IsRunning)
            {
                HostProjectTranslationView(initial, null);
                SetStatus("Hàng đợi đang dịch; hãy Tạm dừng rồi thêm chương.");
                return;
            }
            ProjectTranslationJob? recovered = initial.CurrentJob;
            ProjectTranslationJob? resumable = recovered?.Status is ProjectTranslationJobStatus.Completed or
                ProjectTranslationJobStatus.CompletedWithErrors or ProjectTranslationJobStatus.Cancelled ? null : recovered;
            bool staleWorkers = resumable is not null && StaleWorkerProfiles(resumable).Count > 0;
            if (staleWorkers && !_gpuPreferences.ModeExplicitlySelected) KeepRecoveredModeForCurrentGpus(resumable!);
            await ApplyTranslationModeAsync(resumable);
            await EnsureGpuBackendReadyAsync();
            await SaveCurrentAsync();
            await SaveProjectAssetsAsync();
            // When the user picked a GPU mode, or a saved worker's GPU was deleted or now points at another machine,
            // build the workers from the current setup; Tiếp tục then moves the job onto them.
            bool currentWorkers = _gpuPreferences.ModeExplicitlySelected || staleWorkers;
            if (staleWorkers && _gpuPreferences.Source == TranslationSource.Gpu &&
                _gpuPreferences.Mode is GpuTranslationMode.RemoteOnly or GpuTranslationMode.ParallelLocalRemote &&
                CurrentRemoteProfileId() is Guid remoteId)
                await EnsureRemoteConnectedAsync(_gpuBackends.GetProfile(remoteId));
            ProjectTranslationCoordinator coordinator = resumable?.Workers.Count > 0 || resumable is null
                ? await RebuildParallelCoordinatorAsync(resumable, loadExisting: resumable is not null,
                    useCurrentWorkers: currentWorkers)
                : await EnsureProjectTranslationCoordinatorAsync();
            ProjectTranslationJob? existing = coordinator.CurrentJob;
            if (existing?.Status is ProjectTranslationJobStatus.Paused or
                ProjectTranslationJobStatus.Interrupted or ProjectTranslationJobStatus.Ready)
            {
                await AppendToProjectTranslationQueueAsync(coordinator, existing, chapterIds, askRetranslation);
                return;
            }
            if ((askRetranslation ? AskRetranslation(chapterIds) : new HashSet<Guid>()) is not { } retranslate) return;
            // "Có" retranslates everything chosen, the user's edited and approved segments included.
            ProjectTranslationJob plan = await coordinator.PlanAsync(chapterIds, retranslateAll: retranslate,
                overrideUserEdits: retranslate.Count > 0);
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

    /// <summary>
    /// A paused, interrupted or ready job gets the chosen chapters at its end (asking about retranslation first) instead
    /// of ignoring them. Chapters still waiting in the job are left as they are; Tiếp tục then translates the rest.
    /// </summary>
    private async Task AppendToProjectTranslationQueueAsync(ProjectTranslationCoordinator coordinator,
        ProjectTranslationJob existing, IReadOnlyList<Guid> chapterIds, bool askRetranslation = true)
    {
        Guid[] candidates = chapterIds.Where(id => existing.Items.FirstOrDefault(item => item.ChapterId == id) is not { } item ||
            item.Status is ProjectTranslationItemStatus.Completed or ProjectTranslationItemStatus.Skipped or
                ProjectTranslationItemStatus.Failed or ProjectTranslationItemStatus.NeedsReview or ProjectTranslationItemStatus.Cancelled ||
            // A waiting chapter that already holds failed segments can only finish if it is retranslated in full.
            _projectSession!.Manifest.Chapters.Any(entry => entry.ChapterId == id && entry.State == ProjectChapterState.Error))
            .ToArray();
        if (candidates.Length == 0)
        {
            HostProjectTranslationView(coordinator, null);
            SetStatus("Các chương đã chọn đều đang chờ trong hàng đợi. Bấm Tiếp tục để dịch.");
            return;
        }
        if ((askRetranslation ? AskRetranslation(candidates) : new HashSet<Guid>()) is not { } retranslate) return;
        ProjectTranslationAppendResult result = await coordinator.AppendAsync(candidates, retranslate,
            overrideUserEdits: retranslate.Count > 0);
        HostProjectTranslationView(coordinator, null);
        SetStatus(result.Added == 0 && result.Reopened == 0
            ? "Không có chương mới để thêm: các chương đã chọn đã được dịch trong hàng đợi này."
            : $"Đã thêm {result.Added} chương vào cuối hàng đợi" +
              (result.Reopened > 0 ? $", mở lại {result.Reopened} chương để dịch lại" : "") + ". Bấm Tiếp tục để dịch.");
    }

    /// <summary>
    /// Chapters that already have translated segments would only be skipped ("Đã có bản dịch"). Ask once whether to
    /// translate them again from scratch. Returns the chapters to retranslate (empty: only missing segments), or null
    /// when the user cancels and no queue should be created.
    /// </summary>
    private IReadOnlySet<Guid>? AskRetranslation(IReadOnlyList<Guid> chapterIds)
    {
        ProjectChapterEntry[] translated = _projectSession!.Manifest.Chapters
            // A chapter with failed segments is offered too: the queue never retries a failed attempt on its own.
            .Where(entry => entry.DeletedAt is null && (entry.CompletedSegmentCount > 0 || entry.State == ProjectChapterState.Error) &&
                chapterIds.Contains(entry.ChapterId))
            .OrderBy(entry => entry.Number).ToArray();
        if (translated.Length == 0) return new HashSet<Guid>();
        int complete = translated.Count(entry => entry.CompletedSegmentCount >= entry.SegmentCount);
        string names = translated.Length == 1 ? $"Chương {translated[0].Number}: {translated[0].VietnameseTitle}"
            : $"{translated.Length} chương ({complete} chương đã dịch xong toàn bộ), ví dụ chương " +
              string.Join(", ", translated.Take(5).Select(entry => entry.Number)) + (translated.Length > 5 ? "…" : "");
        MessageBoxResult answer = ThemedDialog.Show(this,
            $"{names} đã có bản dịch.\n\nDịch lại toàn bộ?\n\n" +
            "• Có: dịch lại TẤT CẢ các đoạn đã chọn bằng AI, kể cả đoạn bạn đã sửa tay hoặc đã duyệt. " +
            "Bản cũ (cả bản sửa và bản duyệt) vẫn còn trong lịch sử chia đoạn của chương.\n" +
            "• Không: chỉ dịch các đoạn còn thiếu; đoạn đã sửa/duyệt giữ nguyên, chương đã dịch đủ sẽ được bỏ qua.\n" +
            "• Hủy: không tạo hàng đợi.",
            "Chương đã có bản dịch", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => translated.Select(entry => entry.ChapterId).ToHashSet(),
            MessageBoxResult.No => new HashSet<Guid>(),
            _ => null
        };
    }

    private async Task ApplyTranslationModeAsync(ProjectTranslationJob? recovered)
    {
        if (recovered?.Workers.Any(worker => IsGeminiProfile(worker.ProfileId)) == true &&
            !_gpuPreferences.ModeExplicitlySelected)
        {
            _gpuPreferences.Source = TranslationSource.Api;
            _gpuPreferences.ApiProvider = recovered.Workers.Any(worker => worker.ProfileId == CodexTranslationProfileId)
                ? ApiTranslationProvider.Codex : ApiTranslationProvider.Gemini;
        }
        if (_gpuPreferences.UsesCodex)
        {
            EnsureCodexSignedIn();
            UpdateGpuBackendStatus();
            return;
        }
        if (_gpuPreferences.Source == TranslationSource.Api)
        {
            if (GeminiApiKeyVault.ReadAll().Count == 0)
                throw new InvalidOperationException(
                    "Chưa có Gemini API key. Mở AI & hiệu năng hoặc Cài đặt → Gemini để thêm key trước khi dịch.");
            UpdateGpuBackendStatus();
            return;
        }
        Guid profileId;
        if (recovered?.Workers.Count > 0 && !_gpuPreferences.ModeExplicitlySelected &&
            StaleWorkerProfiles(recovered).Count == 0)
        {
            profileId = recovered.Workers.FirstOrDefault(worker => worker.Backend.Kind == GpuBackendKind.Remote)?.ProfileId
                ?? GpuBackendProfile.DefaultLocalId;
            _gpuPreferences.Mode = recovered.Workers.Count > 1
                ? GpuTranslationMode.ParallelLocalRemote
                : recovered.Workers[0].Backend.Kind == GpuBackendKind.Remote
                    ? GpuTranslationMode.RemoteOnly : GpuTranslationMode.LocalOnly;
            if (profileId != GpuBackendProfile.DefaultLocalId) _gpuPreferences.RemoteProfileId = profileId;
        }
        else
        {
            if (recovered?.Workers.FirstOrDefault(worker => worker.Backend.Kind == GpuBackendKind.Remote) is { } remote &&
                _gpuPreferences.RemoteProfileId is null && GpuProfileExists(remote.ProfileId))
                _gpuPreferences.RemoteProfileId = remote.ProfileId;
            profileId = _gpuPreferences.Mode == GpuTranslationMode.LocalOnly
                ? GpuBackendProfile.DefaultLocalId
                : CurrentRemoteProfileId() ?? _gpuBackends.ActiveProfile.Id;
        }
        GpuBackendProfile profile = _gpuBackends.GetProfile(profileId);
        if (_gpuBackends.ActiveProfile.Id != profileId) await _gpuBackends.ActivateAsync(profileId);
        _selectedGpuProfileId = profileId;
        _projectGpuProfileMissing = false;
        SelectGpuProfileInUi(profileId);
    }

    private async Task<ProjectTranslationCoordinator> RebuildParallelCoordinatorAsync(ProjectTranslationJob? recovered,
        bool closeWindow = true, bool loadExisting = true, bool useCurrentWorkers = false)
    {
        if (_projectSession is null) throw new InvalidOperationException("Dự án đã đóng.");
        if (_projectTranslationCoordinator?.IsRunning == true)
            throw new InvalidOperationException("Hàng đợi đang dịch; hãy Tạm dừng rồi thêm chương.");
        if (_gpuPreferences.Source == TranslationSource.Gpu && _translationClient is GpuBackendManager &&
            (!useCurrentWorkers && recovered?.Workers.Any(worker => worker.Backend.Kind == GpuBackendKind.Local) == true ||
             _gpuPreferences.Mode is GpuTranslationMode.LocalOnly or GpuTranslationMode.ParallelLocalRemote))
        {
            GpuBackendProfile localProfile = _gpuBackends.GetProfile(GpuBackendProfile.DefaultLocalId);
            await _localLlama.StartAsync(localProfile);
        }
        if (_projectTranslationCoordinator?.IsRunning == true)
            throw new InvalidOperationException("Hàng đợi đang dịch; hãy Tạm dừng rồi thêm chương.");
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
                    worker.SegmentationOptions, worker.HourlyPrice));
        }
        else
        {
            GpuBackendProfile local = _gpuBackends.GetProfile(GpuBackendProfile.DefaultLocalId);
            if (_gpuPreferences.UsesCodex)
                definitions.Add(CodexWorkerDefinition(CodexTranslationProfileId));
            else if (_gpuPreferences.Source == TranslationSource.Api)
            {
                ModelOptions options = GeminiTranslationModelOptions();
                // Cloud API behaves like a remote worker for coordinator fault semantics: one terminal
                // quota failure takes the worker offline instead of failing every remaining chapter.
                var backend = new GpuBackendSnapshot(GeminiTranslationProfileId, GpuBackendKind.Remote,
                    "Gemini API", "https://generativelanguage.googleapis.com", options.Model);
                definitions.Add(CreateWorkerDefinition(GeminiTranslationProfileId, backend, options,
                    GeminiTranslationSegmentationOptions(), null));
            }
            else if (_gpuPreferences.Mode is GpuTranslationMode.LocalOnly or GpuTranslationMode.ParallelLocalRemote)
                definitions.Add(ProjectTranslationProfileWorker(local));
            if (_gpuPreferences.Mode is GpuTranslationMode.RemoteOnly or GpuTranslationMode.ParallelLocalRemote)
            {
                Guid remoteId = CurrentRemoteProfileId()
                    ?? throw new InvalidOperationException("Mở GPU Manager, kết nối và chọn một profile Remote trước khi dùng Remote/Parallel.");
                definitions.Add(ProjectTranslationProfileWorker(_gpuBackends.GetProfile(remoteId)));
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

    // A fresh worker for a GPU profile, using the profile's runtime settings (or its model's defaults).
    private ProjectTranslationWorkerDefinition ProjectTranslationProfileWorker(GpuBackendProfile profile,
        List<IDisposable>? ownedClients = null)
    {
        AiRuntimeProfile runtime = profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model);
        return CreateWorkerDefinition(StableWorkerId(profile.Id),
            BackendSnapshot(profile, runtime.ModelOptions.Model), runtime.ModelOptions,
            runtime.SegmentationOptions, profile.HourlyPrice, ownedClients);
    }

    /// <summary>One saved Gemini key as a worker: only that key, 3.5 Flash-Lite then 3.1 when its daily quota is used.</summary>
    private ProjectTranslationWorkerDefinition GeminiKeyWorkerDefinition(GeminiKeyWorker key, List<IDisposable> ownedClients)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var client = new GeminiTranslationClient(http, [key.Key], ownsHttpClient: true);
        ownedClients.Add(client);
        ModelOptions options = GeminiTranslationModelOptions();
        var backend = new GpuBackendSnapshot(key.ProfileId, GpuBackendKind.Remote, "Gemini · " + key.Name,
            "https://generativelanguage.googleapis.com", options.Model);
        return new(key.ProfileId, backend, WithHanRepair(client, ownedClients), options, GeminiTranslationSegmentationOptions(), null,
            "gemini-api:key:" + key.Fingerprint, 1);
    }

    /// <summary>Gemini leaves a little Chinese now and then: fix only those spots (names in Hán-Việt, the paragraph again
    /// on this machine's GPU when it runs) instead of sending the chapter again.</summary>
    private HanResidueRepairClient WithHanRepair(ILlmClient translator, List<IDisposable> ownedClients, bool gpu = false)
    {
        var gate = new object();
        LlamaServerClient? local = null;
        Uri? localEndpoint = null;
        HanRepairTranslator? Local()
        {
            if (_translationClient is not GpuBackendManager || _localLlama.Endpoint is not { } endpoint) return null;
            GpuBackendProfile profile = _gpuBackends.GetProfile(GpuBackendProfile.DefaultLocalId);
            lock (gate)
            {
                if (local is null || localEndpoint != endpoint)
                {
                    local = new LlamaServerClient(new HttpClient(), endpoint, profile.ParallelRequests, ownsHttpClient: true);
                    localEndpoint = endpoint;
                    lock (ownedClients) ownedClients.Add(local);
                }
                return new(local, (profile.Runtime ?? AiRuntimeProfile.CreateDefault(profile.Model)).ModelOptions);
            }
        }
        // A GPU worker repairs on itself (the paragraph goes back to the same server), so no local translator is borrowed.
        return new HanResidueRepairClient(translator, gpu ? () => null : Local, DictionaryHanViet, askForNoHan: !gpu,
            vietPhrase: DictionaryVietPhrase);
    }

    private async Task<string> DictionaryHanViet(string text, CancellationToken cancellationToken) =>
        _traditionalService.CurrentSnapshot is null ? ""
            : (await _traditionalService.TranslateAsync(text, cancellationToken: cancellationToken)).HanVietText;

    private async Task<string> DictionaryVietPhrase(string text, CancellationToken cancellationToken) =>
        _traditionalService.CurrentSnapshot is null ? ""
            : (await _traditionalService.TranslateAsync(text, cancellationToken: cancellationToken)).VietPhraseText;

    /// <param name="ownedClients">Where the clients this worker creates are kept for disposal: the open project's queue
    /// by default; a background lane passes its own list so rebuilding the window's queue never closes them.</param>
    /// <summary>A Codex (ChatGPT) worker on the workspace and model last used by the auto-translate window.</summary>
    private ProjectTranslationWorkerDefinition CodexWorkerDefinition(Guid workerId, List<IDisposable>? ownedClients = null)
    {
        CodexSurveyOptions codex = _codexTranslationOptions;
        EnsureCodexSignedIn();
        var backend = new GpuBackendSnapshot(CodexTranslationProfileId, GpuBackendKind.Remote, "Codex · " + codex.Model,
            "https://chatgpt.com", "codex:" + codex.Model);
        return CreateWorkerDefinition(workerId, backend, GeminiTranslationModelOptions(),
            GeminiTranslationSegmentationOptions(), null, ownedClients);
    }

    private void EnsureCodexSignedIn()
    {
        string home = _codexTranslationOptions.CodexHome;
        if (!File.Exists(Path.Combine(home, "auth.json")))
            throw new InvalidOperationException($"Codex chưa đăng nhập trong {home}. Hãy đăng nhập Codex ở terminal trước.");
    }

    private string CodexSourceLabel => "Codex · " + _codexTranslationOptions.Model + " · " +
        Path.GetFileName(_codexTranslationOptions.CodexHome).Replace(".codex-", "", StringComparison.Ordinal);

    private ProjectTranslationWorkerDefinition CreateWorkerDefinition(Guid workerId, GpuBackendSnapshot backend,
        ModelOptions options, SegmenterOptions segmentation, decimal? hourlyPrice, List<IDisposable>? ownedClients = null)
    {
        bool windowQueue = ownedClients is null;
        ownedClients ??= _projectTranslationOwnedClients;
        if (backend.ProfileId == CodexTranslationProfileId)
        {
            CodexSurveyOptions codex = _codexTranslationOptions;
            var client = new CodexTranslationClient(codex);
            ModelOptions codexOptions = GeminiTranslationModelOptions() with { Model = "codex:" + codex.Model };
            GpuBackendSnapshot codexBackend = backend with { Model = codexOptions.Model };
            return new(workerId, codexBackend, WithHanRepair(client, ownedClients), codexOptions,
                GeminiTranslationSegmentationOptions(), hourlyPrice,
                "codex:" + Path.GetFileName(codex.CodexHome) + ":" + codex.Model, CodexTranslationSlots);
        }
        if (backend.ProfileId == GeminiTranslationProfileId)
        {
            const string identity = "gemini-api:flash-lite-pool-v1";
            var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            var client = new GeminiTranslationClient(http, GeminiApiKeyVault.ReadAll(), ownsHttpClient: true);
            if (windowQueue)
            {
                client.RouteChanged += (_, _) => Dispatcher.BeginInvoke(UpdateGpuBackendStatus);
                _activeGeminiTranslationClient = client;
            }
            ownedClients.Add(client);
            ModelOptions geminiOptions = GeminiTranslationModelOptions();
            SegmenterOptions wholeChapter = GeminiTranslationSegmentationOptions();
            GpuBackendSnapshot geminiBackend = backend with { Model = geminiOptions.Model };
            return new(workerId, geminiBackend, WithHanRepair(client, ownedClients), geminiOptions, wholeChapter, hourlyPrice, identity, 1);
        }
        if (backend.Kind == GpuBackendKind.Local)
        {
            string identity = "local:" + backend.ProfileId.ToString("N");
            ILlmClient client;
            if (_translationClient is GpuBackendManager)
            {
                Uri endpoint = _localLlama.Endpoint ?? throw new InvalidOperationException("llama-server Local chưa chạy.");
                ILlmBackendClient localClient = new LlamaServerClient(new HttpClient(), endpoint,
                    _gpuBackends.GetProfile(backend.ProfileId).ParallelRequests, ownsHttpClient: true);
                ownedClients.Add(localClient);
                client = WithHanRepair(localClient, ownedClients, gpu: true);
            }
            else client = _translationClient; // Deterministic injected provider used by integration tests.
            int localSlots = _translationClient is GpuBackendManager
                ? _gpuBackends.GetProfile(backend.ProfileId).ParallelRequests : 1;
            return new(workerId, backend, client, options with { }, segmentation with { }, hourlyPrice, identity, localSlots);
        }
        if (!_gpuConnections.TryGetConnection(backend.ProfileId, out GpuConnectionHandle? connection) ||
            connection is null || !connection.IsConnected)
            throw new InvalidOperationException($"GPU Remote “{backend.ProfileName}” chưa có SSH tunnel. Mở GPU Manager và bấm Kết nối.");
        string remoteIdentity = SshBackendIdentity(connection.Profile);
        // Slots follow the connected profile: its client was built with the same number of parallel requests.
        int slots = connection.Profile.ParallelRequests;
        // The connection's client is shared and owned by the connection: the wrapper is never disposed by the queue.
        return new(workerId, backend, WithHanRepair(connection.Client, ownedClients, gpu: true), options with { }, segmentation with { }, hourlyPrice, remoteIdentity, slots);
    }

    private static string SshBackendIdentity(GpuBackendProfile profile)
    {
        GpuSshSettings ssh = profile.Ssh ?? throw new InvalidOperationException("Profile Remote thiếu cấu hình SSH.");
        string value = $"{profile.Id:N}|{ssh.Host.ToLowerInvariant()}|{ssh.Port}|{ssh.Username}|{ssh.ServerPort}|{ssh.HostKeyFingerprint}";
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
        if (!changedMode && StaleWorkerProfiles(recovered) is { Count: > 0 } stale)
        {
            // The saved GPU was deleted or now points at another machine (e.g. a new rental box). Resume is the
            // user's explicit action, so the rest of the job simply moves to the current GPU setup.
            KeepRecoveredModeForCurrentGpus(recovered);
            string names = string.Join(", ", stale.Select(worker => $"“{worker.Name}”"));
            SetStatus($"GPU {names} của hàng đợi đã đổi; tiếp tục bằng {DescribeCurrentGpuSetup()}.");
            changedMode = true;
        }
        if (!changedMode)
        {
            await ApplyTranslationModeAsync(recovered);
            foreach (ProjectTranslationWorkerState worker in recovered.Workers
                         .Where(value => value.Backend.Kind == GpuBackendKind.Remote)
                         .Where(value => !IsGeminiProfile(value.ProfileId))
                         .DistinctBy(value => value.ProfileId))
                await EnsureRemoteConnectedAsync(_gpuBackends.GetProfile(worker.ProfileId));
        }
        else if (_gpuPreferences.Source == TranslationSource.Gpu &&
                 _gpuPreferences.Mode is GpuTranslationMode.RemoteOnly or GpuTranslationMode.ParallelLocalRemote &&
                 CurrentRemoteProfileId() is Guid remoteId)
        {
            await ApplyTranslationModeAsync(null);
            await EnsureRemoteConnectedAsync(_gpuBackends.GetProfile(remoteId));
        }
        return await RebuildParallelCoordinatorAsync(recovered, closeWindow: false, loadExisting: true,
            useCurrentWorkers: changedMode);
    }

    private async Task EnsureRemoteConnectedAsync(GpuBackendProfile remote)
    {
        if (remote.Kind != GpuBackendKind.Remote || remote.Ssh is null || !remote.RemoteDisclosureAccepted)
            throw new InvalidOperationException(
                $"GPU Remote “{remote.Name}” cần được xác nhận và kết nối lại trong AI & hiệu năng.");
        if (_gpuConnections.TryGetConnection(remote.Id, out GpuConnectionHandle? existing) &&
            existing is { IsConnected: true })
            return;
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
        await _gpuBackends.AttachRuntimeClientAsync(remote.Id, connection.Client);
    }

    /// <summary>
    /// Before a stale job moves to the current GPUs on a fresh launch (no mode picked yet), keep the job's own shape:
    /// a Remote job stays on Remote, using the current Remote card, instead of falling back to this machine's GPU.
    /// </summary>
    private void KeepRecoveredModeForCurrentGpus(ProjectTranslationJob recovered)
    {
        if (_gpuPreferences.RemoteProfileId is Guid saved && !GpuProfileExists(saved))
            _gpuPreferences.RemoteProfileId = null;
        if (recovered.Workers.Any(worker => IsGeminiProfile(worker.ProfileId))) return;
        bool hadRemote = recovered.Workers.Any(worker => worker.Backend.Kind == GpuBackendKind.Remote);
        bool hadLocal = recovered.Workers.Any(worker => worker.Backend.Kind == GpuBackendKind.Local);
        if (!hadRemote || CurrentRemoteProfileId() is not Guid remoteId) return;
        _gpuPreferences.RemoteProfileId = remoteId;
        _gpuPreferences.Mode = hadLocal ? GpuTranslationMode.ParallelLocalRemote : GpuTranslationMode.RemoteOnly;
    }

    /// <summary>The Remote card to use now: the chosen one, else the active Remote profile, else the first Remote profile.</summary>
    private Guid? CurrentRemoteProfileId() =>
        _gpuPreferences.RemoteProfileId is Guid chosen && GpuProfileExists(chosen) ? chosen
        : _gpuBackends.ActiveProfile.Kind == GpuBackendKind.Remote ? _gpuBackends.ActiveProfile.Id
        : _gpuBackends.Catalog.Profiles.FirstOrDefault(profile => profile.Kind == GpuBackendKind.Remote)?.Id;

    private bool GpuProfileExists(Guid profileId) => IsGeminiProfile(profileId) ||
        _gpuBackends.Catalog.Profiles.Any(profile => profile.Id == profileId);

    /// <summary>Saved workers whose GPU profile was deleted, or whose Remote profile now points at another machine.</summary>
    private List<ProjectTranslationWorkerState> StaleWorkerProfiles(ProjectTranslationJob job) =>
        job.Workers.Where(worker => !GpuProfileExists(worker.ProfileId) || RemoteMachineChanged(worker))
            .DistinctBy(worker => worker.ProfileId).ToList();

    private bool RemoteMachineChanged(ProjectTranslationWorkerState worker)
    {
        if (worker.Backend.Kind != GpuBackendKind.Remote || IsGeminiProfile(worker.ProfileId)) return false;
        GpuBackendProfile profile = _gpuBackends.GetProfile(worker.ProfileId);
        return profile.Kind != GpuBackendKind.Remote || profile.Ssh is null ||
            worker.BackendIdentity.Length > 0 &&
            !string.Equals(worker.BackendIdentity, SshBackendIdentity(profile), StringComparison.Ordinal);
    }

    private string DescribeCurrentGpuSetup()
    {
        if (_gpuPreferences.UsesCodex) return CodexSourceLabel;
        if (_gpuPreferences.Source == TranslationSource.Api)
        {
            int count = GeminiApiKeyVault.ReadAll().Count;
            return _activeGeminiTranslationClient?.CurrentRoute.DisplayText ??
                $"API · Gemini 3.5 Flash-Lite (key {(count > 0 ? 1 : 0)}/{count})";
        }
        string remote = _gpuPreferences.RemoteProfileId is Guid id && GpuProfileExists(id)
            ? $"Remote “{_gpuBackends.GetProfile(id).Name}”" : "Remote (chưa chọn card)";
        return _gpuPreferences.Mode switch
        {
            GpuTranslationMode.LocalOnly => "chỉ GPU máy này",
            GpuTranslationMode.RemoteOnly => "chỉ " + remote,
            _ => "GPU máy này + " + remote
        };
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
        // Also called while the window closes: never throw here, but leave a still-running queue's clients to it.
        bool running = _projectTranslationCoordinator?.IsRunning == true;
        DropHostedQueueView();
        if (_projectTranslationCoordinator is not null)
            _projectTranslationCoordinator.ProgressChanged -= OnProjectTranslationProgressChanged;
        _projectTranslationCoordinator = null;
        _projectTranslationLoadTask = null;
        if (!running) foreach (IDisposable client in _projectTranslationOwnedClients) client.Dispose();
        _projectTranslationOwnedClients.Clear();
        _activeGeminiTranslationClient = null;
        RefreshShellQueueStatus();
    }
}
