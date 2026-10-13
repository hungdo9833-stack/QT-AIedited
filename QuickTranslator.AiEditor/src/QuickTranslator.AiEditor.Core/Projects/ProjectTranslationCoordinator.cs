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
    private readonly ProjectTranslationJobStore _store;
    private readonly Func<CancellationToken, Task<ProjectTranslationEnvironment>> _environmentLoader;
    private readonly SemaphoreSlim _persistGate = new(1, 1);
    private readonly object _sync = new();
    private readonly Guid _coordinatorId = Guid.NewGuid();
    private ProjectTranslationJob? _job;
    private TranslationQueue? _chapterQueue;
    private Task? _runTask;
    private bool _pauseRequested;
    private bool _cancelRequested;

    public ProjectTranslationCoordinator(ProjectSession session, ProjectRepository repository, IOllamaClient client,
        Func<CancellationToken, Task<ProjectTranslationEnvironment>> environmentLoader,
        ProjectTranslationJobStore? store = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _client = client ?? throw new ArgumentNullException(nameof(client));
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
                ErrorSegments = chapter.Segments.Count(segment => segment.Status == SegmentStatus.Error)
            });
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var job = new ProjectTranslationJob
        {
            ProjectId = _session.Manifest.ProjectId,
            Status = ProjectTranslationJobStatus.Draft,
            ModelOptions = environment.ModelOptions with { },
            Backend = (environment.Backend ?? OllamaBackendSnapshot.LocalDefault(environment.ModelOptions.Model)) with { },
            PromptPreset = environment.PromptPreset with { },
            ProjectResourceRevision = environment.Resources.Revision,
            CreatedAt = now,
            UpdatedAt = now,
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

    public async Task PauseAsync()
    {
        Task? running;
        TranslationQueue? chapterQueue;
        lock (_sync)
        {
            running = _runTask;
            if (running is null || running.IsCompleted) return;
            _pauseRequested = true;
            chapterQueue = _chapterQueue;
        }
        await PersistAsync(job => job.Status = ProjectTranslationJobStatus.PauseRequested, CancellationToken.None)
            .ConfigureAwait(false);
        if (chapterQueue is not null) await chapterQueue.PauseAsync().ConfigureAwait(false);
        await running.ConfigureAwait(false);
    }

    public async Task CancelPendingAsync()
    {
        Task? running;
        TranslationQueue? chapterQueue;
        lock (_sync)
        {
            running = _runTask;
            if (running is null || running.IsCompleted) return;
            _cancelRequested = true;
            chapterQueue = _chapterQueue;
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
        if (chapterQueue is not null) await chapterQueue.CancelAsync().ConfigureAwait(false);
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

    private Task LaunchAsync(ProjectTranslationJob job, bool isNew, bool retryFailed, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false }) throw new InvalidOperationException("Project translation job đang chạy.");
            cancellationToken.ThrowIfCancellationRequested();
            _pauseRequested = false;
            _cancelRequested = false;
            _runTask = RunAsync(job, isNew, retryFailed, cancellationToken);
            return _runTask;
        }
    }

    private async Task RunAsync(ProjectTranslationJob requested, bool isNew, bool retryFailed,
        CancellationToken cancellationToken)
    {
        ProjectTranslationEnvironment launchEnvironment = await LoadEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        string? launchConflict = ValidateEnvironment(requested, launchEnvironment);
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
            lock (_sync) _chapterQueue = null;
            Notify();
        }
    }

    private async Task RunChapterAsync(Guid chapterId, bool retryFailed, CancellationToken cancellationToken)
    {
        ProjectTranslationItem item = CurrentJob!.Items.Single(value => value.ChapterId == chapterId);
        ProjectTranslationEnvironment environment = await LoadEnvironmentAsync(cancellationToken).ConfigureAwait(false);
        ChapterDocument chapter = (await _repository.LoadChapterAsync(_session, chapterId, cancellationToken)
            .ConfigureAwait(false)).Chapter;
        string? conflict = ValidateRevision(item, chapter, environment);
        if (conflict is not null)
        {
            await PersistAsync(job =>
            {
                ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
                target.Status = ProjectTranslationItemStatus.NeedsReview;
                target.Error = conflict;
                target.FinishedAt = DateTimeOffset.UtcNow;
            }, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        QueueMode mode = retryFailed && item.StartedAt is not null ? QueueMode.FailedSegments : QueueMode.MissingSegments;
        bool canResumeChapter = item.ChapterQueueRunId is Guid runId && chapter.Queue.RunId == runId &&
            chapter.Queue.Status is QueueStatus.Paused or QueueStatus.Interrupted or QueueStatus.Cancelled or QueueStatus.Faulted;
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
        var service = new TranslationService(chapter, _client, guardedStore);
        service.UseProjectResources(environment.Resources);
        var queue = new TranslationQueue(service);
        queue.ProgressChanged += (_, _) => OnChapterProgress(chapterId, service, queue);
        lock (_sync) _chapterQueue = queue;
        await PersistAsync(job =>
        {
            ProjectTranslationItem target = job.Items.Single(value => value.ChapterId == chapterId);
            target.Status = ProjectTranslationItemStatus.Running;
            target.StartedAt ??= DateTimeOffset.UtcNow;
            target.Error = null;
            job.CurrentChapterId = chapterId;
        }, CancellationToken.None).ConfigureAwait(false);

        try
        {
            Task run = canResumeChapter
                ? queue.ResumeAsync(cancellationToken)
                : queue.StartAsync(mode, CurrentJob!.ModelOptions, CurrentJob!.PromptPreset, cancellationToken);
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
                    job.Status = ProjectTranslationJobStatus.Paused;
                    job.CurrentChapterId = null;
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
                    job.Status = ProjectTranslationJobStatus.Cancelled;
                    job.CurrentChapterId = null;
                }, CancellationToken.None).ConfigureAwait(false);
                return;
            }
            bool failed = status is QueueStatus.CompletedWithErrors or QueueStatus.Faulted;
            await PersistAsync(job =>
            {
                UpdateItem(job, chapterId, after, failed ? ProjectTranslationItemStatus.Failed : ProjectTranslationItemStatus.Completed,
                    failed ? after.Queue.LastError ?? "Chapter còn segment lỗi hoặc chưa hoàn tất." : null);
                job.CurrentChapterId = null;
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
                job.CurrentChapterId = null;
            }, CancellationToken.None).ConfigureAwait(false);
            if (ex is IOException or UnauthorizedAccessException)
            {
                await FinishAsync(ProjectTranslationJobStatus.Interrupted,
                    "Đã dừng sau lỗi lưu/checkpoint: " + ex.Message).ConfigureAwait(false);
                return;
            }
        }
        finally
        {
            lock (_sync) if (ReferenceEquals(_chapterQueue, queue)) _chapterQueue = null;
        }
    }

    private string? ValidateRevision(ProjectTranslationItem item, ChapterDocument chapter,
        ProjectTranslationEnvironment environment)
    {
        if (ProjectTranslationFingerprints.Source(chapter) != item.SourceFingerprint)
            return "Nguyên văn hoặc ranh giới segment đã đổi sau khi enqueue.";
        if (ProjectTranslationFingerprints.Protected(chapter) != item.ProtectedFingerprint)
            return "UserDraft, Approval hoặc lịch sử bảo vệ đã đổi sau khi enqueue.";
        if (item.StartedAt is null && ProjectTranslationFingerprints.Document(chapter) != item.EnqueuedDocumentFingerprint)
            return "Document revision đã đổi sau khi enqueue.";
        ProjectTranslationJob job = CurrentJob!;
        if (environment.Resources.Revision != job.ProjectResourceRevision)
            return "Glossary, Context hoặc Character Profiles đã đổi sau khi enqueue.";
        if (environment.ModelOptions != job.ModelOptions)
            return "Model options đã đổi sau khi enqueue.";
        if (environment.PromptPreset != job.PromptPreset)
            return "Prompt preset đã đổi sau khi enqueue.";
        string? environmentConflict = ValidateEnvironment(job, environment);
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

    private static int CompletedSegments(ChapterDocument chapter) => chapter.Segments.Count(segment =>
        segment.Status == SegmentStatus.Approved || segment.HasCompletedTranslation);

    private static bool ContainsConflict(Exception exception) => exception is ProjectTranslationConflictException ||
        exception.InnerException is not null && ContainsConflict(exception.InnerException) ||
        exception is AggregateException aggregate && aggregate.InnerExceptions.Any(ContainsConflict);

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
