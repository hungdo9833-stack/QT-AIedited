using System.IO;
using System.Text.Json;
using System.Windows;
using QuickTranslator.AiEditor.Core.AutoTranslate;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private AutoTranslateWindow? _autoTranslateWindow;
    internal Func<CodexSurveyOptions, CodexSurveyRunner> CodexSurveyRunnerFactory = options => new CodexSurveyRunner(options);
    /// <summary>Wait before a throttled download continues on its own.</summary>
    internal TimeSpan AutoRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxStalledRetries = 5;

    private void OnAutoTranslateFromLinks(object sender, RoutedEventArgs e)
    {
        if (_autoTranslateWindow is { IsLoaded: true })
        {
            _autoTranslateWindow.Activate();
            return;
        }
        _autoTranslateWindow = new AutoTranslateWindow(new AutoBookStages(this), AutoTranslationWorkerChoices) { Owner = this };
        _autoTranslateWindow.Closed += (_, _) => _autoTranslateWindow = null;
        _autoTranslateWindow.Show();
    }

    /// <summary>The work of each stage of <see cref="AutoBookPipeline"/>, done in this window (projects, library, queue).</summary>
    private sealed class AutoBookStages(MainWindow window) : IAutoBookStages
    {
        public Task<AutoBookExisting?> FindExistingAsync(AutoBookRow row, AutoTranslateSettings settings, CancellationToken cancellationToken)
        {
            row.Stage = "Tìm dự án đã có";
            string[] manifests = window._projectLibrary.RecentProjects.Where(project => !project.Missing)
                .Select(project => project.ManifestPath).ToArray();
            return window.CreateAutoBookPreparer().FindExistingAsync(row.Url, manifests, settings.DownloadDelay, cancellationToken);
        }

        public async Task<AutoBookDownload?> DownloadAsync(AutoBookRow row, AutoTranslateSettings settings, CancellationToken cancellationToken)
        {
            var status = new Progress<AutoBookStatus>(value =>
            {
                row.Stage = value.Stage;
                row.Detail = value.Detail;
                if (value.Title is not null) row.Title = value.Title;
            });
            try
            {
                AutoBookDownload download = await window.CreateAutoBookPreparer()
                    .DownloadAsync(row.Url, settings.DownloadDelay, status, cancellationToken);
                await window.SettleAsync(() =>
                {
                    row.Title = download.Title;
                    row.Stage = "Đã tải, chờ Codex";
                    row.Detail = "";
                });
                return download;
            }
            catch (AutoBookPausedException paused)
            {
                // A throttle passes: download again on its own, unless several waits in a row brought no new chapter.
                // A login wall never passes, so it waits for the user.
                row.StalledRetries = paused.Downloaded > row.LastDownloaded ? 0 : row.StalledRetries + 1;
                row.LastDownloaded = paused.Downloaded;
                bool again = paused.Reason == Core.WebImport.WebImportBlockReason.RateLimited && row.StalledRetries < MaxStalledRetries;
                row.RetryAfter = again ? window.AutoRetryDelay : null;
                await window.SettleAsync(() =>
                {
                    row.Title = paused.Title;
                    row.Stage = $"⏸ Tạm dừng ({paused.Downloaded}/{paused.Total})";
                    row.Detail = again
                        ? $"Web chặn vì tải quá nhanh; tự tải tiếp sau {window.AutoRetryDelay.TotalSeconds:N0} giây (dừng ở {paused.Downloaded}/{paused.Total})."
                        : paused.Message + (paused.Reason == Core.WebImport.WebImportBlockReason.RateLimited
                            ? $" Đã tự thử {MaxStalledRetries} lần không tải thêm được chương nào; bấm Thử lại khi muốn."
                            : "");
                });
                return null;
            }
        }

        public async Task<string?> PrepareAsync(AutoBookRow row, AutoBookDownload download, AutoTranslateSettings settings,
            CancellationToken cancellationToken)
        {
            try { return await window.PrepareAutoBookAsync(window.CreateAutoBookPreparer(), download, row, settings, cancellationToken); }
            catch (ContentStopException stop)
            {
                // Stopped for good: retrying would only stop again.
                row.Finished = true;
                await window.SettleAsync(() =>
                {
                    row.Stage = "⛔ Đã dừng";
                    row.Detail = stop.Message;
                });
                window.ShowContentReport(row, stop.Report, stopped: true);
                return null;
            }
        }

        public async Task<bool> TranslateAsync(AutoBookRow row, string manifestPath, CancellationToken cancellationToken)
        {
            try
            {
                await window.TranslateAutoBookOnLaneAsync(row, manifestPath, cancellationToken);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.Stage = "Lỗi dịch";
                row.Detail = ex.Message;
                return false;
            }
        }

        public IReadOnlySet<string> TranslationResources(AutoBookRow row) => window.LaneResources(row.LaneId);
    }

    /// <summary>Progress reports are posted: applies the final state of a stage after the last report has landed.</summary>
    private Task SettleAsync(Action apply) =>
        Dispatcher.InvokeAsync(apply, System.Windows.Threading.DispatcherPriority.ContextIdle).Task;

    private AutoBookPreparer CreateAutoBookPreparer() => new(_projectRepository, WebNovelImporterFactory, CodexSurveyRunnerFactory,
        new BulkImportChapterDefaults(ReadSegmenterOptions(), ReadModelOptions(), _presetManager.Selected))
    {
        TitleBackend = _traditionalService.CurrentSnapshot is null ? null
            : new TraditionalChapterTitleTranslationBackend(_traditionalService, hanViet: false)
    };

    /// <summary>Creates the project of a downloaded book and saves the Codex characters and glossary. Returns the manifest path.</summary>
    private async Task<string> PrepareAutoBookAsync(AutoBookPreparer preparer, AutoBookDownload download, AutoBookRow row,
        AutoTranslateSettings settings, CancellationToken cancellationToken)
    {
        bool warned = false;
        var status = new Progress<AutoBookStatus>(value =>
        {
            row.Stage = (warned ? "⚠ " : "") + value.Stage;
            row.Detail = value.Detail;
            if (value.Title is not null) row.Title = value.Title;
        });
        // Shown the moment Codex's content scan reports, while the survey still runs. A stop is shown by the caller.
        var content = new Progress<ContentFlagReport>(report =>
        {
            if (report.MustStop) return;
            row.Stage = "⚠ Cảnh báo nội dung";
            row.Detail = $"{report.Flags.Count} sự kiện: " + string.Join("; ", report.Flags.Select(f => $"{f.TypeLabel} ({f.Chapter})"));
            if (!warned) ShowContentReport(row, report, stopped: false);
            warned = true;
        });
        // Codex translation follows the workspace and model chosen here (its effort stays low).
        _codexTranslationOptions = _codexTranslationOptions with { CodexHome = settings.Codex.CodexHome, Model = settings.Codex.Model };
        CodexSurveyOptions codex = settings.Codex with { KnownGenres = await KnownGenresAsync(cancellationToken) };
        preparer.AdvancedEditing = settings.AdvancedEditing;
        AutoBookPrepared prepared = await preparer.PrepareAsync(download, _projectLibrary.DefaultProjectsDirectory,
            codex, status, cancellationToken, content);
        row.Title = prepared.Title;
        // A prepared book is never opened in "chỉ chuẩn bị" mode: list it in the project library now so it can be found.
        if (_projectLibraryReady)
        {
            ProjectLibraryStore.Remember(_projectLibrary, prepared.Session);
            await _projectLibraryStore.SaveAsync(_projectLibrary);
            RefreshProjectSwitcher();
        }
        row.Stage = settings.PrepareOnly ? "Đã chuẩn bị" : "Chờ dịch";
        row.Detail = prepared.Summary;
        return prepared.ManifestPath;
    }

    internal Action<string, string>? ContentReportPresenter;

    private void ShowContentReport(AutoBookRow row, ContentFlagReport report, bool stopped)
    {
        string title = stopped ? "Đã dừng truyện vì nội dung" : "Cảnh báo nội dung";
        string message = (stopped
                ? $"“{row.Title}”: phát hiện cưỡng hiếp liên quan nhân vật dưới 18 tuổi. Đã dừng Codex; truyện không được khảo sát tiếp và không dịch."
                : $"“{row.Title}”: Codex phát hiện nội dung cần lưu ý. Khảo sát vẫn tiếp tục.") +
            Environment.NewLine + Environment.NewLine + report.Describe() + Environment.NewLine + Environment.NewLine +
            "Báo cáo được lưu trong Settings/content-flags.json của dự án.";
        if (ContentReportPresenter is not null) { ContentReportPresenter(title, message); return; }
        // Non-blocking: the survey keeps running (or the next book starts) while the dialog is open.
        Dispatcher.BeginInvoke(() => ThemedDialog.Show((Window?)_autoTranslateWindow ?? this, message, title,
            MessageBoxButton.OK, stopped ? MessageBoxImage.Error : MessageBoxImage.Warning));
    }

    /// <summary>Genres of the books in the project library, most used first, so new books reuse the site's existing tags.</summary>
    private async Task<IReadOnlyList<string>> KnownGenresAsync(CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        foreach (RecentProject recent in _projectLibrary.RecentProjects.Where(project => !project.Missing).Take(100))
        {
            try
            {
                // Shared for delete: the open project may be saved (replaced) while its genres are read.
                await using var stream = new FileStream(recent.ManifestPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (!document.RootElement.TryGetProperty("genres", out JsonElement genres) || genres.ValueKind != JsonValueKind.Array) continue;
                foreach (JsonElement genre in genres.EnumerateArray())
                    if (genre.GetString()?.Trim() is { Length: > 0 and <= 50 } name) counts[name] = counts.GetValueOrDefault(name) + 1;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return counts.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).Take(60).ToArray();
    }

    /// <summary>Projects translating in the background on a lane, by full manifest path (null while the lane gets ready).</summary>
    private readonly Dictionary<string, ProjectTranslationCoordinator?> _backgroundTranslations = new(StringComparer.OrdinalIgnoreCase);

    private bool IsTranslatingInBackground(string manifestPath)
    {
        lock (_backgroundTranslations) return _backgroundTranslations.ContainsKey(Path.GetFullPath(manifestPath));
    }

    /// <summary>Every worker a book can translate on: this machine's GPU, each Remote GPU and the Gemini key pool.</summary>
    internal IReadOnlyList<WorkerChoice> AutoTranslationWorkerChoices()
    {
        var choices = new List<WorkerChoice>
        {
            new(TranslationLanes.LocalId, "GPU máy này",
                _gpuBackends.GetProfile(GpuBackendProfile.DefaultLocalId).Model, true)
        };
        // Each key has its own daily quota: one worker per key, so books on different keys translate at the same time.
        IReadOnlyList<GeminiKeyWorker> keys;
        try { keys = GeminiKeyWorkers.All(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException) { keys = []; }
        foreach (GeminiKeyWorker key in keys)
            choices.Add(new(TranslationLanes.GeminiKey(key.Fingerprint), "Gemini · " + key.Name,
                $"{key.Masked} · 3.5 Flash-Lite, hết lượt thì 3.1 · 1 truyện mỗi lúc", true));
        choices.Add(new(TranslationLanes.GeminiId, "Gemini (tất cả key)",
            keys.Count > 0 ? $"{keys.Count} key cùng dịch một truyện" : "Chưa có Gemini API key.",
            keys.Count > 0, keys.Count > 0 ? null : "Chưa có Gemini API key."));
        foreach (GpuBackendProfile profile in RemoteProfiles())
        {
            bool connected = _gpuConnections.TryGetConnection(profile.Id, out GpuConnectionHandle? handle) && handle is { IsConnected: true };
            choices.Add(new(TranslationLanes.Remote(profile.Id), "Remote: " + profile.Name,
                $"{profile.Model} · {profile.ParallelRequests} luồng · " + (connected ? "đã kết nối" : "chưa kết nối, sẽ tự kết nối"), true));
        }
        CodexSurveyOptions codex = _codexTranslationOptions;
        bool codexReady = File.Exists(Path.Combine(codex.CodexHome, "auth.json"));
        choices.Add(new(TranslationLanes.CodexId, "Codex (ChatGPT)",
            $"{Path.GetFileName(codex.CodexHome)} · {codex.Model} · nguyên chương, {CodexTranslationSlots} chương mỗi lúc · trừ quota gói ChatGPT",
            codexReady, codexReady ? null : $"Codex chưa đăng nhập trong {codex.CodexHome}."));
        return choices;
    }

    /// <summary>Names of the choosable workers by id, for the labels of books' workers.</summary>
    internal IReadOnlyList<TranslationLaneOption> AutoTranslationLaneOptions() =>
        AutoTranslationWorkerChoices().Where(choice => choice.Available)
            .Select(choice => new TranslationLaneOption(choice.Id, choice.Name)).ToList();

    private GpuBackendProfile[] RemoteProfiles() =>
        _gpuBackends.Catalog.Profiles.Where(profile => profile.Kind == GpuBackendKind.Remote).ToArray();

    /// <summary>The GPU profiles (or the Gemini pool) a book's workers use now; several share the book's chapters.</summary>
    internal IReadOnlyList<Guid> ResolveLaneProfiles(string laneId)
    {
        string[] ids = laneId.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (ids.Length == 0 || ids is [TranslationLanes.FollowAppId]) return FollowAppProfiles();
        var profiles = new List<Guid>();
        foreach (string id in ids)
            profiles.AddRange(ResolveWorker(id));
        Guid[] distinct = profiles.Distinct().ToArray();
        return distinct.Length <= ProjectTranslationCoordinator.MaximumWorkers ? distinct
            : throw new InvalidOperationException($"Một truyện dùng tối đa {ProjectTranslationCoordinator.MaximumWorkers} worker.");
    }

    private IReadOnlyList<Guid> ResolveWorker(string id)
    {
        Guid local = GpuBackendProfile.DefaultLocalId;
        Guid[] remotes = RemoteProfiles().Select(profile => profile.Id).ToArray();
        switch (id)
        {
            case TranslationLanes.FollowAppId: return FollowAppProfiles();
            case TranslationLanes.LocalId: return [local];
            case TranslationLanes.GeminiId: return AllGeminiKeyProfiles();
            case TranslationLanes.CodexId: return [CodexTranslationProfileId];
            case TranslationLanes.AllRemoteId:
                return remotes.Length > 0 ? remotes : throw new InvalidOperationException("Chưa có GPU Remote nào trong AI & hiệu năng.");
            case TranslationLanes.AllGpuId: return [local, .. remotes];
        }
        if (TranslationLanes.GeminiKeyFingerprint(id) is { } fingerprint)
            return GeminiKeyWorkers.ByFingerprint(fingerprint) is { } key ? [key.ProfileId]
                : throw new InvalidOperationException("Gemini API key của worker này đã bị xóa; hãy bỏ worker đó và chọn worker khác.");
        if (TranslationLanes.RemoteProfile(id) is Guid chosen)
            return GpuProfileExists(chosen) ? [chosen]
                : throw new InvalidOperationException("GPU Remote của worker này đã bị xóa; hãy bỏ worker đó và chọn worker khác.");
        throw new InvalidOperationException($"Worker “{id}” không dịch được; hãy bỏ nó và chọn worker khác.");
    }

    private IReadOnlyList<Guid> FollowAppProfiles()
    {
        Guid local = GpuBackendProfile.DefaultLocalId;
        if (_gpuPreferences.UsesCodex) return [CodexTranslationProfileId];
        if (_gpuPreferences.Source == TranslationSource.Api) return AllGeminiKeyProfiles();
        Guid? remote = CurrentRemoteProfileId();
        return _gpuPreferences.Mode switch
        {
            GpuTranslationMode.LocalOnly => [local],
            GpuTranslationMode.RemoteOnly => [remote ?? throw RemoteMissing()],
            _ => [local, remote ?? throw RemoteMissing()]
        };

        static InvalidOperationException RemoteMissing() =>
            new("Mở GPU Manager, kết nối và chọn một profile Remote trước khi dùng Remote/Parallel.");
    }

    private static Guid[] AllGeminiKeyProfiles()
    {
        Guid[] keys = GeminiKeyWorkers.All().Select(key => key.ProfileId).ToArray();
        return keys.Length > 0 ? keys
            : throw new InvalidOperationException("Chưa có Gemini API key. Mở AI & hiệu năng hoặc Cài đặt → Gemini để thêm key.");
    }

    /// <summary>An API worker (the old Gemini pool, one saved Gemini key, or Codex): not a GPU to start or reconnect.</summary>
    private static bool IsGeminiProfile(Guid profileId) =>
        profileId == GeminiTranslationProfileId || profileId == CodexTranslationProfileId || GeminiKeyWorkers.IsKeyProfile(profileId);

    /// <summary>What a lane holds while a book translates on it. A lane that cannot be resolved holds only itself, so the
    /// book starts and reports why.</summary>
    internal IReadOnlySet<string> LaneResources(string laneId)
    {
        try { return ResolveLaneProfiles(laneId).Select(id => id.ToString("N")).ToHashSet(StringComparer.Ordinal); }
        catch (InvalidOperationException) { return new HashSet<string>(StringComparer.Ordinal) { "lane:" + laneId }; }
    }

    /// <summary>
    /// Translates a book in the background on its lane: its own project session and queue, so several books translate at
    /// once on different GPUs while the window stays free. A paused queue of the book resumes on the lane's GPUs.
    /// </summary>
    private async Task TranslateAutoBookOnLaneAsync(AutoBookRow row, string manifestPath, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(manifestPath);
        IReadOnlyList<Guid> profiles = ResolveLaneProfiles(row.LaneId);
        if (string.Equals(_projectSession?.ManifestPath is { } open ? Path.GetFullPath(open) : null, path, StringComparison.OrdinalIgnoreCase))
        {
            if (_projectTranslationCoordinator?.IsRunning == true)
                throw new InvalidOperationException("Truyện đang dịch trong cửa sổ chính; hãy tạm dừng hàng đợi ở đó rồi bấm Thử lại.");
            row.Stage = "Đóng dự án ở cửa sổ chính";
            if (!await CloseProjectCoreAsync())
                throw new InvalidOperationException("Truyện đang mở trong cửa sổ chính và chưa đóng được (đang có thao tác khác).");
        }
        lock (_backgroundTranslations)
        {
            if (!_backgroundTranslations.TryAdd(path, null))
                throw new InvalidOperationException("Truyện này đang được dịch ở một luồng khác.");
        }
        var owned = new List<IDisposable>();
        ProjectTranslationCoordinator? coordinator = null;
        try
        {
            // A rental GPU's SSH tunnel drops now and then; the connection monitor brings it back within seconds. The book
            // then resumes from its checkpoint on its own, a few times in a row unless a resume makes progress again.
            int failuresInARow = 0, lastCompleted = -1;
            while (true)
            {
                row.Stage = failuresInARow == 0 ? "Chuẩn bị luồng dịch" : $"Kết nối lại GPU (lần {failuresInARow}/{LaneAutoResumes})";
                ProjectTranslationProgress progress;
                int workerCount;
                try
                {
                    ProjectSession session = (await _projectRepository.OpenAsync(path, cancellationToken)).Session;
                    var workers = new List<ProjectTranslationWorkerDefinition>();
                    foreach (Guid profileId in profiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        workers.Add(await LaneWorkerAsync(profileId, owned));
                    }
                    workerCount = workers.Count;
                    coordinator = new ProjectTranslationCoordinator(session, _projectRepository, workers,
                        token => LoadLaneTranslationEnvironmentAsync(session, workers[0], token));
                    lock (_backgroundTranslations) _backgroundTranslations[path] = coordinator;
                    await RunLaneCoordinatorAsync(row, session, coordinator, workers.Count, cancellationToken);
                    progress = coordinator.GetProgress();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && failuresInARow < LaneAutoResumes &&
                                           profiles.Any(id => !IsGeminiProfile(id) && id != GpuBackendProfile.DefaultLocalId))
                {
                    // Reconnecting the Remote GPU failed this time: wait for the monitor and try again.
                    failuresInARow++;
                    row.Detail = "Chưa kết nối lại được GPU: " + ex.Message;
                    await Task.Delay(LaneResumeDelay, cancellationToken);
                    continue;
                }
                finally
                {
                    if (coordinator?.IsRunning == true) await coordinator.StopAsync();
                    foreach (IDisposable client in owned) client.Dispose();
                    owned.Clear();
                }
                ShowLaneProgress(row, coordinator, workerCount);
                switch (progress.Status)
                {
                    case ProjectTranslationJobStatus.Completed: row.Stage = "Xong"; return;
                    case ProjectTranslationJobStatus.CompletedWithErrors: row.Stage = "Xong (có lỗi)"; return;
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (progress.CompletedChapters > lastCompleted) failuresInARow = 0;
                lastCompleted = progress.CompletedChapters;
                string stopped = $"{progress.CompletedChapters}/{progress.TotalChapters} chương. " +
                    "Hàng đợi đã dừng (" + progress.Status + ")" +
                    (progress.Error is { } error ? ": " + error : ". Bấm Thử lại để dịch tiếp.");
                if (progress.Status != ProjectTranslationJobStatus.Interrupted || ++failuresInARow > LaneAutoResumes)
                    throw new InvalidOperationException(stopped);
                row.Detail = stopped + $" Tự dịch tiếp sau {LaneResumeDelay.TotalSeconds:N0} giây.";
                await Task.Delay(LaneResumeDelay, cancellationToken);
            }
        }
        finally
        {
            if (coordinator?.IsRunning == true) await coordinator.StopAsync();
            foreach (IDisposable client in owned) client.Dispose();
            lock (_backgroundTranslations) _backgroundTranslations.Remove(path);
        }
    }

    /// <summary>Times in a row a book on a lane resumes on its own after its GPU dropped, before it waits for Thử lại.</summary>
    private const int LaneAutoResumes = 5;
    internal TimeSpan LaneResumeDelay = TimeSpan.FromSeconds(20);

    /// <summary>Starts or resumes the book's queue on the lane's workers and waits until it ends, showing progress.</summary>
    private async Task RunLaneCoordinatorAsync(AutoBookRow row, ProjectSession session, ProjectTranslationCoordinator coordinator,
        int workers, CancellationToken cancellationToken)
    {
        // Reports are posted: one landing after the run ended must not cover the final state or the error.
        bool live = true;
        void Report(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
        {
            if (live) ShowLaneProgress(row, coordinator, workers);
        });
        coordinator.ProgressChanged += Report;
        try
        {
            Guid[] chapterIds = session.Manifest.Chapters.Where(entry => entry.DeletedAt is null)
                .OrderBy(entry => entry.Number).Select(entry => entry.ChapterId).ToArray();
            ProjectTranslationJob? job = await coordinator.LoadAsync(cancellationToken);
            // Stop is a safe pause: the book resumes later from its checkpoint.
            using CancellationTokenRegistration pause = cancellationToken.Register(() => _ = coordinator.PauseAsync());
            cancellationToken.ThrowIfCancellationRequested();
            row.Stage = "Đang dịch";
            if (job?.Status is ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted)
            {
                await coordinator.AppendAsync(chapterIds);
                await coordinator.ResumeWithCurrentConfigurationAsync();
            }
            else
            {
                if (job?.Status is ProjectTranslationJobStatus.Ready or ProjectTranslationJobStatus.Draft) await coordinator.ClearAsync();
                await coordinator.StartAsync(await coordinator.PlanAsync(chapterIds, cancellationToken));
            }
            // Segments that failed along the way (often while a GPU was down) are sent once more before the book ends.
            if (coordinator.GetProgress().Status == ProjectTranslationJobStatus.CompletedWithErrors &&
                !cancellationToken.IsCancellationRequested)
            {
                row.Stage = "Dịch lại các đoạn lỗi";
                await coordinator.RetryErrorSegmentsAsync();
            }
        }
        finally
        {
            live = false;
            coordinator.ProgressChanged -= Report;
        }
    }

    private void ShowLaneProgress(AutoBookRow row, ProjectTranslationCoordinator coordinator, int workers)
    {
        ProjectTranslationProgress progress = coordinator.GetProgress();
        row.Detail = $"{progress.CompletedChapters}/{progress.TotalChapters} chương" +
            (progress.FailedChapters > 0 ? $", {progress.FailedChapters} lỗi" : "") +
            (workers > 1 ? $" · {workers} GPU" : "") +
            (progress.CurrentChapterTitle is { } current ? $" · {current}" : "");
    }

    /// <summary>A worker of a background lane: starts this machine's llama-server or reconnects the Remote GPU first.</summary>
    private async Task<ProjectTranslationWorkerDefinition> LaneWorkerAsync(Guid profileId, List<IDisposable> owned)
    {
        if (profileId == GeminiTranslationProfileId)
        {
            if (GeminiApiKeyVault.ReadAll().Count == 0)
                throw new InvalidOperationException("Chưa có Gemini API key. Mở AI & hiệu năng hoặc Cài đặt → Gemini để thêm key.");
            ModelOptions options = GeminiTranslationModelOptions();
            var backend = new GpuBackendSnapshot(GeminiTranslationProfileId, GpuBackendKind.Remote,
                "Gemini API", "https://generativelanguage.googleapis.com", options.Model);
            return CreateWorkerDefinition(GeminiTranslationProfileId, backend, options,
                GeminiTranslationSegmentationOptions(), null, owned);
        }
        if (GeminiKeyWorkers.ByProfile(profileId) is { } key) return GeminiKeyWorkerDefinition(key, owned);
        if (profileId == CodexTranslationProfileId) return CodexWorkerDefinition(CodexTranslationProfileId, owned);
        GpuBackendProfile profile = _gpuBackends.GetProfile(profileId);
        if (profile.Kind == GpuBackendKind.Local)
        {
            if (_translationClient is GpuBackendManager) await _localLlama.StartAsync(profile);
        }
        else await EnsureRemoteConnectedAsync(profile);
        return ProjectTranslationProfileWorker(profile, owned);
    }

    /// <summary>Closing the app: background books stop at a checkpoint and resume when their link is queued again.</summary>
    private async Task StopBackgroundTranslationsAsync()
    {
        ProjectTranslationCoordinator[] running;
        lock (_backgroundTranslations)
            running = _backgroundTranslations.Values.OfType<ProjectTranslationCoordinator>().Where(value => value.IsRunning).ToArray();
        foreach (ProjectTranslationCoordinator coordinator in running)
        {
            try { await coordinator.StopAsync(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        }
    }
}
