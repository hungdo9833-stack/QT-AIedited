using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed class ProjectTranslationJobStore(ProjectSession session)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = Gates.GetOrAdd(session.Resolve("Queue/project-translation-job.json"), _ => new(1, 1));
    public string PathName { get; } = session.Resolve("Queue/project-translation-job.json");
    public RecoveryInfo? LastRecovery { get; private set; }

    public async Task<ProjectTranslationJob?> LoadAsync(CancellationToken cancellationToken = default)
    {
        var read = await RecoveryFiles.ReadAsync(PathName, Decode, () => (ProjectTranslationJob)null!, cancellationToken)
            .ConfigureAwait(false);
        LastRecovery = read.Info;
        if (read.Value is null) return null;
        ProjectTranslationJob job = Clone(read.Value);
        if (job.Status is ProjectTranslationJobStatus.Running or ProjectTranslationJobStatus.PauseRequested or ProjectTranslationJobStatus.Cancelling)
        {
            job.Status = ProjectTranslationJobStatus.Interrupted;
            job.LastError = "Hàng đợi bị gián đoạn. Chỉ tiếp tục sau khi người dùng bấm Resume.";
            if (job.CurrentChapterId is Guid current)
            {
                ProjectTranslationItem? item = job.Items.SingleOrDefault(value => value.ChapterId == current);
                if (item?.Status == ProjectTranslationItemStatus.Running)
                {
                    item.Status = ProjectTranslationItemStatus.Interrupted;
                    item.Error = job.LastError;
                }
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
        var read = await RecoveryFiles.ReadAsync(PathName, Decode, () => (ProjectTranslationJob)null!, cancellationToken)
            .ConfigureAwait(false);
        LastRecovery = read.Info;
        return read.Value;
    }

    private async Task<ProjectTranslationJob> WriteAsync(ProjectTranslationJob job, CancellationToken cancellationToken)
    {
        JsonElement content = JsonSerializer.SerializeToElement(job, DocumentJson.Options);
        var envelope = new Envelope { Content = content, Sha256 = Hash(content) };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        LastRecovery = await RecoveryFiles.WriteAsync(PathName, bytes, Decode, cancellationToken).ConfigureAwait(false);
        return Clone(job);
    }

    private ProjectTranslationJob Decode(byte[] bytes)
    {
        Envelope? envelope = JsonSerializer.Deserialize<Envelope>(bytes, DocumentJson.Options);
        if (envelope is null || envelope.SchemaVersion != 1 || envelope.Content.ValueKind != JsonValueKind.Object ||
            !string.Equals(envelope.Sha256, Hash(envelope.Content), StringComparison.Ordinal))
            throw new InvalidDataException("Project translation job sai checksum hoặc định dạng.");
        ProjectTranslationJob job = envelope.Content.Deserialize<ProjectTranslationJob>(DocumentJson.Options)
            ?? throw new InvalidDataException("Project translation job trống.");
        job.Validate();
        if (job.ProjectId != session.Manifest.ProjectId)
            throw new InvalidDataException("Translation job thuộc Project khác.");
        return job;
    }

    private static string Hash(JsonElement value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, DocumentJson.Options))));
    internal static ProjectTranslationJob Clone(ProjectTranslationJob job) =>
        JsonSerializer.Deserialize<ProjectTranslationJob>(JsonSerializer.Serialize(job, DocumentJson.Options), DocumentJson.Options)!;

    private sealed class Envelope
    {
        public int SchemaVersion { get; set; } = 1;
        public string Sha256 { get; set; } = "";
        public JsonElement Content { get; set; }
    }
}
