using System.Collections.Concurrent;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed class ProjectTranslationJobStore(ProjectSession session)
{
    internal const string RelativePath = "Queue/project-translation-job.json";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = Gates.GetOrAdd(session.Resolve(RelativePath), _ => new(1, 1));
    public string PathName { get; } = session.Resolve(RelativePath);
    public async Task<ProjectTranslationJob?> LoadAsync(CancellationToken cancellationToken = default)
    {
        ProjectTranslationJob? read = await SimpleFileStorage.ReadOptionalAsync(PathName, Decode,
            () => (ProjectTranslationJob)null!, cancellationToken).ConfigureAwait(false);
        if (read is null) return null;
        ProjectTranslationJob job = Clone(read);
        if (job.Status is ProjectTranslationJobStatus.Running or ProjectTranslationJobStatus.PauseRequested or ProjectTranslationJobStatus.Cancelling)
        {
            job.Status = ProjectTranslationJobStatus.Interrupted;
            job.LastError = "Hàng đợi bị gián đoạn. Chỉ tiếp tục sau khi người dùng bấm Resume.";
            Guid[] runningChapterIds = job.Items.Where(item => item.Status == ProjectTranslationItemStatus.Running)
                .Select(item => item.ChapterId).ToArray();
            if (job.CurrentChapterId is Guid current && !runningChapterIds.Contains(current))
                runningChapterIds = [.. runningChapterIds, current];
            foreach (Guid currentChapterId in runningChapterIds)
            {
                ProjectTranslationItem? item = job.Items.SingleOrDefault(value => value.ChapterId == currentChapterId);
                if (item?.Status == ProjectTranslationItemStatus.Running)
                {
                    item.Status = ProjectTranslationItemStatus.Interrupted;
                    item.Error = job.LastError;
                }
            }
            foreach (ProjectTranslationWorkerState worker in job.Workers)
            {
                if (worker.Status is ProjectTranslationWorkerStatus.Running or ProjectTranslationWorkerStatus.Pausing)
                    worker.Status = ProjectTranslationWorkerStatus.Interrupted;
                worker.CurrentChapterId = null;
                worker.LastError = job.LastError;
            }
            job.CurrentChapterId = null;
            job = await SaveCoreAsync(job, cancellationToken).ConfigureAwait(false);
        }
        return job;
    }

    public async Task<ProjectTranslationJob> SaveNewAsync(ProjectTranslationJob draft,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectTranslationJob? current = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (current is not null && current.Status is not (ProjectTranslationJobStatus.Completed or
                ProjectTranslationJobStatus.CompletedWithErrors or ProjectTranslationJobStatus.Cancelled))
                throw new InvalidOperationException("Project đã có translation job chưa kết thúc; không tạo job trùng.");
            var saved = Clone(draft);
            saved.Status = ProjectTranslationJobStatus.Ready;
            saved.Revision = 0;
            saved.UpdatedAt = DateTimeOffset.UtcNow;
            saved.Validate();
            return await WriteAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<ProjectTranslationJob> SaveAsync(ProjectTranslationJob proposed,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await SaveCoreAsync(proposed, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task<ProjectTranslationJob> SaveCoreAsync(ProjectTranslationJob proposed,
        CancellationToken cancellationToken)
    {
        ProjectTranslationJob? current = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (current is null || current.JobId != proposed.JobId || current.Revision != proposed.Revision)
            throw new InvalidOperationException("Translation job đã thay đổi trên đĩa; hãy mở lại trạng thái mới nhất.");
        var saved = Clone(proposed);
        saved.Revision = checked(current.Revision + 1);
        saved.UpdatedAt = DateTimeOffset.UtcNow;
        saved.Validate();
        return await WriteAsync(saved, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectTranslationJob?> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        return await SimpleFileStorage.ReadOptionalAsync(PathName, Decode,
            () => (ProjectTranslationJob)null!, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<ProjectTranslationJob> PrepareNextAsync(ProjectTranslationJob proposed,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectTranslationJob? current = await ReadCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (current is null || current.JobId != proposed.JobId || current.Revision != proposed.Revision)
                throw new InvalidOperationException("Translation job đã thay đổi trên đĩa; preflight phải chạy lại.");
            ProjectTranslationJob saved = Clone(proposed);
            saved.Revision = checked(current.Revision + 1);
            saved.UpdatedAt = DateTimeOffset.UtcNow;
            saved.Validate();
            return saved;
        }
        finally { _gate.Release(); }
    }

    private async Task<ProjectTranslationJob> WriteAsync(ProjectTranslationJob job, CancellationToken cancellationToken)
    {
        byte[] bytes = Serialize(job);
        await SimpleFileStorage.WriteAsync(PathName, bytes, Decode, cancellationToken).ConfigureAwait(false);
        return Clone(job);
    }

    private ProjectTranslationJob Decode(byte[] bytes)
    {
        ProjectTranslationJob job = JsonFileCodec.DecodeDirect<ProjectTranslationJob>(
            bytes, value => value.Validate(), "schemaVersion", "jobId", "projectId", "items");
        job.Validate();
        if (job.ProjectId != session.Manifest.ProjectId)
            throw new InvalidDataException("Translation job thuộc Project khác.");
        return job;
    }

    internal static byte[] Serialize(ProjectTranslationJob job)
    {
        job.Validate();
        return JsonSerializer.SerializeToUtf8Bytes(job,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
    }
    internal static ProjectTranslationJob Clone(ProjectTranslationJob job) =>
        JsonSerializer.Deserialize<ProjectTranslationJob>(JsonSerializer.Serialize(job, DocumentJson.Options), DocumentJson.Options)!;

}
