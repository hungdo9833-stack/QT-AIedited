using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed class ProjectTranslationCoordinator
{
    private static readonly ConcurrentDictionary<Guid, Guid> ActiveProjects = new();
    private readonly ProjectSession _session;
    private readonly ProjectRepository _repository;
    private readonly ILlmClient _client;
    private readonly IReadOnlyList<ProjectTranslationWorkerDefinition> _workerDefinitions;
    private readonly ProjectTranslationJobStore _store;
    private readonly Func<CancellationToken, Task<ProjectTranslationEnvironment>> _environmentLoader;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Guid _coordinatorId = Guid.NewGuid();
    private ProjectTranslationJob? _job;
    private TranslationQueue? _chapterQueue;
    private readonly Dictionary<Guid, WorkerQueue> _workerQueues = [];
    private readonly Dictionary<Guid, WorkerActivity> _workerActivity = [];
    private Task? _runTask;
    private bool _pauseRequested;
    private bool _cancelRequested;
    private bool _stopRequested;
    private bool _interruptionRequested;
    private int _progressChangesSinceSave;

    public ProjectTranslationCoordinator(ProjectSession session, ProjectRepository repository, ILlmClient client,
        Func<CancellationToken, Task<ProjectTranslationEnvironment>> environmentLoader,
        ProjectTranslationJobStore? store = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _workerDefinitions = [];
        _environmentLoader = environmentLoader ?? throw new ArgumentNullException(nameof(environmentLoader));
        _store = store ?? new ProjectTranslationJobStore(session);
    }

    public ProjectTranslationCoordinator(ProjectSession session, ProjectRepository repository,
        IReadOnlyList<ProjectTranslationWorkerDefinition> workers,
        Func<CancellationToken, Task<ProjectTranslationEnvironment>> environmentLoader,
        ProjectTranslationJobStore? store = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentNullException.ThrowIfNull(workers);
        if (workers.Count is < 1 or > 2 || workers.Select(worker => worker.WorkerId).Distinct().Count() != workers.Count)
            throw new ArgumentException("M6.6 hỗ trợ một hoặc hai worker duy nhất.", nameof(workers));
        foreach (ProjectTranslationWorkerDefinition worker in workers) worker.Validate();
        _workerDefinitions = workers.ToArray();
        _client = workers[0].Client;
        _environmentLoader = environmentLoader ?? throw new ArgumentNullException(nameof(environmentLoader));
        _store = store ?? new ProjectTranslationJobStore(session);
    }

    public event EventHandler? ProgressChanged;
    public bool IsRunning { get { lock (_sync) return _runTask is { IsCompleted: false }; } }
    public ProjectTranslationJob? CurrentJob { get { lock (_sync) return _job is null ? null : ProjectTranslationJobStore.Clone(_job); } }

    public bool IsChapterLockedByRunningQueue(Guid chapterId)
    {
        lock (_sync)
        {
            if (_runTask is not { IsCompleted: false } || _job is null) return false;
            ProjectTranslationItem? item = _job.Items.SingleOrDefault(x => x.ChapterId == chapterId);
            return item is not null && item.Status is ProjectTranslationItemStatus.Pending
                or ProjectTranslationItemStatus.Running
                or ProjectTranslationItemStatus.Paused
                or ProjectTranslationItemStatus.Interrupted;
        }
    }

    public async Task<ProjectTranslationJob?> LoadAsync(CancellationToken cancellationToken = default)
    {
        ProjectTranslationJob? loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync) _job = loaded;
        Notify();
        return loaded is null ? null : ProjectTranslationJobStore.Clone(loaded);
    }

    /// <param name="retranslateAll">Chapters to translate again from scratch (QueueMode.AllSegments); segments the user
    /// edited or approved stay protected. Other chapters only get their missing segments.</param>
    public async Task<ProjectTranslationJob> PlanAsync(IEnumerable<Guid> chapterIds,
        CancellationToken cancellationToken = default, IReadOnlySet<Guid>? retranslateAll = null)
    {
        Guid[] selected = chapterIds.Distinct().ToArray();
        if (selected.Length == 0) throw new ArgumentException("Hãy chọn ít nhất một chapter.", nameof(chapterIds));
        ProjectTranslationEnvironment environment = await LoadEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        var entries = selected.Select(id => _session.Manifest.Chapters.SingleOrDefault(entry =>
                entry.ChapterId == id && entry.DeletedAt is null)
                ?? throw new InvalidOperationException("Lựa chọn có chapter không còn active trong Project."))
            .OrderBy(entry => entry.Number).ToArray();
        var items = new List<ProjectTranslationItem>(entries.Length);
        for (int index = 0; index < entries.Length; index++)
            items.Add(await CreateItemAsync(entries[index], index + 1, retranslateAll?.Contains(entries[index].ChapterId) == true,
                cancellationToken).ConfigureAwait(false));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var job = new ProjectTranslationJob
        {
            ProjectId = _session.Manifest.ProjectId,
            Status = ProjectTranslationJobStatus.Draft,
            ModelOptions = _workerDefinitions.Count > 0 ? _workerDefinitions[0].ModelOptions with { } : environment.ModelOptions with { },
            Backend = _workerDefinitions.Count > 0 ? _workerDefinitions[0].Backend with { } :
                (environment.Backend ?? GpuBackendSnapshot.LocalDefault(environment.ModelOptions.Model)) with { },
            PromptPreset = environment.PromptPreset with { },
            ProjectResourceRevision = environment.Resources.Revision,
            CreatedAt = now,
            UpdatedAt = now,
            Workers = _workerDefinitions.Select(worker => new ProjectTranslationWorkerState
            {
                WorkerId = worker.WorkerId, ProfileId = worker.Backend.ProfileId, Name = worker.Backend.ProfileName,
                Backend = worker.Backend with { }, ModelOptions = worker.ModelOptions with { },
                SegmentationOptions = worker.SegmentationOptions with { },
                HourlyPrice = worker.HourlyPrice, BackendIdentity = worker.BackendIdentity,
                Slots = worker.Slots,
                Status = ProjectTranslationWorkerStatus.Ready
            }).ToList(),
            Items = items
        };
        job.Validate();
        return job;
    }

    private async Task<ProjectTranslationItem> CreateItemAsync(ProjectChapterEntry entry, int order, bool retranslate,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ChapterDocument chapter = (await _repository.LoadChapterAsync(_session, entry.ChapterId, cancellationToken)
            .ConfigureAwait(false)).Chapter;
        var item = new ProjectTranslationItem
        {
            ChapterId = chapter.Id,
            Number = entry.Number,
            Order = order,
            Title = entry.VietnameseTitle,
            SourceFingerprint = ProjectTranslationFingerprints.Source(chapter),
            ProtectedFingerprint = ProjectTranslationFingerprints.Protected(chapter),
            TotalSegments = chapter.Segments.Count,
            CompletedSegments = CompletedSegments(chapter),
            ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error),
            SourceHanCharacters = chapter.Segments.Sum(segment => CountHan(segment.SourceText)),
            CompletedHanCharacters = CompletedHanCharacters(chapter),
            RetranslateAll = retranslate
        };
        if (retranslate) item.CompletedSegments = CompletedSegmentsFor(chapter, item);
        item.RecordEligibilityDiagnostic(TranslationEligibilityDiagnostics.Analyze(chapter,
            retranslate ? QueueMode.AllSegments : QueueMode.MissingSegments, TranslationEligibilityDiagnosticStage.Plan));
        return item;
    }

    /// <summary>
    /// Adds chapters to the end of a job that is paused, interrupted or ready (never while it runs). A chapter already
    /// in the job keeps its place: if it finished or was skipped and is chosen for retranslation it goes back to
    /// Pending; if it has not started yet it only takes the retranslation choice. Resume then continues as usual.
    /// </summary>
    public async Task<ProjectTranslationAppendResult> AppendAsync(IEnumerable<Guid> chapterIds,
        IReadOnlySet<Guid>? retranslateAll = null, CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("Hãy tạm dừng hàng đợi trước khi thêm chương.");
        ProjectTranslationJob current = CurrentJob ?? await LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Project chưa có hàng đợi để thêm chương.");
        if (current.Status is not (ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted or
            ProjectTranslationJobStatus.Ready))
            throw new InvalidOperationException("Chỉ thêm chương vào hàng đợi đang tạm dừng, bị gián đoạn hoặc sẵn sàng.");
        HashSet<Guid> selected = chapterIds.ToHashSet();
        HashSet<Guid> inJob = current.Items.Select(item => item.ChapterId).ToHashSet();
        ProjectChapterEntry[] entries = _session.Manifest.Chapters
            .Where(entry => entry.DeletedAt is null && selected.Contains(entry.ChapterId) && !inJob.Contains(entry.ChapterId))
            .OrderBy(entry => entry.Number).ToArray();
        int order = current.Items.Count == 0 ? 0 : current.Items.Max(item => item.Order);
        var added = new List<ProjectTranslationItem>(entries.Length);
        foreach (ProjectChapterEntry entry in entries)
            added.Add(await CreateItemAsync(entry, ++order, retranslateAll?.Contains(entry.ChapterId) == true,
                cancellationToken).ConfigureAwait(false));
        int reopened = 0;
        await PersistAsync(job =>
        {
            foreach (ProjectTranslationItem item in job.Items.Where(item => retranslateAll?.Contains(item.ChapterId) == true))
            {
                // A started chapter that is waiting again (paused, interrupted, or pending after a run) restarts from scratch too:
                // otherwise its old run resumes with only the missing segments and keeps any failed one.
                if (item.Status is ProjectTranslationItemStatus.Completed or ProjectTranslationItemStatus.Skipped or
                    ProjectTranslationItemStatus.Failed or ProjectTranslationItemStatus.NeedsReview or ProjectTranslationItemStatus.Cancelled ||
                    item.ChapterQueueRunId is not null && item.Status is ProjectTranslationItemStatus.Pending or
                        ProjectTranslationItemStatus.Paused or ProjectTranslationItemStatus.Interrupted)
                {
                    item.Status = ProjectTranslationItemStatus.Pending;
                    item.Disposition = ProjectTranslationItemDisposition.None;
                    item.Error = null; item.FinishedAt = null; item.ChapterQueueRunId = null; item.AssignedWorkerId = null;
                    PrepareItemMetricsForRetry(item);
                    item.RetranslateAll = true;
                    // Count progress from the new run; preflight recounts kept user segments once the chapter starts.
                    item.StartedAt = null; item.CompletedSegments = 0;
                    reopened++;
                }
                else if (item.ChapterQueueRunId is null) item.RetranslateAll = true; // Not started yet: retranslate when it starts.
            }
            job.Items.AddRange(added);
        }, cancellationToken).ConfigureAwait(false);
        return new(added.Count, reopened);
    }

    public Task StartAsync(ProjectTranslationJob plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Status != ProjectTranslationJobStatus.Draft)
            throw new InvalidOperationException("Queue Plan phải ở trạng thái Draft trước khi bắt đầu.");
        return LaunchAsync(plan, isNew: true, retryFailed: false, cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
        => await ResumeCoreAsync(false, cancellationToken).ConfigureAwait(false);

    public async Task ResumeWithCurrentConfigurationAsync(CancellationToken cancellationToken = default)
        => await ResumeCoreAsync(true, cancellationToken).ConfigureAwait(false);

    private async Task ResumeCoreAsync(bool acceptChangedBackendOrWorkers,
        CancellationToken cancellationToken)
    {
        ProjectTranslationJob job = CurrentJob ?? await LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Project chưa có translation job để tiếp tục.");
        if (job.Status is not (ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted))
            throw new InvalidOperationException("Translation job chưa ở trạng thái có thể Resume.");
        await LaunchAsync(job, isNew: false, retryFailed: false, cancellationToken,
            acceptChangedBackendOrWorkers).ConfigureAwait(false);
    }

    public async Task RetryFailedAsync(CancellationToken cancellationToken = default)
    {
        ProjectTranslationJob job = CurrentJob ?? await LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Project chưa có translation job.");
        if (IsRunning) throw new InvalidOperationException("Hãy Pause translation job trước khi Retry.");
        if (!job.Items.Any(item => item.Status == ProjectTranslationItemStatus.Failed))
            throw new InvalidOperationException("Không có chapter thất bại để Retry.");
        await PersistAsync(value =>
        {
            foreach (ProjectTranslationItem item in value.Items.Where(item => item.Status == ProjectTranslationItemStatus.Failed))
            {
                item.Status = ProjectTranslationItemStatus.Pending;
                item.Disposition = ProjectTranslationItemDisposition.None;
                item.Error = null;
                item.FinishedAt = null;
                PrepareItemMetricsForRetry(item);
            }
            value.Status = ProjectTranslationJobStatus.Ready;
            value.LastError = null;
        }, cancellationToken).ConfigureAwait(false);
        await LaunchAsync(CurrentJob!, isNew: false, retryFailed: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectTranslationJob> ReplanUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("Hãy Pause translation job trước khi tạo lại hàng đợi.");
        ProjectTranslationJob current = CurrentJob ?? await LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Project chưa có translation job để tạo lại.");
        Guid[] unfinished = current.Items
            .Where(item => item.Status is not (ProjectTranslationItemStatus.Completed or ProjectTranslationItemStatus.Skipped))
            .OrderBy(item => item.Order)
            .Select(item => item.ChapterId)
            .ToArray();
        if (unfinished.Length == 0)
            throw new InvalidOperationException("Không còn chapter chưa hoàn tất để tạo lại hàng đợi.");

        // Build the replacement first. If current settings or chapter files cannot be read, the old job remains intact.
        // A chapter chosen for a full retranslation keeps that choice until its retranslation queue has started.
        HashSet<Guid> retranslate = current.Items.Where(item => item.RetranslateAll && item.ChapterQueueRunId is null)
            .Select(item => item.ChapterId).ToHashSet();
        ProjectTranslationJob replacement = await PlanAsync(unfinished, cancellationToken, retranslate).ConfigureAwait(false);
        CarryHistoricalWorkerMetrics(current, replacement);
        if (current.Status is not (ProjectTranslationJobStatus.Completed or
            ProjectTranslationJobStatus.CompletedWithErrors or ProjectTranslationJobStatus.Cancelled))
        {
            await PersistAsync(job =>
            {
                job.Status = ProjectTranslationJobStatus.Cancelled;
                job.CurrentChapterId = null;
                job.LastError = "Job cũ đã được đóng để tạo lại phần chưa hoàn tất bằng cấu hình hiện tại.";
                foreach (ProjectTranslationWorkerState worker in job.Workers)
                {
                    worker.CurrentChapterId = null;
                    worker.RunningChapterIds.Clear();
                    if (worker.Status is not ProjectTranslationWorkerStatus.Completed)
                        worker.Status = ProjectTranslationWorkerStatus.Cancelled;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        lock (_sync) _job = null;
        Notify();
        return replacement;
    }

    public async Task RetryWorkerAsync(Guid workerId, bool confirmUnknownRemoteResult,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("Hãy Pause job trước khi Retry worker.");
        ProjectTranslationJob job = CurrentJob ?? throw new InvalidOperationException("Project chưa có translation job.");
        ProjectTranslationWorkerState worker = job.Workers.SingleOrDefault(value => value.WorkerId == workerId)
            ?? throw new KeyNotFoundException("Không tìm thấy worker trong Queue Plan.");
        if (!confirmUnknownRemoteResult && job.Items.Any(item => item.AssignedWorkerId == workerId &&
            item.Status == ProjectTranslationItemStatus.NeedsReview))
            throw new InvalidOperationException("Remote có kết quả chưa xác định. Hãy kiểm tra chapter và xác nhận Retry rõ ràng.");
        await PersistAsync(value =>
        {
            foreach (ProjectTranslationItem item in value.Items.Where(item => item.AssignedWorkerId == workerId &&
                item.Status is ProjectTranslationItemStatus.Failed or ProjectTranslationItemStatus.NeedsReview))
            {
                item.Status = ProjectTranslationItemStatus.Pending;
                item.Disposition = ProjectTranslationItemDisposition.None;
                item.Error = null; item.FinishedAt = null; item.ChapterQueueRunId = null;
                PrepareItemMetricsForRetry(item);
            }
            ProjectTranslationWorkerState state = value.Workers.Single(item => item.WorkerId == workerId);
            state.Status = ProjectTranslationWorkerStatus.Ready; state.LastError = null;
            state.CurrentChapterId = null; state.RunningChapterIds.Clear();
            value.Status = ProjectTranslationJobStatus.Interrupted;
            value.LastError = "Worker đã được chuẩn bị Retry; bấm Resume để tiếp tục.";
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReassignPendingAsync(Guid fromWorkerId, Guid toWorkerId,
        CancellationToken cancellationToken = default)
    {
        if (IsRunning) throw new InvalidOperationException("Hãy Pause job trước khi chuyển Pending sang GPU khác.");
        if (fromWorkerId == toWorkerId) throw new ArgumentException("Worker nguồn và đích phải khác nhau.");
        ProjectTranslationJob job = CurrentJob ?? throw new InvalidOperationException("Project chưa có translation job.");
        if (job.Workers.All(worker => worker.WorkerId != fromWorkerId) || job.Workers.All(worker => worker.WorkerId != toWorkerId))
            throw new KeyNotFoundException("Worker không còn trong Queue Plan.");
        await PersistAsync(value =>
        {
            foreach (ProjectTranslationItem item in value.Items.Where(item => item.AssignedWorkerId == fromWorkerId &&
                item.Status == ProjectTranslationItemStatus.Pending && item.ChapterQueueRunId is null))
                item.AssignedWorkerId = toWorkerId;
            value.LastError = "Chỉ các chapter Pending chưa gửi request được chuyển worker.";
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAsync()
    {
        Task? running;
        TranslationQueue[] chapterQueues;
        lock (_sync)
        {
            running = _runTask;
            if (running is null || running.IsCompleted) return;
            _pauseRequested = true;
            chapterQueues = _workerQueues.Count > 0 ? _workerQueues.Values.Select(value => value.Queue).ToArray() :
                _chapterQueue is null ? [] : [_chapterQueue];
        }
        await PersistAsync(job => job.Status = ProjectTranslationJobStatus.PauseRequested, CancellationToken.None)
            .ConfigureAwait(false);
        await Task.WhenAll(chapterQueues.Select(queue => queue.PauseAsync())).ConfigureAwait(false);
        await running.ConfigureAwait(false);
    }

    /// <summary>
    /// Empties the queue: the saved job is deleted so the next selection starts a new one. Segments already translated
    /// stay in their chapters. Only allowed while the queue is not running.
    /// </summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false })
                throw new InvalidOperationException("Hàng đợi đang dịch; hãy Tạm dừng trước khi xóa hàng đợi.");
        }
        await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync) _job = null;
        Notify();
    }

    public async Task CancelPendingAsync()
    {
        Task? running;
        TranslationQueue[] chapterQueues;
        lock (_sync)
        {
            running = _runTask;
            if (running is null || running.IsCompleted) return;
            _cancelRequested = true;
            chapterQueues = _workerQueues.Count > 0 ? _workerQueues.Values.Select(value => value.Queue).ToArray() :
                _chapterQueue is null ? [] : [_chapterQueue];
        }
        await PersistAsync(job =>
        {
            job.Status = ProjectTranslationJobStatus.Cancelling;
            foreach (ProjectTranslationItem item in job.Items.Where(item => item.Status == ProjectTranslationItemStatus.Pending))
            {
                item.Status = ProjectTranslationItemStatus.Cancelled;
                item.Disposition = ProjectTranslationItemDisposition.Cancelled;
                item.Error = "Đã hủy trước khi gửi request.";
                item.FinishedAt = DateTimeOffset.UtcNow;
            }
        }, CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(chapterQueues.Select(CancelQueueAsync)).ConfigureAwait(false);
        await running.ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        Task? running;
        TranslationQueue[] chapterQueues;
        lock (_sync)
        {
            running = _runTask;
            if (running is null || running.IsCompleted) return;
            _stopRequested = true;
            chapterQueues = _workerQueues.Count > 0 ? _workerQueues.Values.Select(value => value.Queue).ToArray() :
                _chapterQueue is null ? [] : [_chapterQueue];
        }
        await PersistAsync(job =>
        {
            job.Status = ProjectTranslationJobStatus.Cancelling;
            job.LastError = "Đang dừng request hiện tại; các checkpoint đã hoàn tất được giữ nguyên.";
        }, CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(chapterQueues.Select(CancelQueueAsync)).ConfigureAwait(false);
        await running.ConfigureAwait(false);
    }

    public ProjectTranslationProgress GetProgress()
    {
        ProjectTranslationJob? job = CurrentJob;
        if (job is null) return new(ProjectTranslationJobStatus.Draft, 0, 0, 0, 0, 0, null, null, 0, 0, null);
        return CalculateProgress(job);
    }

    private static ProjectTranslationProgress CalculateProgress(ProjectTranslationJob job)
    {
        ProjectTranslationItem? current = job.CurrentChapterId is Guid id
            ? job.Items.SingleOrDefault(item => item.ChapterId == id) : null;
        return new(job.Status, job.Items.Count,
            job.Items.Count(item => item.Status == ProjectTranslationItemStatus.Completed),
            job.Items.Count(item => item.Status == ProjectTranslationItemStatus.Failed),
            job.Items.Count(item => item.Status is ProjectTranslationItemStatus.Skipped or ProjectTranslationItemStatus.NeedsReview or ProjectTranslationItemStatus.Cancelled),
            job.Items.Count(item => item.Status == ProjectTranslationItemStatus.Pending),
            current?.ChapterId, current?.Title, current?.CompletedSegments ?? 0, current?.TotalSegments ?? 0,
            job.LastError);
    }

    public IReadOnlyList<ProjectTranslationWorkerProgress> GetWorkerProgress()
    {
        ProjectTranslationJob? job = CurrentJob;
        if (job is null) return [];
        return CalculateWorkerProgress(job);
    }

    private static IReadOnlyList<ProjectTranslationWorkerProgress> CalculateWorkerProgress(ProjectTranslationJob job)
    {
        return job.Workers.Select(worker =>
        {
            ProjectTranslationItem? current = worker.CurrentChapterId is Guid id
                ? job.Items.SingleOrDefault(item => item.ChapterId == id) : null;
            double rate = worker.ActiveSeconds > 0 ? worker.CompletedHanCharacters / worker.ActiveSeconds * 60d : 0;
            decimal cost = worker.HourlyPrice is decimal hourly
                ? hourly * (decimal)(worker.ActiveSeconds / 3600d) : 0;
            double totalTokensPerSecond = worker.ActiveSeconds > 0
                ? worker.OutputTokens / worker.ActiveSeconds : 0;
            double perSlotTokensPerSecond = worker.EvaluationSeconds > 0
                ? worker.OutputTokens / worker.EvaluationSeconds : 0;
            return new ProjectTranslationWorkerProgress(worker.WorkerId, worker.Name, worker.Status,
                current?.ChapterId, current?.Title, current?.CompletedSegments ?? 0, current?.TotalSegments ?? 0,
                rate, worker.CompletedChapters, worker.FailedChapters, cost, worker.LastError,
                totalTokensPerSecond, perSlotTokensPerSecond, worker.InputTokens, worker.OutputTokens, worker.ErrorSegments,
                worker.TruncatedOutputCount, worker.PossibleMissingContentCount, worker.TimeoutCount)
            { RunningChapters = worker.RunningChapterIds.Count, Slots = worker.Slots };
        }).ToArray();
    }

    private static ProjectTranslationAggregateMetrics CalculateAggregateMetrics(ProjectTranslationJob job,
        IReadOnlyList<ProjectTranslationWorkerProgress> workers)
    {
        double rate = workers.Sum(worker => worker.HanCharactersPerMinute);
        double remaining = job.Items.Where(item => item.Status is ProjectTranslationItemStatus.Pending or
            ProjectTranslationItemStatus.Running or ProjectTranslationItemStatus.Paused or
            ProjectTranslationItemStatus.Interrupted).Sum(item =>
                Math.Max(0, item.SourceHanCharacters - item.CompletedHanCharacters));
        TimeSpan? eta = rate > 0 && remaining > 0 ? TimeSpan.FromMinutes(remaining / rate) : null;
        long outputTokens = workers.Sum(worker => worker.OutputTokens);
        double evaluationSeconds = job.Workers.Sum(worker => worker.EvaluationSeconds);
        return new(rate, eta, workers.Sum(worker => worker.EstimatedCost),
            workers.Sum(worker => worker.TotalTokensPerSecond),
            evaluationSeconds > 0 ? outputTokens / evaluationSeconds : 0,
            workers.Sum(worker => worker.Slots));
    }

    public ProjectTranslationViewSnapshot? GetViewSnapshot()
    {
        ProjectTranslationJob? job = CurrentJob;
        if (job is null) return null;
        IReadOnlyList<ProjectTranslationWorkerProgress> workers = CalculateWorkerProgress(job);
        return new(job, CalculateProgress(job), workers, CalculateAggregateMetrics(job, workers));
    }

    private Task LaunchAsync(ProjectTranslationJob job, bool isNew, bool retryFailed, CancellationToken cancellationToken,
        bool acceptChangedBackendOrWorkers = false)
    {
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false }) throw new InvalidOperationException("Project translation job đang chạy.");
            cancellationToken.ThrowIfCancellationRequested();
            _pauseRequested = false;
            _cancelRequested = false;
            _stopRequested = false;
            _interruptionRequested = false;
            _progressChangesSinceSave = 0;
            _runTask = RunAsync(job, isNew, retryFailed, cancellationToken, acceptChangedBackendOrWorkers);
            return _runTask;
        }
    }

    private async Task RunAsync(ProjectTranslationJob requested, bool isNew, bool retryFailed,
        CancellationToken cancellationToken, bool acceptChangedBackendOrWorkers)
    {
        ProjectTranslationEnvironment launchEnvironment = await LoadEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        ProjectTranslationJob launchJob = ProjectTranslationJobStore.Clone(requested);
        bool configurationChanged = SynchronizeLaunchConfiguration(launchJob, launchEnvironment,
            isNew || acceptChangedBackendOrWorkers);
        if (!ActiveProjects.TryAdd(_session.Manifest.ProjectId, _coordinatorId))
            throw new InvalidOperationException("Project đã có coordinator khác đang ghi translation queue.");
        try
        {
            if (isNew)
            {
                ProjectTranslationJob saved = await _store.SaveNewAsync(launchJob, cancellationToken).ConfigureAwait(false);
                lock (_sync) _job = saved;
            }
            else
            {
                ProjectTranslationJob saved = configurationChanged
                    ? await _store.SaveAsync(launchJob, cancellationToken).ConfigureAwait(false)
                    : launchJob;
                lock (_sync) _job = saved;
            }
            await PersistAsync(job =>
            {
                job.Status = ProjectTranslationJobStatus.Running;
                job.LastError = null;
            }, cancellationToken).ConfigureAwait(false);

            if (CurrentJob!.Workers.Count > 0)
            {
                await RunWorkersAsync(retryFailed, cancellationToken).ConfigureAwait(false);
                ProjectTranslationJob parallelResult = CurrentJob!;
                if (parallelResult.Status is ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Cancelled or
                    ProjectTranslationJobStatus.Interrupted) return;
                bool workerErrors = parallelResult.Items.Any(item => item.Status is ProjectTranslationItemStatus.Failed or
                    ProjectTranslationItemStatus.NeedsReview or ProjectTranslationItemStatus.Cancelled);
                await FinishAsync(workerErrors ? ProjectTranslationJobStatus.CompletedWithErrors : ProjectTranslationJobStatus.Completed,
                    workerErrors ? "Hàng đợi song song kết thúc nhưng còn chapter lỗi hoặc cần kiểm tra kết quả không chắc chắn." : null)
                    .ConfigureAwait(false);
                return;
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_cancelRequested)
                {
                    await FinishAsync(ProjectTranslationJobStatus.Cancelled, null).ConfigureAwait(false);
                    return;
                }
                if (_stopRequested)
                {
                    await FinishAsync(ProjectTranslationJobStatus.Interrupted,
                        "Đã dừng nhanh. Bấm Tiếp tục dịch để xử lý phần còn lại.").ConfigureAwait(false);
                    return;
                }
                if (_pauseRequested)
                {
                    await FinishAsync(ProjectTranslationJobStatus.Paused, null).ConfigureAwait(false);
                    return;
                }
                ProjectTranslationItem? next = CurrentJob!.Items.FirstOrDefault(item => item.Status is
                    ProjectTranslationItemStatus.Pending or ProjectTranslationItemStatus.Paused or ProjectTranslationItemStatus.Interrupted);
                if (next is null) break;
                try { await RunChapterAsync(next.ChapterId, retryFailed, cancellationToken).ConfigureAwait(false); }
                catch (ProjectTranslationConfigurationChangedException ex)
                {
                    await FinishAsync(ProjectTranslationJobStatus.Interrupted,
                        ex.Message + " Các checkpoint đã xong được giữ nguyên; hãy tạo lại phần chưa hoàn tất.")
                        .ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    bool staleChapter = ContainsConflict(ex) || ex is FileNotFoundException;
                    await PersistAsync(job =>
                    {
                        ProjectTranslationItem target = job.Items.Single(item => item.ChapterId == next.ChapterId);
                        target.Status = staleChapter ? ProjectTranslationItemStatus.NeedsReview : ProjectTranslationItemStatus.Failed;
                        target.Disposition = staleChapter
                            ? ProjectTranslationItemDisposition.NeedsReview
                            : ProjectTranslationItemDisposition.Failed;
                        target.Error = ex.Message;
                        target.FinishedAt = DateTimeOffset.UtcNow;
                        job.CurrentChapterId = null;
                    }, CancellationToken.None).ConfigureAwait(false);
                    if (!staleChapter && ex is (IOException or UnauthorizedAccessException))
                    {
                        await FinishAsync(ProjectTranslationJobStatus.Interrupted,
                            "Đã dừng sau lỗi đọc/lưu chapter: " + ex.Message).ConfigureAwait(false);
                        return;
                    }
                }
                if (CurrentJob!.Status is ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Cancelled)
                    return;
            }

            bool errors = CurrentJob!.Items.Any(item => item.Status is ProjectTranslationItemStatus.Failed or
                ProjectTranslationItemStatus.NeedsReview or ProjectTranslationItemStatus.Cancelled);
            await FinishAsync(errors ? ProjectTranslationJobStatus.CompletedWithErrors : ProjectTranslationJobStatus.Completed,
                errors ? "Hàng đợi kết thúc nhưng còn chapter lỗi, bị hủy hoặc cần kiểm tra revision." : null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await FinishAsync(ProjectTranslationJobStatus.Interrupted,
                "Coordinator bị hủy; checkpoint đã commit được giữ. Bấm Resume để tiếp tục.").ConfigureAwait(false);
            throw;
        }
        finally
        {
            ActiveProjects.TryRemove(new KeyValuePair<Guid, Guid>(_session.Manifest.ProjectId, _coordinatorId));
            lock (_sync) { _chapterQueue = null; _workerQueues.Clear(); _workerActivity.Clear(); }
            Notify();
        }
    }

    private async Task RunWorkersAsync(bool retryFailed, CancellationToken cancellationToken)
    {
        ProjectTranslationJob job = CurrentJob!;
        ProjectTranslationWorkerDefinition[] definitions = ResolveWorkerDefinitions(job);
        lock (_sync)
        {
            _workerActivity.Clear();
            foreach (ProjectTranslationWorkerDefinition definition in definitions)
                _workerActivity[definition.WorkerId] = new WorkerActivity();
        }
        await PersistAsync(value =>
        {
            foreach (ProjectTranslationWorkerDefinition definition in definitions)
            {
                ProjectTranslationWorkerState worker = value.Workers.Single(item => item.WorkerId == definition.WorkerId);
                worker.Status = ProjectTranslationWorkerStatus.Running;
                worker.LastError = null;
                worker.CurrentChapterId = null;
                worker.RunningChapterIds.Clear();
                worker.Slots = definition.Slots;
            }
        }, cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(definitions.Select(worker => RunWorkerAsync(worker, retryFailed, cancellationToken)))
            .ConfigureAwait(false);
        if (_interruptionRequested)
        {
            await FinishAsync(ProjectTranslationJobStatus.Interrupted,
                "Đã dừng mọi worker sau lỗi đọc/lưu checkpoint; dữ liệu đã commit được giữ nguyên.").ConfigureAwait(false);
            return;
        }
        if (_cancelRequested)
        {
            await FinishAsync(ProjectTranslationJobStatus.Cancelled, null).ConfigureAwait(false);
            return;
        }
        if (_stopRequested)
        {
            await FinishAsync(ProjectTranslationJobStatus.Interrupted,
                "Đã dừng nhanh. Các chapter chưa hoàn tất vẫn có thể tiếp tục.").ConfigureAwait(false);
            return;
        }
        if (_pauseRequested)
        {
            await FinishAsync(ProjectTranslationJobStatus.Paused, null).ConfigureAwait(false);
            return;
        }
        if (CurrentJob!.Workers.Any(worker => worker.Status == ProjectTranslationWorkerStatus.Offline) &&
            CurrentJob.Items.Any(item => item.Status == ProjectTranslationItemStatus.Pending))
            await FinishAsync(ProjectTranslationJobStatus.Interrupted,
                "Một GPU đã offline; chapter chưa bắt đầu vẫn an toàn. Kết nối lại hoặc chuyển Pending sang worker còn hoạt động.")
                .ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(ProjectTranslationWorkerDefinition definition, bool retryFailed,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(Enumerable.Range(0, definition.Slots)
                .Select(_ => WorkerLoopAsync(definition, retryFailed, cancellationToken))).ConfigureAwait(false);
        }
        finally
        {
            await PersistAsync(job =>
            {
                ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == definition.WorkerId);
                worker.CurrentChapterId = null;
                worker.RunningChapterIds.Clear();
                if (_cancelRequested) worker.Status = ProjectTranslationWorkerStatus.Cancelled;
                else if (_stopRequested || cancellationToken.IsCancellationRequested)
                    worker.Status = ProjectTranslationWorkerStatus.Interrupted;
                else if (_pauseRequested) worker.Status = ProjectTranslationWorkerStatus.Paused;
                else if (worker.Status is not (ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted))
                    worker.Status = ProjectTranslationWorkerStatus.Completed;
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task WorkerLoopAsync(ProjectTranslationWorkerDefinition definition, bool retryFailed,
        CancellationToken cancellationToken)
    {
        while (!_pauseRequested && !_cancelRequested && !_stopRequested && !_interruptionRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid? chapterId = await ClaimNextAsync(definition.WorkerId, cancellationToken).ConfigureAwait(false);
            if (chapterId is null) break;
            try
            {
                await RunChapterAsync(chapterId.Value, retryFailed, cancellationToken, definition).ConfigureAwait(false);
            }
            catch (ProjectTranslationConfigurationChangedException error)
            {
                _interruptionRequested = true;
                await PersistAsync(job =>
                {
                    ProjectTranslationItem item = job.Items.Single(value => value.ChapterId == chapterId.Value);
                    item.Status = ProjectTranslationItemStatus.Interrupted;
                    item.Error = error.Message;
                    item.FinishedAt = null;
                    ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == definition.WorkerId);
                    worker.Status = ProjectTranslationWorkerStatus.Interrupted;
                    worker.LastError = error.Message;
                }, CancellationToken.None).ConfigureAwait(false);
                await CancelWorkerQueuesAsync(definition.WorkerId, chapterId.Value).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                bool conflict = ContainsConflict(error) || error is FileNotFoundException;
                await PersistAsync(job =>
                {
                    ProjectTranslationItem item = job.Items.Single(value => value.ChapterId == chapterId.Value);
                    item.Status = conflict ? ProjectTranslationItemStatus.NeedsReview : ProjectTranslationItemStatus.Failed;
                    item.Disposition = conflict
                        ? ProjectTranslationItemDisposition.NeedsReview
                        : ProjectTranslationItemDisposition.Failed;
                    item.Error = SafeWorkerError(error);
                    item.FinishedAt = DateTimeOffset.UtcNow;
                    ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == definition.WorkerId);
                    worker.FailedChapters++;
                    worker.LastError = item.Error;
                    if (error is IOException or UnauthorizedAccessException)
                    {
                        worker.Status = ProjectTranslationWorkerStatus.Interrupted;
                        _interruptionRequested = true;
                    }
                }, CancellationToken.None).ConfigureAwait(false);
                if (error is IOException or UnauthorizedAccessException)
                    await CancelWorkerQueuesAsync(definition.WorkerId, chapterId.Value).ConfigureAwait(false);
            }
            finally
            {
                await ReleaseWorkerChapterAsync(definition.WorkerId, chapterId.Value).ConfigureAwait(false);
            }
            ProjectTranslationWorkerState state = CurrentJob!.Workers.Single(worker => worker.WorkerId == definition.WorkerId);
            if (state.Status is ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted) break;
        }
    }

    private async Task<Guid?> ClaimNextAsync(Guid workerId, CancellationToken cancellationToken)
    {
        Guid? claimed = null;
        await MutateInMemoryAsync(job =>
        {
            if (_pauseRequested || _cancelRequested || _stopRequested || _interruptionRequested) return;
            ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerId);
            if (worker.Status is ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted) return;
            ProjectTranslationItem? item = job.Items.OrderBy(value => value.Order).FirstOrDefault(value =>
                (value.Status is ProjectTranslationItemStatus.Pending or ProjectTranslationItemStatus.Paused or
                    ProjectTranslationItemStatus.Interrupted) &&
                (value.AssignedWorkerId is null || value.AssignedWorkerId == workerId));
            if (item is null) return;
            item.AssignedWorkerId = workerId;
            item.Status = ProjectTranslationItemStatus.Running;
            item.StartedAt ??= DateTimeOffset.UtcNow;
            item.WorkerMetricsStartedAt ??= DateTimeOffset.UtcNow;
            item.Error = null;
            worker.Status = ProjectTranslationWorkerStatus.Running;
            worker.RunningChapterIds.Add(item.ChapterId);
            worker.CurrentChapterId = worker.RunningChapterIds[0];
            WorkerActivity activity;
            lock (_sync) activity = _workerActivity[workerId];
            if (activity.ActiveChapters++ == 0) activity.StartedAt = DateTimeOffset.UtcNow;
            claimed = item.ChapterId;
        }, cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    private async Task ReleaseWorkerChapterAsync(Guid workerId, Guid chapterId)
    {
        await PersistAsync(job =>
        {
            ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerId);
            worker.RunningChapterIds.Remove(chapterId);
            worker.CurrentChapterId = worker.RunningChapterIds.Count > 0 ? worker.RunningChapterIds[0] : null;
            WorkerActivity activity;
            lock (_sync) activity = _workerActivity[workerId];
            if (activity.ActiveChapters == 0) return;
            // Bank the busy time at every finished chapter, not only when the worker goes idle: with all slots busy
            // the worker never idles before the queue ends, and speed/ETA/cost would read 0 until then.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            BankWorkerBusyTime(worker, activity, now);
            activity.StartedAt = --activity.ActiveChapters == 0 ? default : now;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CancelWorkerQueuesAsync(Guid workerId, Guid exceptChapterId)
    {
        TranslationQueue[] queues;
        lock (_sync) queues = _workerQueues
            .Where(pair => pair.Key != exceptChapterId && pair.Value.WorkerId == workerId)
            .Select(pair => pair.Value.Queue).ToArray();
        await Task.WhenAll(queues.Select(CancelQueueAsync)).ConfigureAwait(false);
    }

    private ProjectTranslationWorkerDefinition[] ResolveWorkerDefinitions(ProjectTranslationJob job)
    {
        if (_workerDefinitions.Count == 0)
            throw new InvalidOperationException("Job song song cần kết nối lại các GPU đã chốt trước khi Resume.");
        var definitions = new List<ProjectTranslationWorkerDefinition>(job.Workers.Count);
        foreach (ProjectTranslationWorkerState snapshot in job.Workers)
        {
            ProjectTranslationWorkerDefinition definition = _workerDefinitions.SingleOrDefault(worker => worker.WorkerId == snapshot.WorkerId)
                ?? throw new InvalidOperationException($"Worker “{snapshot.Name}” chưa được kết nối lại.");
            if (definition.Backend != snapshot.Backend || definition.ModelOptions != snapshot.ModelOptions ||
                definition.SegmentationOptions != snapshot.SegmentationOptions ||
                snapshot.BackendIdentity.Length > 0 &&
                !string.Equals(definition.BackendIdentity, snapshot.BackendIdentity, StringComparison.Ordinal))
                throw new InvalidOperationException($"Backend/model của worker “{snapshot.Name}” đã đổi; job không tự gửi request.");
            definitions.Add(definition);
        }
        return definitions.ToArray();
    }

    private async Task RunChapterAsync(Guid chapterId, bool retryFailed, CancellationToken cancellationToken,
        ProjectTranslationWorkerDefinition? workerDefinition = null)
    {
        DateTimeOffset workerStarted = DateTimeOffset.UtcNow;
        ProjectTranslationItem item = CurrentJob!.Items.Single(value => value.ChapterId == chapterId);
        int completedSegmentsAtPlanCheckpoint = item.CompletedSegments;
        ProjectTranslationEnvironment environment = await LoadEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        ChapterDocument chapter = (await _repository.LoadChapterAsync(_session, chapterId, cancellationToken)
            .ConfigureAwait(false)).Chapter;
        string? conflict = await RefreshChapterRevisionAsync(item, chapter, cancellationToken).ConfigureAwait(false);
        if (conflict is not null)
        {
            await PersistAsync(job =>
            {
                ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                target.Status = ProjectTranslationItemStatus.NeedsReview;
                target.Disposition = ProjectTranslationItemDisposition.NeedsReview;
                target.Error = conflict;
                target.FinishedAt = DateTimeOffset.UtcNow;
                if (workerDefinition is not null)
                {
                    ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId);
                    worker.FailedChapters++;
                    worker.LastError = conflict;
                }
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        await RefreshBoundaryConfigurationAsync(environment, workerDefinition, cancellationToken).ConfigureAwait(false);
        item = CurrentJob!.Items.Single(value => value.ChapterId == chapterId);

        // A full retranslation runs once, as the chapter's first queue run in this job; a later run (retry, or a new run
        // after an interruption that left no resumable checkpoint) only fills what is missing.
        QueueMode mode = retryFailed && item.StartedAt is not null ? QueueMode.FailedSegments :
            item.RetranslateAll && item.ChapterQueueRunId is null ? QueueMode.AllSegments : QueueMode.MissingSegments;
        bool canResumeChapter = item.ChapterQueueRunId is Guid runId && chapter.Queue.RunId == runId &&
            chapter.Queue.Status is QueueStatus.Paused or QueueStatus.Interrupted or QueueStatus.Cancelled or QueueStatus.Faulted;
        // A finished chapter is intentionally left on its existing boundaries. In particular, switching an old GPU
        // project to a whole-chapter worker must not discard completed translations merely to discover there is no work.
        bool alreadyFullyTranslated = mode != QueueMode.AllSegments && chapter.Segments.Count > 0 &&
            chapter.Segments.All(segment => segment.HasCompletedTranslation || segment.Status == SegmentStatus.Approved);
        if (workerDefinition is not null && !canResumeChapter && !alreadyFullyTranslated)
        {
            chapter = await RunWorkerPreflightAsync(workerDefinition, chapterId, cancellationToken).ConfigureAwait(false);
            item = CurrentJob!.Items.Single(value => value.ChapterId == chapterId);
        }
        bool preflightChangedSegmentation = item.EligibilityDiagnostics.Any(value =>
            value.Stage == TranslationEligibilityDiagnosticStage.AfterWorkerPreflight &&
            value.WorkerPreflightChangedSegmentation);
        TranslationEligibilityDiagnostic eligibility = TranslationEligibilityDiagnostics.Analyze(chapter,
            canResumeChapter ? chapter.Queue.Mode : mode,
            TranslationEligibilityDiagnosticStage.BeforeEligibilityDecision,
            preflightChangedSegmentation);
        await MutateInMemoryAsync(job => job.Items.Single(value => value.ChapterId == chapterId)
            .RecordEligibilityDiagnostic(eligibility), CancellationToken.None).ConfigureAwait(false);
        item = CurrentJob!.Items.Single(value => value.ChapterId == chapterId);
        bool hasEligible = chapter.Segments.Any(segment => TranslationService.IsQueueEligible(segment,
            canResumeChapter ? chapter.Queue.Mode : mode));
        if (!hasEligible && !canResumeChapter)
        {
            bool completedByThisJob = item.ChapterQueueRunId is Guid completedRun && chapter.Queue.RunId == completedRun &&
                chapter.Queue.Status is QueueStatus.Completed or QueueStatus.CompletedWithWarnings;
            bool progressedSinceSavedJob = CompletedSegments(chapter) > completedSegmentsAtPlanCheckpoint;
            bool alreadyTranslated = chapter.Segments.Count > 0 && chapter.Segments.All(segment =>
                segment.HasCompletedTranslation || segment.Status == SegmentStatus.Approved);
            bool hasProtectedContent = chapter.Segments.Any(segment => segment.IsProtectedFromAutomaticTranslation);
            bool protectedOrCompleteOnly = hasProtectedContent && chapter.Segments.All(segment =>
                segment.IsProtectedFromAutomaticTranslation || segment.HasCompletedTranslation);
            await PersistProgressAsync(job =>
            {
                ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                if (completedByThisJob || progressedSinceSavedJob)
                {
                    target.Status = ProjectTranslationItemStatus.Completed;
                    target.Disposition = ProjectTranslationItemDisposition.TranslatedThisJob;
                    target.Error = null;
                }
                // A chapter chosen for retranslation that has nothing left to send is fully protected, not "already translated".
                else if (alreadyTranslated && mode != QueueMode.AllSegments)
                {
                    target.Status = ProjectTranslationItemStatus.Completed;
                    target.Disposition = ProjectTranslationItemDisposition.AlreadyTranslated;
                    target.Error = null;
                }
                else if (protectedOrCompleteOnly)
                {
                    target.Status = ProjectTranslationItemStatus.Skipped;
                    target.Disposition = ProjectTranslationItemDisposition.ProtectedContent;
                    target.Error = "Không dịch tự động vì toàn bộ nội dung còn lại là bản nháp, đã duyệt hoặc được bảo vệ.";
                }
                else
                {
                    target.Status = ProjectTranslationItemStatus.NeedsReview;
                    target.Disposition = ProjectTranslationItemDisposition.MissingEligibility;
                    target.Error = EligibilityFailureMessage(eligibility);
                }
                target.CompletedSegments = CompletedSegmentsFor(chapter, target);
                target.ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error);
                target.FinishedAt = DateTimeOffset.UtcNow;
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        var service = new TranslationService(chapter, workerDefinition?.Client ?? _client,
            new TranslationChapterStore(_session, _repository, chapter));
        service.UseProjectResources(environment.Resources);
        service.UseBookKnowledgeContext(environment.BookKnowledgeContext);
        var queue = new TranslationQueue(service);
        queue.ProgressChanged += (_, _) => OnChapterProgress(chapterId, service, queue);
        lock (_sync)
        {
            if (workerDefinition is null) _chapterQueue = queue;
            else _workerQueues[chapterId] = new WorkerQueue(workerDefinition.WorkerId, queue);
        }
        await MutateInMemoryAsync(job =>
        {
            ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
            target.Status = ProjectTranslationItemStatus.Running;
            target.StartedAt ??= DateTimeOffset.UtcNow;
            target.Error = null;
            if (workerDefinition is null) job.CurrentChapterId = chapterId;
            else
            {
                ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId);
                if (!worker.RunningChapterIds.Contains(chapterId)) worker.RunningChapterIds.Add(chapterId);
                worker.CurrentChapterId ??= chapterId;
            }
        }, CancellationToken.None).ConfigureAwait(false);

        try
        {
            Task run = canResumeChapter
                ? queue.ResumeAsync(workerDefinition?.ModelOptions ?? CurrentJob!.ModelOptions,
                    CurrentJob!.PromptPreset, cancellationToken)
                : queue.StartAsync(mode, workerDefinition?.ModelOptions ?? CurrentJob!.ModelOptions,
                    CurrentJob!.PromptPreset, cancellationToken);
            if (_pauseRequested) await queue.PauseAsync().ConfigureAwait(false);
            else if (_cancelRequested || _stopRequested || (workerDefinition is not null && CurrentJob!.Workers
                .Single(value => value.WorkerId == workerDefinition.WorkerId).Status is
                ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted))
                await CancelQueueAsync(queue).ConfigureAwait(false);
            Guid chapterRunId = service.GetSnapshot().Queue.RunId;
            await MutateInMemoryAsync(job => job.Items.Single(value => value.ChapterId == chapterId).ChapterQueueRunId = chapterRunId,
                CancellationToken.None).ConfigureAwait(false);
            await run.ConfigureAwait(false);
            ChapterDocument after = service.GetSnapshot();
            QueueStatus status = after.Queue.Status;
            bool chapterCompleted = !after.Segments.Any(segment => TranslationService.IsQueueEligible(segment, QueueMode.MissingSegments)) &&
                after.Segments.All(segment => segment.Status != SegmentStatus.Error);
            if (_pauseRequested || status == QueueStatus.Paused)
            {
                await PersistAsync(job =>
                {
                    UpdateItem(job, chapterId, after,
                        chapterCompleted ? ProjectTranslationItemStatus.Completed : ProjectTranslationItemStatus.Paused, null);
                    ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                    job.Status = ProjectTranslationJobStatus.Paused;
                    ClearCurrent(job, workerDefinition, chapterId);
                    UpdateWorkerCompletion(job, workerDefinition, target, after, workerStarted);
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (_cancelRequested || status == QueueStatus.Cancelled)
            {
                await PersistAsync(job =>
                {
                    bool stopped = _stopRequested;
                    bool workerHalted = workerDefinition is not null && job.Workers
                        .Single(value => value.WorkerId == workerDefinition.WorkerId).Status is
                        ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted;
                    UpdateItem(job, chapterId, after, chapterCompleted ? ProjectTranslationItemStatus.Completed :
                        stopped || workerHalted ? ProjectTranslationItemStatus.Interrupted : ProjectTranslationItemStatus.Cancelled,
                        chapterCompleted ? null : stopped || workerHalted
                            ? "Request đang chạy đã dừng; checkpoint trước đó được giữ."
                            : "Đã hủy; các segment đã checkpoint vẫn được giữ.");
                    ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                    if (stopped) job.Status = ProjectTranslationJobStatus.Interrupted;
                    else if (_cancelRequested) job.Status = ProjectTranslationJobStatus.Cancelled;
                    ClearCurrent(job, workerDefinition, chapterId);
                    UpdateWorkerCompletion(job, workerDefinition, target, after, workerStarted);
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            bool failed = status is QueueStatus.CompletedWithErrors or QueueStatus.Faulted;
            bool uncertainRemote = failed && workerDefinition?.Backend.Kind == GpuBackendKind.Remote;
            await PersistProgressAsync(job =>
            {
                UpdateItem(job, chapterId, after, uncertainRemote ? ProjectTranslationItemStatus.NeedsReview :
                    failed ? ProjectTranslationItemStatus.Failed : ProjectTranslationItemStatus.Completed,
                    failed ? after.Queue.LastError ?? "Chapter còn segment lỗi hoặc chưa hoàn tất." : null);
                ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                ClearCurrent(job, workerDefinition, chapterId);
                UpdateWorkerCompletion(job, workerDefinition, target, after, workerStarted);
                if (uncertainRemote && workerDefinition is not null)
                {
                    ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId);
                    worker.Status = ProjectTranslationWorkerStatus.Offline;
                    worker.LastError = "Remote mất kết nối hoặc trả kết quả không chắc chắn; chapter cần kiểm tra trước khi Retry.";
                }
            }, CancellationToken.None).ConfigureAwait(false);
            if (uncertainRemote && workerDefinition is not null)
                await CancelWorkerQueuesAsync(workerDefinition.WorkerId, chapterId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ChapterDocument snapshot = service.GetSnapshot();
            bool conflictFailure = ContainsConflict(ex);
            await PersistAsync(job =>
            {
                UpdateItem(job, chapterId, snapshot,
                    conflictFailure ? ProjectTranslationItemStatus.NeedsReview : ProjectTranslationItemStatus.Failed, ex.Message);
                ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                ClearCurrent(job, workerDefinition, chapterId);
                UpdateWorkerCompletion(job, workerDefinition, target, snapshot, workerStarted);
            }, CancellationToken.None).ConfigureAwait(false);
            if (ex is IOException or UnauthorizedAccessException)
            {
                if (workerDefinition is null)
                    await FinishAsync(ProjectTranslationJobStatus.Interrupted,
                        "Đã dừng sau lỗi lưu/checkpoint: " + ex.Message).ConfigureAwait(false);
                else
                {
                    _interruptionRequested = true;
                    await PersistAsync(job =>
                    {
                        ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId);
                        worker.Status = ProjectTranslationWorkerStatus.Interrupted;
                        worker.LastError = "Lỗi lưu/checkpoint; coordinator sẽ dừng sau khi worker còn lại kết thúc request hiện tại.";
                    }, CancellationToken.None).ConfigureAwait(false);
                    await CancelWorkerQueuesAsync(workerDefinition.WorkerId, chapterId).ConfigureAwait(false);
                }
                return;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (workerDefinition is null && ReferenceEquals(_chapterQueue, queue)) _chapterQueue = null;
                else if (workerDefinition is not null) _workerQueues.Remove(chapterId);
            }
        }
    }

    private async Task<string?> RefreshChapterRevisionAsync(ProjectTranslationItem item, ChapterDocument chapter,
        CancellationToken cancellationToken)
    {
        if (ProjectTranslationFingerprints.Source(chapter) != item.SourceFingerprint)
            return "Nguyên văn hoặc ranh giới segment đã đổi sau khi enqueue.";
        string protectedFingerprint = ProjectTranslationFingerprints.Protected(chapter);
        if (protectedFingerprint != item.ProtectedFingerprint)
        {
            await PersistAsync(job =>
            {
                ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapter.Id);
                target.ProtectedFingerprint = protectedFingerprint;
                target.TotalSegments = chapter.Segments.Count;
                target.CompletedSegments = CompletedSegmentsFor(chapter, target);
                target.ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error);
                target.Error = null;
            }, cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    private async Task<ChapterDocument> RunWorkerPreflightAsync(ProjectTranslationWorkerDefinition definition,
        Guid chapterId, CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectTranslationJob expected;
            lock (_sync)
            {
                expected = ProjectTranslationJobStore.Clone(_job
                    ?? throw new InvalidOperationException("Translation job chưa được lưu."));
            }
            WorkerPreflightResult result = await _repository.PreflightWorkerChapterAsync(_session,
                expected, definition.WorkerId, chapterId, definition.SegmentationOptions, cancellationToken)
                .ConfigureAwait(false);
            lock (_sync) _job = result.Job;
            return result.Chapter;
        }
        finally
        {
            _persistGate.Release();
            Notify();
        }
    }

    private async Task RefreshBoundaryConfigurationAsync(ProjectTranslationEnvironment environment,
        ProjectTranslationWorkerDefinition? workerDefinition, CancellationToken cancellationToken)
    {
        ProjectTranslationJob job = CurrentJob!;
        if (workerDefinition is null && ValidateEnvironment(job, environment) is string backendConflict)
            throw new ProjectTranslationConfigurationChangedException(backendConflict +
                " Hãy Pause rồi bấm Tiếp tục dịch sau khi backend mới đã kết nối.");
        bool changed = environment.Resources.Revision != job.ProjectResourceRevision ||
            environment.PromptPreset != job.PromptPreset ||
            workerDefinition is null && environment.ModelOptions != job.ModelOptions;
        if (!changed) return;
        await PersistAsync(value =>
        {
            value.ProjectResourceRevision = environment.Resources.Revision;
            value.PromptPreset = environment.PromptPreset with { };
            if (workerDefinition is null) value.ModelOptions = environment.ModelOptions with { };
            value.LastError = "Cấu hình mới sẽ được áp dụng từ chương tiếp theo.";
        }, cancellationToken).ConfigureAwait(false);
    }

    private bool SynchronizeLaunchConfiguration(ProjectTranslationJob job,
        ProjectTranslationEnvironment environment, bool acceptChangedBackendOrWorkers)
    {
        bool changed = false;
        if (job.ProjectResourceRevision != environment.Resources.Revision)
        {
            job.ProjectResourceRevision = environment.Resources.Revision;
            changed = true;
        }
        if (job.PromptPreset != environment.PromptPreset)
        {
            job.PromptPreset = environment.PromptPreset with { };
            changed = true;
        }
        if (job.Workers.Count == 0)
        {
            GpuBackendSnapshot current = environment.Backend ??
                GpuBackendSnapshot.LocalDefault(environment.ModelOptions.Model);
            string? backendConflict = ValidateEnvironment(job, environment);
            if (backendConflict is not null && !acceptChangedBackendOrWorkers)
                throw new InvalidOperationException(backendConflict);
            if (job.ModelOptions != environment.ModelOptions)
            {
                job.ModelOptions = environment.ModelOptions with { };
                changed = true;
            }
            if (job.Backend != current)
            {
                job.Backend = current with { };
                changed = true;
            }
            return changed;
        }

        if (!acceptChangedBackendOrWorkers)
        {
            _ = ResolveWorkerDefinitions(job);
            return changed;
        }
        return SynchronizeWorkerSnapshots(job) || changed;
    }

    private bool SynchronizeWorkerSnapshots(ProjectTranslationJob job)
    {
        if (_workerDefinitions.Count == 0)
            throw new InvalidOperationException("Hãy kết nối ít nhất một GPU hợp lệ trước khi tiếp tục dịch.");
        foreach (ProjectTranslationWorkerDefinition definition in _workerDefinitions) definition.Validate();
        bool exact = job.Workers.Count == _workerDefinitions.Count && job.Workers.All(snapshot =>
            _workerDefinitions.Any(definition => definition.WorkerId == snapshot.WorkerId &&
                definition.Backend == snapshot.Backend && definition.ModelOptions == snapshot.ModelOptions &&
                definition.SegmentationOptions == snapshot.SegmentationOptions &&
                string.Equals(definition.BackendIdentity, snapshot.BackendIdentity, StringComparison.Ordinal)));
        if (exact) return false;

        var unused = job.Workers.ToList();
        var remap = new Dictionary<Guid, Guid>();
        var refreshed = new List<ProjectTranslationWorkerState>(_workerDefinitions.Count);
        foreach (ProjectTranslationWorkerDefinition definition in _workerDefinitions)
        {
            ProjectTranslationWorkerState? previous = unused.FirstOrDefault(value => value.WorkerId == definition.WorkerId)
                ?? unused.FirstOrDefault(value => value.ProfileId == definition.Backend.ProfileId)
                ?? unused.FirstOrDefault(value => value.Backend.Kind == definition.Backend.Kind);
            if (previous is not null)
            {
                if (previous.ProfileId == definition.Backend.ProfileId && previous.BackendIdentity.Length > 0 &&
                    !string.Equals(previous.BackendIdentity, definition.BackendIdentity, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Danh tính kết nối của “{previous.Name}” đã đổi; hãy xác nhận HostKey trước khi gửi nguyên văn.");
                unused.Remove(previous);
                remap[previous.WorkerId] = definition.WorkerId;
            }
            refreshed.Add(CreateWorkerState(definition, previous));
        }
        if (unused.Count > 0)
        {
            ProjectTranslationWorkerState destination = refreshed[0];
            foreach (ProjectTranslationWorkerState historical in unused) AddHistoricalMetrics(destination, historical);
        }
        foreach (ProjectTranslationItem item in job.Items)
        {
            if (item.AssignedWorkerId is not Guid previousId) continue;
            item.AssignedWorkerId = remap.TryGetValue(previousId, out Guid currentId) ? currentId : null;
        }
        job.Workers = refreshed;
        ProjectTranslationWorkerDefinition primary = _workerDefinitions[0];
        job.ModelOptions = primary.ModelOptions with { };
        job.Backend = primary.Backend with { };
        job.CurrentChapterId = null;
        return true;
    }

    private static ProjectTranslationWorkerState CreateWorkerState(ProjectTranslationWorkerDefinition definition,
        ProjectTranslationWorkerState? previous) => new()
    {
        WorkerId = definition.WorkerId,
        ProfileId = definition.Backend.ProfileId,
        Name = definition.Backend.ProfileName,
        Backend = definition.Backend with { },
        ModelOptions = definition.ModelOptions with { },
        SegmentationOptions = definition.SegmentationOptions with { },
        Slots = definition.Slots,
        HourlyPrice = definition.HourlyPrice,
        BackendIdentity = definition.BackendIdentity,
        Status = ProjectTranslationWorkerStatus.Ready,
        CompletedChapters = previous?.CompletedChapters ?? 0,
        FailedChapters = previous?.FailedChapters ?? 0,
        CompletedHanCharacters = previous?.CompletedHanCharacters ?? 0,
        ActiveSeconds = previous?.ActiveSeconds ?? 0,
        InputTokens = previous?.InputTokens ?? 0,
        OutputTokens = previous?.OutputTokens ?? 0,
        EvaluationSeconds = previous?.EvaluationSeconds ?? 0,
        ErrorSegments = previous?.ErrorSegments ?? 0,
        TruncatedOutputCount = previous?.TruncatedOutputCount ?? 0,
        PossibleMissingContentCount = previous?.PossibleMissingContentCount ?? 0,
        TimeoutCount = previous?.TimeoutCount ?? 0
    };

    private static void AddHistoricalMetrics(ProjectTranslationWorkerState destination,
        ProjectTranslationWorkerState historical)
    {
        destination.CompletedChapters += historical.CompletedChapters;
        destination.FailedChapters += historical.FailedChapters;
        destination.CompletedHanCharacters += historical.CompletedHanCharacters;
        destination.ActiveSeconds += historical.ActiveSeconds;
        destination.InputTokens += historical.InputTokens;
        destination.OutputTokens += historical.OutputTokens;
        destination.EvaluationSeconds += historical.EvaluationSeconds;
        destination.ErrorSegments += historical.ErrorSegments;
        destination.TruncatedOutputCount += historical.TruncatedOutputCount;
        destination.PossibleMissingContentCount += historical.PossibleMissingContentCount;
        destination.TimeoutCount += historical.TimeoutCount;
    }

    private static void CarryHistoricalWorkerMetrics(ProjectTranslationJob current,
        ProjectTranslationJob replacement)
    {
        if (replacement.Workers.Count == 0) return;
        foreach (ProjectTranslationWorkerState historical in current.Workers)
        {
            ProjectTranslationWorkerState destination = replacement.Workers.SingleOrDefault(worker =>
                worker.WorkerId == historical.WorkerId) ?? replacement.Workers[0];
            AddHistoricalMetrics(destination, historical);
        }
    }

    private static string? ValidateEnvironment(ProjectTranslationJob job, ProjectTranslationEnvironment environment)
    {
        GpuBackendSnapshot planned = job.Backend ?? GpuBackendSnapshot.LocalDefault(job.ModelOptions.Model);
        GpuBackendSnapshot current = environment.Backend ?? GpuBackendSnapshot.LocalDefault(environment.ModelOptions.Model);
        if (planned.ProfileId != current.ProfileId || planned.Kind != current.Kind ||
            !string.Equals(planned.Endpoint, current.Endpoint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(planned.Model, current.Model, StringComparison.Ordinal))
            return "Backend/model đã đổi sau khi tạo Queue Plan. Hãy xác nhận server mới và tạo lại Queue Plan; job cũ không tự gửi request.";
        return null;
    }

    private async Task<ProjectTranslationEnvironment> LoadEnvironmentAsync(CancellationToken cancellationToken)
    {
        ProjectTranslationEnvironment environment = await _environmentLoader(cancellationToken).ConfigureAwait(false);
        environment.ModelOptions.Validate();
        environment.PromptPreset.Validate();
        environment.Resources.Validate();
        environment.Backend?.Validate();
        if (environment.Resources.ProjectId != _session.Manifest.ProjectId)
            throw new InvalidOperationException("Tài nguyên translation queue thuộc Project khác.");
        return environment;
    }

    private void OnChapterProgress(Guid chapterId, TranslationService service, TranslationQueue queue)
    {
        ChapterDocument chapter = service.GetSnapshot();
        bool hasWorker;
        lock (_sync)
        {
            ProjectTranslationItem? current = _job?.Items.SingleOrDefault(value => value.ChapterId == chapterId);
            hasWorker = current?.AssignedWorkerId is not null;
            if (!hasWorker && current is not null)
            {
                current.TotalSegments = chapter.Segments.Count;
                current.CompletedSegments = CompletedSegmentsFor(chapter, current);
                current.ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error);
                current.CompletedHanCharacters = CompletedHanCharacters(chapter);
            }
        }
        if (!hasWorker)
        {
            Notify();
            return;
        }
        PersistProgressAsync(job =>
        {
            ProjectTranslationItem? item = job.Items.SingleOrDefault(value => value.ChapterId == chapterId);
            if (item is null) return;
            item.TotalSegments = chapter.Segments.Count;
            item.CompletedSegments = CompletedSegmentsFor(chapter, item);
            item.ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error);
            item.CompletedHanCharacters = CompletedHanCharacters(chapter);
            if (item.AssignedWorkerId is not Guid workerId) return;
            ProjectTranslationWorkerState? worker = job.Workers.SingleOrDefault(value => value.WorkerId == workerId);
            if (worker is null) return;
            RecordWorkerSegmentMetrics(worker, item, chapter, item.StartedAt ?? DateTimeOffset.UtcNow);
            WorkerActivity? activity;
            lock (_sync) _workerActivity.TryGetValue(workerId, out activity);
            if (activity is null || activity.ActiveChapters == 0) return;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            BankWorkerBusyTime(worker, activity, now);
            activity.StartedAt = now;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    private async Task PersistAsync(Action<ProjectTranslationJob> update, CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectTranslationJob proposed;
            lock (_sync)
            {
                if (_job is null) throw new InvalidOperationException("Translation job chưa được lưu.");
                proposed = ProjectTranslationJobStore.Clone(_job);
            }
            update(proposed);
            ProjectTranslationJob saved = await _store.SaveAsync(proposed, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _job = saved;
                _progressChangesSinceSave = 0;
            }
        }
        finally { _persistGate.Release(); }
        Notify();
    }

    private async Task MutateInMemoryAsync(Action<ProjectTranslationJob> update,
        CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectTranslationJob proposed;
            lock (_sync)
            {
                proposed = ProjectTranslationJobStore.Clone(_job
                    ?? throw new InvalidOperationException("Translation job chưa được lưu."));
            }
            update(proposed);
            proposed.Validate();
            lock (_sync) _job = proposed;
        }
        finally { _persistGate.Release(); }
        Notify();
    }

    private async Task PersistProgressAsync(Action<ProjectTranslationJob> update,
        CancellationToken cancellationToken)
    {
        await _persistGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectTranslationJob proposed;
            int checkpointInterval;
            lock (_sync)
            {
                proposed = ProjectTranslationJobStore.Clone(_job
                    ?? throw new InvalidOperationException("Translation job chưa được lưu."));
                // Persist roughly ten project-level progress checkpoints. Chapter files remain the
                // authoritative per-segment checkpoints, and Pause/Stop/Finish always force a job save.
                checkpointInterval = Math.Max(1, (proposed.Items.Count + 9) / 10);
            }
            update(proposed);
            proposed.Validate();
            bool save;
            lock (_sync)
            {
                _progressChangesSinceSave++;
                save = _progressChangesSinceSave >= checkpointInterval;
            }
            if (save)
            {
                ProjectTranslationJob saved = await _store.SaveAsync(proposed, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _job = saved;
                    _progressChangesSinceSave = 0;
                }
            }
            else lock (_sync) _job = proposed;
        }
        finally { _persistGate.Release(); }
        Notify();
    }

    private async Task FinishAsync(ProjectTranslationJobStatus status, string? error)
    {
        await PersistAsync(job =>
        {
            job.Status = status;
            job.LastError = error;
            job.CurrentChapterId = null;
        }, CancellationToken.None).ConfigureAwait(false);
        // Chapter files already contain every completed segment. Flush their accumulated summaries once
        // at a durable queue boundary instead of rewriting the whole manifest for every segment/GPU.
        await _repository.SaveManifestAsync(_session, CancellationToken.None).ConfigureAwait(false);
    }

    private static void UpdateItem(ProjectTranslationJob job, Guid chapterId, ChapterDocument chapter,
        ProjectTranslationItemStatus status, string? error)
    {
        ProjectTranslationItem item = job.Items.Single(value => value.ChapterId == chapterId);
        item.Status = status;
        item.TotalSegments = chapter.Segments.Count;
        item.CompletedSegments = CompletedSegmentsFor(chapter, item);
        item.ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error);
        item.CompletedHanCharacters = CompletedHanCharacters(chapter);
        item.SourceFingerprint = ProjectTranslationFingerprints.Source(chapter);
        item.ProtectedFingerprint = ProjectTranslationFingerprints.Protected(chapter);
        item.Error = error;
        item.Disposition = status switch
        {
            ProjectTranslationItemStatus.Completed when item.Disposition == ProjectTranslationItemDisposition.AlreadyTranslated
                => ProjectTranslationItemDisposition.AlreadyTranslated,
            ProjectTranslationItemStatus.Completed => ProjectTranslationItemDisposition.TranslatedThisJob,
            ProjectTranslationItemStatus.Failed => ProjectTranslationItemDisposition.Failed,
            ProjectTranslationItemStatus.NeedsReview when item.Disposition == ProjectTranslationItemDisposition.MissingEligibility
                => ProjectTranslationItemDisposition.MissingEligibility,
            ProjectTranslationItemStatus.NeedsReview => ProjectTranslationItemDisposition.NeedsReview,
            ProjectTranslationItemStatus.Cancelled => ProjectTranslationItemDisposition.Cancelled,
            _ => item.Disposition
        };
        if (status is not (ProjectTranslationItemStatus.Paused or ProjectTranslationItemStatus.Interrupted))
            item.FinishedAt = DateTimeOffset.UtcNow;
    }

    private static void ClearCurrent(ProjectTranslationJob job, ProjectTranslationWorkerDefinition? workerDefinition,
        Guid chapterId)
    {
        if (workerDefinition is null) job.CurrentChapterId = null;
        else
        {
            ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId);
            worker.RunningChapterIds.Remove(chapterId);
            worker.CurrentChapterId = worker.RunningChapterIds.Count > 0 ? worker.RunningChapterIds[0] : null;
        }
    }

    private static void UpdateWorkerCompletion(ProjectTranslationJob job,
        ProjectTranslationWorkerDefinition? workerDefinition, ProjectTranslationItem item,
        ChapterDocument chapter, DateTimeOffset started)
    {
        if (workerDefinition is null) return;
        ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId);
        if (item.Status == ProjectTranslationItemStatus.Completed)
        {
            worker.CompletedChapters++;
        }
        else if (item.Status is ProjectTranslationItemStatus.Failed or ProjectTranslationItemStatus.NeedsReview)
            worker.FailedChapters++;
        RecordWorkerSegmentMetrics(worker, item, chapter, started);
        if (item.Status is ProjectTranslationItemStatus.Completed or ProjectTranslationItemStatus.Failed or
            ProjectTranslationItemStatus.NeedsReview or ProjectTranslationItemStatus.Cancelled)
            item.WorkerMetricsRecorded = true;
        worker.LastError = item.Error;
    }

    private static void RecordWorkerSegmentMetrics(ProjectTranslationWorkerState worker,
        ProjectTranslationItem item, ChapterDocument chapter, DateTimeOffset fallbackStarted)
    {
        DateTimeOffset started = item.WorkerMetricsStartedAt ?? item.StartedAt ?? fallbackStarted;
        item.RecordedWorkerAttemptIds ??= [];
        item.RecordedWorkerSegmentIds ??= [];
        var recordedAttempts = item.RecordedWorkerAttemptIds.ToHashSet();
        foreach (TranslationSegment segment in chapter.Segments)
        {
            TranslationAttempt[] newAttempts = segment.Attempts.Where(attempt => attempt.StartedAt >= started &&
                attempt.FinishedAt is not null && attempt.Outcome != AttemptOutcome.InProgress &&
                !recordedAttempts.Contains(attempt.Id)).ToArray();
            if (newAttempts.Length == 0) continue;
            foreach (TranslationAttempt attempt in newAttempts)
            {
                worker.InputTokens += attempt.Metrics?.InputTokens ?? 0;
                worker.OutputTokens += attempt.Metrics?.OutputTokens ?? 0;
                worker.EvaluationSeconds += Math.Max(0,
                    (attempt.Metrics?.EvaluationDurationNanoseconds ?? 0) / 1_000_000_000d);
                worker.TruncatedOutputCount += attempt.Issues.Count(issue => issue.Code == IssueCode.TruncatedOutput);
                worker.PossibleMissingContentCount += attempt.Issues.Count(issue => issue.Code == IssueCode.PossibleMissingContent);
                if (attempt.Error?.Contains("timeout", StringComparison.OrdinalIgnoreCase) == true ||
                    attempt.Error?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true)
                    worker.TimeoutCount++;
                item.RecordedWorkerAttemptIds.Add(attempt.Id);
                recordedAttempts.Add(attempt.Id);
            }
            if (segment.HasCompletedTranslation && !item.RecordedWorkerSegmentIds.Contains(segment.Id))
            {
                worker.CompletedHanCharacters += segment.HanCharacterCount > 0
                    ? segment.HanCharacterCount : CountHan(segment.SourceText);
                item.RecordedWorkerSegmentIds.Add(segment.Id);
            }
            if (segment.Status == SegmentStatus.Error) worker.ErrorSegments++;
        }
    }

    private static void PrepareItemMetricsForRetry(ProjectTranslationItem item)
    {
        item.WorkerMetricsRecorded = false;
        item.WorkerMetricsStartedAt = DateTimeOffset.UtcNow;
        item.RecordedWorkerAttemptIds?.Clear();
        item.RecordedWorkerSegmentIds?.Clear();
    }

    private static void BankWorkerBusyTime(ProjectTranslationWorkerState worker, WorkerActivity activity,
        DateTimeOffset now)
    {
        if (activity.ActiveChapters > 0 && activity.StartedAt != default)
            worker.ActiveSeconds += Math.Max(0, (now - activity.StartedAt).TotalSeconds);
    }

    private static async Task CancelQueueAsync(TranslationQueue queue)
    {
        try { await queue.CancelAsync().ConfigureAwait(false); }
        // Another slot/control path can finish and dispose the linked CTS between the running check and Cancel().
        // At that point this queue is already crossing the requested cancellation boundary.
        catch (ObjectDisposedException) { }
    }

    private static int CompletedSegments(ChapterDocument chapter) => chapter.Segments.Count(segment =>
        segment.Status == SegmentStatus.Approved || segment.HasCompletedTranslation);

    /// <summary>
    /// Progress of one queue item. A retranslated chapter counts only segments translated since the item started, plus
    /// the ones the user wrote or approved (those are kept, not sent again); older AI translations do not count.
    /// </summary>
    internal static int CompletedSegmentsFor(ChapterDocument chapter, ProjectTranslationItem item) => !item.RetranslateAll
        ? CompletedSegments(chapter)
        : chapter.Segments.Count(segment => segment.Status == SegmentStatus.Approved || segment.UserDraft is not null ||
            item.StartedAt is DateTimeOffset started && segment.HasCompletedTranslation && segment.Attempts.Any(attempt =>
                attempt.Outcome == AttemptOutcome.Completed && attempt.StartedAt >= started));

    private static int CompletedHanCharacters(ChapterDocument chapter) => chapter.Segments
        .Where(segment => segment.Status == SegmentStatus.Approved || segment.HasCompletedTranslation)
        .Sum(segment => segment.HanCharacterCount > 0 ? segment.HanCharacterCount : CountHan(segment.SourceText));

    private static string EligibilityFailureMessage(TranslationEligibilityDiagnostic diagnostic)
    {
        int unresolved = diagnostic.ReasonCounts.Where(pair => pair.Key is not (
                QueueEligibilityReason.CompletedTranslation or QueueEligibilityReason.ProtectedUserDraft or
                QueueEligibilityReason.ProtectedApproval or QueueEligibilityReason.ProtectedPreviousEdit))
            .Sum(pair => pair.Value);
        if (diagnostic.FailedAttemptCount > 0 || diagnostic.InvalidOutputAttemptCount > 0)
            return $"Còn {unresolved:N0} đoạn chưa có bản dịch hợp lệ; " +
                $"{diagnostic.FailedAttemptCount:N0} attempt lỗi và {diagnostic.InvalidOutputAttemptCount:N0} đầu ra không hợp lệ. " +
                "Kiểm tra chi tiết rồi dùng Dịch lại lỗi.";
        if (diagnostic.InProgressAttemptCount > 0)
            return $"Còn {unresolved:N0} đoạn chưa có bản dịch hợp lệ nhưng có " +
                $"{diagnostic.InProgressAttemptCount:N0} attempt chưa kết thúc; cần kiểm tra trạng thái trước khi Retry.";
        return $"Còn {unresolved:N0}/{diagnostic.SegmentCount:N0} đoạn chưa có bản dịch hợp lệ " +
            "nhưng không có đoạn đủ điều kiện. Xem chẩn đoán eligibility trước khi Retry.";
    }

    private static int CountHan(string text) => text.Count(character => character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff');

    private static string SafeWorkerError(Exception error) => error switch
    {
        IOException => "Không đọc hoặc lưu được checkpoint của chapter.",
        UnauthorizedAccessException => "Không có quyền ghi checkpoint của chapter.",
        LlmRequestException request when request.Kind == LlmErrorKind.Connection => "GPU mất kết nối; kết quả request hiện tại chưa xác định.",
        _ => error.Message
    };

    private static bool ContainsConflict(Exception exception) =>
        exception is InvalidOperationException &&
        (exception.Message.Contains("đã đổi", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("được sửa", StringComparison.OrdinalIgnoreCase) ||
         exception.Message.Contains("không còn", StringComparison.OrdinalIgnoreCase)) ||
        exception.InnerException is not null && ContainsConflict(exception.InnerException) ||
        exception is AggregateException aggregate && aggregate.InnerExceptions.Any(ContainsConflict);

    private sealed class ProjectTranslationConfigurationChangedException(string message) : InvalidOperationException(message);

    private sealed record WorkerQueue(Guid WorkerId, TranslationQueue Queue);

    private sealed class WorkerActivity
    {
        public int ActiveChapters { get; set; }
        public DateTimeOffset StartedAt { get; set; }
    }

    private void Notify()
    {
        EventHandler? handlers = ProgressChanged;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList().Cast<EventHandler>())
        {
            try { handler(this, EventArgs.Empty); }
            catch (Exception ex) { Trace.TraceError($"ProjectTranslationCoordinator observer failed: {ex}"); }
        }
    }
}

internal static class ProjectTranslationFingerprints
{
    public static string Source(ChapterDocument chapter) => Hash(new
    {
        chapter.Id,
        chapter.ProjectId,
        chapter.OriginalText,
        Segments = chapter.Segments.Select(segment => new
        {
            segment.Id, segment.Order, segment.StartUtf16, segment.SourceText
        })
    });

    public static string Protected(ChapterDocument chapter) => Hash(chapter.Segments.Select(segment => new
    {
        segment.Id,
        segment.UserDraft,
        segment.DraftRevision,
        segment.ProtectedByPreviousEdit,
        segment.PreservedText,
        Approvals = segment.Approvals.Select(approval => new
        {
            approval.Id, approval.ApprovedAt, approval.DraftRevision, approval.Text, approval.SourceAttemptId
        }),
        DraftHistory = segment.DraftHistory.Select(draft => new
        {
            draft.Id, draft.RecordedAt, draft.Revision, draft.Text, draft.SourceAttemptId, draft.Reason
        })
    }));

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, DocumentJson.Options))));
}
