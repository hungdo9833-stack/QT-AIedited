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
    private readonly IOllamaClient _client;
    private readonly IReadOnlyList<ProjectTranslationWorkerDefinition> _workerDefinitions;
    private readonly ProjectTranslationJobStore _store;
    private readonly Func<CancellationToken, Task<ProjectTranslationEnvironment>> _environmentLoader;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Guid _coordinatorId = Guid.NewGuid();
    private ProjectTranslationJob? _job;
    private TranslationQueue? _chapterQueue;
    private readonly Dictionary<Guid, TranslationQueue> _workerQueues = [];
    private Task? _runTask;
    private bool _pauseRequested;
    private bool _cancelRequested;
    private bool _interruptionRequested;

    public ProjectTranslationCoordinator(ProjectSession session, ProjectRepository repository, IOllamaClient client,
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

    public async Task<ProjectTranslationJob?> LoadAsync(CancellationToken cancellationToken = default)
    {
        ProjectTranslationJob? loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync) _job = loaded;
        Notify();
        return loaded is null ? null : ProjectTranslationJobStore.Clone(loaded);
    }

    public async Task<ProjectTranslationJob> PlanAsync(IEnumerable<Guid> chapterIds,
        CancellationToken cancellationToken = default)
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChapterDocument chapter = (await _repository.LoadChapterAsync(_session, entries[index].ChapterId, cancellationToken)
                .ConfigureAwait(false)).Chapter;
            items.Add(new ProjectTranslationItem
            {
                ChapterId = chapter.Id,
                Number = entries[index].Number,
                Order = index + 1,
                Title = entries[index].VietnameseTitle,
                SourceFingerprint = ProjectTranslationFingerprints.Source(chapter),
                ProtectedFingerprint = ProjectTranslationFingerprints.Protected(chapter),
                EnqueuedDocumentFingerprint = ProjectTranslationFingerprints.Document(chapter),
                TotalSegments = chapter.Segments.Count,
                CompletedSegments = CompletedSegments(chapter),
                ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error),
                SourceHanCharacters = chapter.Segments.Sum(segment => CountHan(segment.SourceText))
            });
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var job = new ProjectTranslationJob
        {
            ProjectId = _session.Manifest.ProjectId,
            Status = ProjectTranslationJobStatus.Draft,
            ModelOptions = _workerDefinitions.Count > 0 ? _workerDefinitions[0].ModelOptions with { } : environment.ModelOptions with { },
            Backend = _workerDefinitions.Count > 0 ? _workerDefinitions[0].Backend with { } :
                (environment.Backend ?? OllamaBackendSnapshot.LocalDefault(environment.ModelOptions.Model)) with { },
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
                Status = ProjectTranslationWorkerStatus.Ready
            }).ToList(),
            Items = items
        };
        job.Validate();
        return job;
    }

    public Task StartAsync(ProjectTranslationJob plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Status != ProjectTranslationJobStatus.Draft)
            throw new InvalidOperationException("Queue Plan phải ở trạng thái Draft trước khi bắt đầu.");
        return LaunchAsync(plan, isNew: true, retryFailed: false, cancellationToken);
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        ProjectTranslationJob job = CurrentJob ?? await LoadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Project chưa có translation job để tiếp tục.");
        if (job.Status is not (ProjectTranslationJobStatus.Paused or ProjectTranslationJobStatus.Interrupted))
            throw new InvalidOperationException("Translation job chưa ở trạng thái có thể Resume.");
        await LaunchAsync(job, isNew: false, retryFailed: false, cancellationToken).ConfigureAwait(false);
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
                item.Error = null;
                item.FinishedAt = null;
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
        ProjectTranslationJob replacement = await PlanAsync(unfinished, cancellationToken).ConfigureAwait(false);
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
                item.Error = null; item.FinishedAt = null; item.ChapterQueueRunId = null;
            }
            ProjectTranslationWorkerState state = value.Workers.Single(item => item.WorkerId == workerId);
            state.Status = ProjectTranslationWorkerStatus.Ready; state.LastError = null;
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
            chapterQueues = _workerQueues.Count > 0 ? _workerQueues.Values.ToArray() :
                _chapterQueue is null ? [] : [_chapterQueue];
        }
        await PersistAsync(job => job.Status = ProjectTranslationJobStatus.PauseRequested, CancellationToken.None)
            .ConfigureAwait(false);
        await Task.WhenAll(chapterQueues.Select(queue => queue.PauseAsync())).ConfigureAwait(false);
        await running.ConfigureAwait(false);
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
            chapterQueues = _workerQueues.Count > 0 ? _workerQueues.Values.ToArray() :
                _chapterQueue is null ? [] : [_chapterQueue];
        }
        await PersistAsync(job =>
        {
            job.Status = ProjectTranslationJobStatus.Cancelling;
            foreach (ProjectTranslationItem item in job.Items.Where(item => item.Status == ProjectTranslationItemStatus.Pending))
            {
                item.Status = ProjectTranslationItemStatus.Cancelled;
                item.Error = "Đã hủy trước khi gửi request.";
                item.FinishedAt = DateTimeOffset.UtcNow;
            }
        }, CancellationToken.None).ConfigureAwait(false);
        await Task.WhenAll(chapterQueues.Select(queue => queue.CancelAsync())).ConfigureAwait(false);
        await running.ConfigureAwait(false);
    }

    public ProjectTranslationProgress GetProgress()
    {
        ProjectTranslationJob? job = CurrentJob;
        if (job is null) return new(ProjectTranslationJobStatus.Draft, 0, 0, 0, 0, 0, null, null, 0, 0, null);
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
        return job.Workers.Select(worker =>
        {
            ProjectTranslationItem? current = worker.CurrentChapterId is Guid id
                ? job.Items.SingleOrDefault(item => item.ChapterId == id) : null;
            double rate = worker.ActiveSeconds > 0 ? worker.CompletedHanCharacters / worker.ActiveSeconds * 60d : 0;
            decimal cost = worker.HourlyPrice is decimal hourly
                ? hourly * (decimal)(worker.ActiveSeconds / 3600d) : 0;
            double tokensPerSecond = worker.EvaluationSeconds > 0
                ? worker.OutputTokens / worker.EvaluationSeconds : 0;
            return new ProjectTranslationWorkerProgress(worker.WorkerId, worker.Name, worker.Status,
                current?.ChapterId, current?.Title, current?.CompletedSegments ?? 0, current?.TotalSegments ?? 0,
                rate, worker.CompletedChapters, worker.FailedChapters, cost, worker.LastError,
                tokensPerSecond, worker.InputTokens, worker.OutputTokens, worker.ErrorSegments,
                worker.TruncatedOutputCount, worker.PossibleMissingContentCount, worker.TimeoutCount);
        }).ToArray();
    }

    public ProjectTranslationAggregateMetrics GetAggregateMetrics()
    {
        ProjectTranslationJob? job = CurrentJob;
        if (job is null) return new(0, null, 0);
        ProjectTranslationWorkerProgress[] workers = GetWorkerProgress().ToArray();
        double rate = workers.Sum(worker => worker.HanCharactersPerMinute);
        double remaining = job.Items.Where(item => item.Status is ProjectTranslationItemStatus.Pending or
            ProjectTranslationItemStatus.Running or ProjectTranslationItemStatus.Paused or
            ProjectTranslationItemStatus.Interrupted).Sum(item => item.TotalSegments <= 0 ? item.SourceHanCharacters :
                item.SourceHanCharacters * Math.Max(0d, 1d - (double)item.CompletedSegments / item.TotalSegments));
        TimeSpan? eta = rate > 0 && remaining > 0 ? TimeSpan.FromMinutes(remaining / rate) : null;
        return new(rate, eta, workers.Sum(worker => worker.EstimatedCost));
    }

    private Task LaunchAsync(ProjectTranslationJob job, bool isNew, bool retryFailed, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false }) throw new InvalidOperationException("Project translation job đang chạy.");
            cancellationToken.ThrowIfCancellationRequested();
            _pauseRequested = false;
            _cancelRequested = false;
            _interruptionRequested = false;
            _runTask = RunAsync(job, isNew, retryFailed, cancellationToken);
            return _runTask;
        }
    }

    private async Task RunAsync(ProjectTranslationJob requested, bool isNew, bool retryFailed,
        CancellationToken cancellationToken)
    {
        ProjectTranslationEnvironment launchEnvironment = await LoadEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        string? launchConflict = requested.Workers.Count > 0
            ? ValidateParallelLaunch(requested, launchEnvironment)
            : ValidateEnvironment(requested, launchEnvironment);
        if (launchConflict is not null) throw new InvalidOperationException(launchConflict);
        if (!ActiveProjects.TryAdd(_session.Manifest.ProjectId, _coordinatorId))
            throw new InvalidOperationException("Project đã có coordinator khác đang ghi translation queue.");
        try
        {
            if (isNew)
            {
                ProjectTranslationJob saved = await _store.SaveNewAsync(requested, cancellationToken).ConfigureAwait(false);
                lock (_sync) _job = saved;
            }
            else
            {
                lock (_sync) _job = ProjectTranslationJobStore.Clone(requested);
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
            lock (_sync) { _chapterQueue = null; _workerQueues.Clear(); }
            Notify();
        }
    }

    private async Task RunWorkersAsync(bool retryFailed, CancellationToken cancellationToken)
    {
        ProjectTranslationJob job = CurrentJob!;
        ProjectTranslationWorkerDefinition[] definitions = ResolveWorkerDefinitions(job);
        await PersistAsync(value =>
        {
            foreach (ProjectTranslationWorkerState worker in value.Workers)
            {
                worker.Status = ProjectTranslationWorkerStatus.Running;
                worker.LastError = null;
                worker.CurrentChapterId = null;
            }
        }, cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(definitions.Select(worker => WorkerLoopAsync(worker, retryFailed, cancellationToken)))
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

    private async Task WorkerLoopAsync(ProjectTranslationWorkerDefinition definition, bool retryFailed,
        CancellationToken cancellationToken)
    {
        while (!_pauseRequested && !_cancelRequested && !_interruptionRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid? chapterId = await ClaimNextAsync(definition.WorkerId, cancellationToken).ConfigureAwait(false);
            if (chapterId is null) break;
            DateTimeOffset started = DateTimeOffset.UtcNow;
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
                    worker.CurrentChapterId = null;
                    worker.Status = ProjectTranslationWorkerStatus.Interrupted;
                    worker.LastError = error.Message;
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                bool conflict = ContainsConflict(error) || error is FileNotFoundException;
                await PersistAsync(job =>
                {
                    ProjectTranslationItem item = job.Items.Single(value => value.ChapterId == chapterId.Value);
                    item.Status = conflict ? ProjectTranslationItemStatus.NeedsReview : ProjectTranslationItemStatus.Failed;
                    item.Error = SafeWorkerError(error);
                    item.FinishedAt = DateTimeOffset.UtcNow;
                    ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == definition.WorkerId);
                    worker.CurrentChapterId = null;
                    worker.FailedChapters++;
                    worker.ActiveSeconds += Math.Max(0, (DateTimeOffset.UtcNow - started).TotalSeconds);
                    worker.LastError = item.Error;
                    if (error is IOException or UnauthorizedAccessException)
                    {
                        worker.Status = ProjectTranslationWorkerStatus.Interrupted;
                        _interruptionRequested = true;
                    }
                }, CancellationToken.None).ConfigureAwait(false);
            }
            ProjectTranslationWorkerState state = CurrentJob!.Workers.Single(worker => worker.WorkerId == definition.WorkerId);
            if (state.Status is ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted) break;
        }
        await PersistAsync(job =>
        {
            ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == definition.WorkerId);
            worker.CurrentChapterId = null;
            if (_cancelRequested) worker.Status = ProjectTranslationWorkerStatus.Cancelled;
            else if (_pauseRequested) worker.Status = ProjectTranslationWorkerStatus.Paused;
            else if (worker.Status is not (ProjectTranslationWorkerStatus.Offline or ProjectTranslationWorkerStatus.Interrupted))
                worker.Status = ProjectTranslationWorkerStatus.Completed;
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<Guid?> ClaimNextAsync(Guid workerId, CancellationToken cancellationToken)
    {
        Guid? claimed = null;
        await PersistAsync(job =>
        {
            ProjectTranslationItem? item = job.Items.OrderBy(value => value.Order).FirstOrDefault(value =>
                (value.Status is ProjectTranslationItemStatus.Pending or ProjectTranslationItemStatus.Paused or
                    ProjectTranslationItemStatus.Interrupted) &&
                (value.AssignedWorkerId is null || value.AssignedWorkerId == workerId));
            if (item is null) return;
            item.AssignedWorkerId = workerId;
            item.Status = ProjectTranslationItemStatus.Running;
            item.StartedAt ??= DateTimeOffset.UtcNow;
            item.Error = null;
            ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerId);
            worker.Status = ProjectTranslationWorkerStatus.Running;
            worker.CurrentChapterId = item.ChapterId;
            claimed = item.ChapterId;
        }, cancellationToken).ConfigureAwait(false);
        return claimed;
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
        ProjectTranslationEnvironment environment = await LoadEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        ChapterDocument chapter = (await _repository.LoadChapterAsync(_session, chapterId, cancellationToken)
            .ConfigureAwait(false)).Chapter;
        string? conflict = ValidateChapterRevision(item, chapter);
        if (conflict is not null)
        {
            await PersistAsync(job =>
            {
                ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                target.Status = ProjectTranslationItemStatus.NeedsReview;
                target.Error = conflict;
                target.FinishedAt = DateTimeOffset.UtcNow;
                if (workerDefinition is not null)
                {
                    ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId);
                    worker.CurrentChapterId = null;
                    worker.FailedChapters++;
                    worker.LastError = conflict;
                }
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }
        string? configurationConflict = ValidateConfigurationRevision(environment, workerDefinition);
        if (configurationConflict is not null)
            throw new ProjectTranslationConfigurationChangedException(configurationConflict);

        QueueMode mode = retryFailed && item.StartedAt is not null ? QueueMode.FailedSegments : QueueMode.MissingSegments;
        bool canResumeChapter = item.ChapterQueueRunId is Guid runId && chapter.Queue.RunId == runId &&
            chapter.Queue.Status is QueueStatus.Paused or QueueStatus.Interrupted or QueueStatus.Cancelled or QueueStatus.Faulted;
        if (workerDefinition is not null && !canResumeChapter)
        {
            chapter = await RunWorkerPreflightAsync(workerDefinition, chapterId, cancellationToken).ConfigureAwait(false);
            item = CurrentJob!.Items.Single(value => value.ChapterId == chapterId);
        }
        bool hasEligible = chapter.Segments.Any(segment => TranslationService.IsQueueEligible(segment,
            canResumeChapter ? chapter.Queue.Mode : mode));
        if (!hasEligible && !canResumeChapter)
        {
            bool completedByThisJob = item.ChapterQueueRunId is Guid completedRun && chapter.Queue.RunId == completedRun &&
                chapter.Queue.Status is QueueStatus.Completed or QueueStatus.CompletedWithWarnings;
            await PersistAsync(job =>
            {
                ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                target.Status = completedByThisJob ? ProjectTranslationItemStatus.Completed : ProjectTranslationItemStatus.Skipped;
                target.Error = completedByThisJob ? null : "Không có segment đủ điều kiện; UserDraft và Approval được giữ nguyên.";
                target.CompletedSegments = CompletedSegments(chapter);
                target.ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error);
                target.FinishedAt = DateTimeOffset.UtcNow;
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        RevisionGuardedChapterStore guardedStore = await RevisionGuardedChapterStore.CreateAsync(
            _session, _repository, chapterId, cancellationToken).ConfigureAwait(false);
        var service = new TranslationService(chapter, workerDefinition?.Client ?? _client, guardedStore);
        service.UseProjectResources(environment.Resources);
        var queue = new TranslationQueue(service);
        queue.ProgressChanged += (_, _) => OnChapterProgress(chapterId, service, queue);
        lock (_sync)
        {
            if (workerDefinition is null) _chapterQueue = queue;
            else _workerQueues[workerDefinition.WorkerId] = queue;
        }
        await PersistAsync(job =>
        {
            ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
            target.Status = ProjectTranslationItemStatus.Running;
            target.StartedAt ??= DateTimeOffset.UtcNow;
            target.Error = null;
            if (workerDefinition is null) job.CurrentChapterId = chapterId;
            else job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId).CurrentChapterId = chapterId;
        }, CancellationToken.None).ConfigureAwait(false);

        try
        {
            Task run = canResumeChapter
                ? queue.ResumeAsync(cancellationToken)
                : queue.StartAsync(mode, workerDefinition?.ModelOptions ?? CurrentJob!.ModelOptions,
                    CurrentJob!.PromptPreset, cancellationToken);
            Guid chapterRunId = service.GetSnapshot().Queue.RunId;
            await PersistAsync(job => job.Items.Single(value => value.ChapterId == chapterId).ChapterQueueRunId = chapterRunId,
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
                    ClearCurrent(job, workerDefinition);
                    UpdateWorkerCompletion(job, workerDefinition, target, after, workerStarted);
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            if (_cancelRequested || status == QueueStatus.Cancelled)
            {
                await PersistAsync(job =>
                {
                    UpdateItem(job, chapterId, after,
                        chapterCompleted ? ProjectTranslationItemStatus.Completed : ProjectTranslationItemStatus.Cancelled,
                        chapterCompleted ? null : "Đã hủy; các segment đã checkpoint vẫn được giữ.");
                    ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                    job.Status = ProjectTranslationJobStatus.Cancelled;
                    ClearCurrent(job, workerDefinition);
                    UpdateWorkerCompletion(job, workerDefinition, target, after, workerStarted);
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            bool failed = status is QueueStatus.CompletedWithErrors or QueueStatus.Faulted;
            bool uncertainRemote = failed && workerDefinition?.Backend.Kind == OllamaBackendKind.Remote;
            await PersistAsync(job =>
            {
                UpdateItem(job, chapterId, after, uncertainRemote ? ProjectTranslationItemStatus.NeedsReview :
                    failed ? ProjectTranslationItemStatus.Failed : ProjectTranslationItemStatus.Completed,
                    failed ? after.Queue.LastError ?? "Chapter còn segment lỗi hoặc chưa hoàn tất." : null);
                ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                ClearCurrent(job, workerDefinition);
                UpdateWorkerCompletion(job, workerDefinition, target, after, workerStarted);
                if (uncertainRemote && workerDefinition is not null)
                {
                    ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId);
                    worker.Status = ProjectTranslationWorkerStatus.Offline;
                    worker.LastError = "Remote mất kết nối hoặc trả kết quả không chắc chắn; chapter cần kiểm tra trước khi Retry.";
                }
            }, CancellationToken.None).ConfigureAwait(false);
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
                ClearCurrent(job, workerDefinition);
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
                }
                return;
            }
        }
        finally
        {
            lock (_sync)
            {
                if (workerDefinition is null && ReferenceEquals(_chapterQueue, queue)) _chapterQueue = null;
                else if (workerDefinition is not null) _workerQueues.Remove(workerDefinition.WorkerId);
            }
        }
    }

    private static string? ValidateChapterRevision(ProjectTranslationItem item, ChapterDocument chapter)
    {
        if (ProjectTranslationFingerprints.Source(chapter) != item.SourceFingerprint)
            return "Nguyên văn hoặc ranh giới segment đã đổi sau khi enqueue.";
        if (ProjectTranslationFingerprints.Protected(chapter) != item.ProtectedFingerprint)
            return "UserDraft, Approval hoặc lịch sử bảo vệ đã đổi sau khi enqueue.";
        if (item.StartedAt is null && ProjectTranslationFingerprints.Document(chapter) != item.EnqueuedDocumentFingerprint)
            return "Document revision đã đổi sau khi enqueue.";
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

    private string? ValidateConfigurationRevision(ProjectTranslationEnvironment environment,
        ProjectTranslationWorkerDefinition? workerDefinition)
    {
        ProjectTranslationJob job = CurrentJob!;
        if (environment.Resources.Revision != job.ProjectResourceRevision)
            return "Glossary, Context hoặc Character Profiles đã đổi khi hàng đợi đang chạy.";
        if (workerDefinition is null && environment.ModelOptions != job.ModelOptions)
            return "Model options đã đổi khi hàng đợi đang chạy.";
        if (environment.PromptPreset != job.PromptPreset)
            return "Prompt preset đã đổi khi hàng đợi đang chạy.";
        string? environmentConflict = workerDefinition is null ? ValidateEnvironment(job, environment) : null;
        if (environmentConflict is not null) return environmentConflict;
        return null;
    }

    private static string? ValidateEnvironment(ProjectTranslationJob job, ProjectTranslationEnvironment environment)
    {
        OllamaBackendSnapshot planned = job.Backend ?? OllamaBackendSnapshot.LocalDefault(job.ModelOptions.Model);
        OllamaBackendSnapshot current = environment.Backend ?? OllamaBackendSnapshot.LocalDefault(environment.ModelOptions.Model);
        if (planned.ProfileId != current.ProfileId || planned.Kind != current.Kind ||
            !string.Equals(planned.Endpoint, current.Endpoint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(planned.Model, current.Model, StringComparison.Ordinal))
            return "Backend/model Ollama đã đổi sau khi tạo Queue Plan. Hãy xác nhận server mới và tạo lại Queue Plan; job cũ không tự gửi request.";
        return null;
    }

    private string? ValidateParallelLaunch(ProjectTranslationJob job, ProjectTranslationEnvironment environment)
    {
        if (environment.Resources.Revision != job.ProjectResourceRevision)
            return "Glossary, Context hoặc Character Profiles đã đổi sau khi tạo Queue Plan.";
        if (environment.PromptPreset != job.PromptPreset)
            return "Prompt preset đã đổi sau khi tạo Queue Plan.";
        _ = ResolveWorkerDefinitions(job);
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
        lock (_sync)
        {
            ProjectTranslationItem? item = _job?.Items.SingleOrDefault(value => value.ChapterId == chapterId);
            if (item is null) return;
            item.TotalSegments = chapter.Segments.Count;
            item.CompletedSegments = CompletedSegments(chapter);
            item.ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error);
        }
        Notify();
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
            lock (_sync) _job = saved;
        }
        finally { _persistGate.Release(); }
        Notify();
    }

    private Task FinishAsync(ProjectTranslationJobStatus status, string? error) => PersistAsync(job =>
    {
        job.Status = status;
        job.LastError = error;
        job.CurrentChapterId = null;
    }, CancellationToken.None);

    private static void UpdateItem(ProjectTranslationJob job, Guid chapterId, ChapterDocument chapter,
        ProjectTranslationItemStatus status, string? error)
    {
        ProjectTranslationItem item = job.Items.Single(value => value.ChapterId == chapterId);
        item.Status = status;
        item.TotalSegments = chapter.Segments.Count;
        item.CompletedSegments = CompletedSegments(chapter);
        item.ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error);
        item.Error = error;
        if (status is not (ProjectTranslationItemStatus.Paused or ProjectTranslationItemStatus.Interrupted))
            item.FinishedAt = DateTimeOffset.UtcNow;
    }

    private static void ClearCurrent(ProjectTranslationJob job, ProjectTranslationWorkerDefinition? workerDefinition)
    {
        if (workerDefinition is null) job.CurrentChapterId = null;
        else job.Workers.Single(worker => worker.WorkerId == workerDefinition.WorkerId).CurrentChapterId = null;
    }

    private static void UpdateWorkerCompletion(ProjectTranslationJob job,
        ProjectTranslationWorkerDefinition? workerDefinition, ProjectTranslationItem item,
        ChapterDocument chapter, DateTimeOffset started)
    {
        if (workerDefinition is null) return;
        ProjectTranslationWorkerState worker = job.Workers.Single(value => value.WorkerId == workerDefinition.WorkerId);
        worker.ActiveSeconds += Math.Max(0, (DateTimeOffset.UtcNow - started).TotalSeconds);
        if (item.Status == ProjectTranslationItemStatus.Completed)
        {
            worker.CompletedChapters++;
            worker.CompletedHanCharacters += item.SourceHanCharacters;
        }
        else if (item.Status is ProjectTranslationItemStatus.Failed or ProjectTranslationItemStatus.NeedsReview)
            worker.FailedChapters++;
        if (!item.WorkerMetricsRecorded && item.Status is ProjectTranslationItemStatus.Completed or
            ProjectTranslationItemStatus.Failed or ProjectTranslationItemStatus.NeedsReview or
            ProjectTranslationItemStatus.Cancelled)
        {
            TranslationAttempt[] attempts = chapter.Segments.SelectMany(segment => segment.Attempts)
                .Where(attempt => attempt.StartedAt >= (item.StartedAt ?? started)).ToArray();
            worker.InputTokens += attempts.Sum(attempt => (long)(attempt.Metrics?.InputTokens ?? 0));
            worker.OutputTokens += attempts.Sum(attempt => (long)(attempt.Metrics?.OutputTokens ?? 0));
            worker.EvaluationSeconds += attempts.Sum(attempt =>
                Math.Max(0, (attempt.Metrics?.EvaluationDurationNanoseconds ?? 0) / 1_000_000_000d));
            worker.ErrorSegments += item.ErrorSegments;
            worker.TruncatedOutputCount += attempts.Sum(attempt =>
                attempt.Issues.Count(issue => issue.Code == IssueCode.TruncatedOutput));
            worker.PossibleMissingContentCount += attempts.Sum(attempt =>
                attempt.Issues.Count(issue => issue.Code == IssueCode.PossibleMissingContent));
            worker.TimeoutCount += attempts.Count(attempt => attempt.Error?.Contains("timeout",
                StringComparison.OrdinalIgnoreCase) == true || attempt.Error?.Contains("timed out",
                StringComparison.OrdinalIgnoreCase) == true);
            item.WorkerMetricsRecorded = true;
        }
        worker.LastError = item.Error;
    }

    private static int CompletedSegments(ChapterDocument chapter) => chapter.Segments.Count(segment =>
        segment.Status == SegmentStatus.Approved || segment.HasCompletedTranslation);

    private static int CountHan(string text) => text.Count(character => character is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff');

    private static string SafeWorkerError(Exception error) => error switch
    {
        IOException => "Không đọc hoặc lưu được checkpoint của chapter.",
        UnauthorizedAccessException => "Không có quyền ghi checkpoint của chapter.",
        OllamaRequestException request when request.Kind == OllamaErrorKind.Connection => "GPU mất kết nối; kết quả request hiện tại chưa xác định.",
        _ => error.Message
    };

    private static bool ContainsConflict(Exception exception) => exception is ProjectTranslationConflictException ||
        exception.InnerException is not null && ContainsConflict(exception.InnerException) ||
        exception is AggregateException aggregate && aggregate.InnerExceptions.Any(ContainsConflict);

    private sealed class ProjectTranslationConfigurationChangedException(string message) : InvalidOperationException(message);

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

    public static string Document(ChapterDocument chapter) => Hash(chapter);

    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, DocumentJson.Options))));
}
