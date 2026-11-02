using System.IO;
using System.Text.Json;
using System.Windows;
using QuickTranslator.AiEditor.Core.AutoTranslate;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private AutoTranslateWindow? _autoTranslateWindow;
    internal Func<CodexSurveyOptions, CodexSurveyRunner> CodexSurveyRunnerFactory = options => new CodexSurveyRunner(options);
    internal TimeSpan AutoTranslatePollInterval = TimeSpan.FromSeconds(3);

    private void OnAutoTranslateFromLinks(object sender, RoutedEventArgs e)
    {
        if (_autoTranslateWindow is { IsLoaded: true })
        {
            _autoTranslateWindow.Activate();
            return;
        }
        _autoTranslateWindow = new AutoTranslateWindow(RunAutoTranslateAsync) { Owner = this };
        _autoTranslateWindow.Closed += (_, _) => _autoTranslateWindow = null;
        _autoTranslateWindow.Show();
    }

    /// <summary>
    /// Three stages run side by side, each one book at a time: downloading, the Codex survey and translation. The next
    /// book downloads while Codex surveys this one, and a surveyed book translates while Codex surveys the next. Translation
    /// stays one book at a time because the queue belongs to the project open in this window.
    /// </summary>
    internal async Task RunAutoTranslateAsync(IReadOnlyList<AutoBookRow> rows, AutoTranslateSettings settings,
        CancellationToken cancellationToken)
    {
        AutoBookPreparer preparer = CreateAutoBookPreparer();
        var downloaded = rows.Select(_ => new TaskCompletionSource<AutoBookDownload?>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var prepared = rows.Select(_ => new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        async Task DownloadAllAsync()
        {
            for (int index = 0; index < rows.Count; index++)
            {
                if (cancellationToken.IsCancellationRequested) { downloaded[index].SetResult(null); rows[index].Stage = "Đã dừng"; continue; }
                AutoBookRow row = rows[index];
                var status = new Progress<AutoBookStatus>(value =>
                {
                    row.Stage = value.Stage;
                    row.Detail = value.Detail;
                    if (value.Title is not null) row.Title = value.Title;
                });
                try
                {
                    AutoBookDownload download = await preparer.DownloadAsync(row.Url, settings.DownloadDelay, status, cancellationToken);
                    // Progress reports are posted: set the waiting state after the last one has landed.
                    await Dispatcher.InvokeAsync(() =>
                    {
                        row.Title = download.Title;
                        row.Stage = "Đã tải, chờ Codex";
                        row.Detail = "";
                    }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                    downloaded[index].SetResult(download);
                }
                catch (AutoBookPausedException paused)
                {
                    row.Title = paused.Title;
                    row.Stage = $"⏸ Tạm dừng ({paused.Downloaded}/{paused.Total})";
                    row.Detail = paused.Message;
                    downloaded[index].SetResult(null);
                }
                catch (Exception ex)
                {
                    row.Stage = ex is OperationCanceledException ? "Đã dừng" : "Lỗi";
                    if (ex is not OperationCanceledException) row.Detail = ex.Message;
                    downloaded[index].SetResult(null);
                }
            }
        }
        async Task PrepareAllAsync()
        {
            for (int index = 0; index < rows.Count; index++)
            {
                if (await downloaded[index].Task is not { } download) { prepared[index].SetResult(null); continue; }
                if (cancellationToken.IsCancellationRequested) { prepared[index].SetResult(null); rows[index].Stage = "Đã dừng"; continue; }
                try { prepared[index].SetResult(await PrepareAutoBookAsync(preparer, download, rows[index], settings, cancellationToken)); }
                catch (ContentStopException stop)
                {
                    rows[index].Stage = "⛔ Đã dừng";
                    rows[index].Detail = stop.Message;
                    prepared[index].SetResult(null);
                    ShowContentReport(rows[index], stop.Report, stopped: true);
                }
                catch (Exception ex)
                {
                    rows[index].Stage = ex is OperationCanceledException ? "Đã dừng" : "Lỗi";
                    if (ex is not OperationCanceledException) rows[index].Detail = ex.Message;
                    prepared[index].SetResult(null);
                }
            }
        }
        Task downloading = DownloadAllAsync();
        Task preparing = PrepareAllAsync();
        for (int index = 0; index < rows.Count; index++)
        {
            if (await prepared[index].Task is not string manifest) continue;
            if (settings.PrepareOnly) continue;
            if (cancellationToken.IsCancellationRequested) { rows[index].Stage = "Đã chuẩn bị, chưa dịch"; continue; }
            try { await TranslateAutoBookAsync(rows[index], manifest, cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                rows[index].Stage = "Lỗi dịch";
                rows[index].Detail = ex.Message;
            }
            catch (OperationCanceledException) { rows[index].Stage = "Đã tạm dừng"; }
        }
        await Task.WhenAll(downloading, preparing);
    }

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
        CodexSurveyOptions codex = settings.Codex with { KnownGenres = await KnownGenresAsync(cancellationToken) };
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

    private async Task TranslateAutoBookAsync(AutoBookRow row, string manifestPath, CancellationToken cancellationToken)
    {
        if (_projectTranslationCoordinator?.IsRunning == true)
            throw new InvalidOperationException("Hàng đợi dịch của dự án đang mở vẫn chạy; hãy chờ nó xong rồi chạy lại.");
        row.Stage = "Mở dự án";
        if (!await RunWorkspaceTransitionAsync(() => OpenProjectCoreAsync(manifestPath)))
            throw new InvalidOperationException("Không chuyển được sang dự án (đang có thao tác khác hoặc nội dung chưa lưu).");
        RefreshProjectSwitcher();
        Guid[] chapterIds = _projectSession!.Manifest.Chapters.Where(entry => entry.DeletedAt is null)
            .OrderBy(entry => entry.Number).Select(entry => entry.ChapterId).ToArray();
        row.Stage = "Đang dịch";
        await OpenProjectTranslationQueueAsync(chapterIds);
        ProjectTranslationCoordinator coordinator = _projectTranslationCoordinator
            ?? throw new InvalidOperationException("Không tạo được hàng đợi dịch.");
        DateTimeOffset waitingSince = DateTimeOffset.UtcNow;
        while (true)
        {
            try { await Task.Delay(AutoTranslatePollInterval, cancellationToken); }
            catch (OperationCanceledException)
            {
                if (coordinator.IsRunning) await coordinator.PauseAsync();
                throw;
            }
            if (!string.Equals(_projectSession?.ManifestPath, manifestPath, StringComparison.OrdinalIgnoreCase) ||
                !ReferenceEquals(coordinator, _projectTranslationCoordinator))
                throw new InvalidOperationException("Dự án hoặc hàng đợi đã được đổi trong lúc dịch tự động.");
            ProjectTranslationProgress progress = coordinator.GetProgress();
            row.Detail = $"{progress.CompletedChapters}/{progress.TotalChapters} chương" +
                (progress.FailedChapters > 0 ? $", {progress.FailedChapters} lỗi" : "") +
                (progress.CurrentChapterTitle is { } current ? $" · {current}" : "");
            switch (progress.Status)
            {
                case ProjectTranslationJobStatus.Completed:
                    row.Stage = "Xong";
                    return;
                case ProjectTranslationJobStatus.CompletedWithErrors:
                    row.Stage = "Xong (có lỗi)";
                    return;
                case ProjectTranslationJobStatus.Cancelled or ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted
                    when !coordinator.IsRunning:
                    throw new InvalidOperationException("Hàng đợi đã dừng (" + progress.Status + ")" +
                        (progress.Error is { } error ? ": " + error : ". Mở tab Hàng đợi để tiếp tục."));
                case ProjectTranslationJobStatus.Draft or ProjectTranslationJobStatus.Ready
                    when !coordinator.IsRunning && DateTimeOffset.UtcNow - waitingSince > TimeSpan.FromMinutes(2):
                    throw new InvalidOperationException("Hàng đợi chưa bắt đầu. Kiểm tra backend dịch trong AI & hiệu năng.");
            }
        }
    }
}
