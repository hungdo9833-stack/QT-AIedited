using System.Security.Cryptography;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed partial class ProjectRepository
{
    public async Task<string> GetChapterStorageRevisionAsync(ProjectSession session, Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapterId && x.DeletedAt is null)
                ?? throw new FileNotFoundException("Không tìm thấy chương active trong Project.");
            return Hash(await File.ReadAllBytesAsync(session.Resolve(entry.RelativePath), cancellationToken).ConfigureAwait(false));
        }
        finally { session.OperationGate.Release(); }
    }

    public async Task<string> UpdateChapterIfStorageRevisionAsync(ProjectSession session, ChapterDocument chapter,
        string expectedStorageRevision, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedStorageRevision);
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.RecoveryReport.RequiresDecision)
                throw new InvalidOperationException("Còn transaction cần phục hồi; không ghi checkpoint hàng đợi.");
            var entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapter.Id && x.DeletedAt is null)
                ?? throw new InvalidOperationException("Chương không còn active trong Project.");
            if (chapter.ProjectId != session.Manifest.ProjectId)
                throw new InvalidOperationException("Checkpoint không thuộc Project đang chạy.");
            string relative = entry.RelativePath;
            byte[] before = await File.ReadAllBytesAsync(session.Resolve(relative), cancellationToken).ConfigureAwait(false);
            if (!string.Equals(Hash(before), expectedStorageRevision, StringComparison.Ordinal))
                throw new ProjectTranslationConflictException(chapter.Id,
                    "Chapter đã thay đổi trên đĩa sau khi coordinator nạp; kết quả AI không được ghi đè.");

            chapter.ProjectId = session.Manifest.ProjectId;
            chapter.ChapterNumber = entry.Number;
            chapter.CharacterProject.Id = session.Manifest.ProjectId;
            byte[] afterBytes = ChapterFileStore.SerializeDocument(chapter);
            var after = Clone(session.Manifest);
            CopySummary(after.Chapters.Single(x => x.ChapterId == chapter.Id), chapter);
            after.CurrentChapterId = chapter.Id;
            NormalizeManifest(after);
            await Transactions.ExecuteAsync(session, "ProjectTranslationCheckpoint", after,
                [new(relative + ".bak", before), new(relative, afterBytes)], cancellationToken).ConfigureAwait(false);
            PublishManifestCommitted(session, "ProjectTranslationCheckpoint");
            return Hash(afterBytes);
        }
        finally { session.OperationGate.Release(); }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

public sealed class ProjectTranslationConflictException(Guid chapterId, string message) : IOException(message)
{
    public Guid ChapterId { get; } = chapterId;
}

internal sealed class RevisionGuardedChapterStore : IChapterStore
{
    private readonly ProjectSession _session;
    private readonly ProjectRepository _repository;
    private readonly Guid _chapterId;
    private string _storageRevision;

    private RevisionGuardedChapterStore(ProjectSession session, ProjectRepository repository, Guid chapterId,
        string storageRevision)
    {
        _session = session;
        _repository = repository;
        _chapterId = chapterId;
        _storageRevision = storageRevision;
    }

    public static async Task<RevisionGuardedChapterStore> CreateAsync(ProjectSession session,
        ProjectRepository repository, Guid chapterId, CancellationToken cancellationToken = default) =>
        new(session, repository, chapterId,
            await repository.GetChapterStorageRevisionAsync(session, chapterId, cancellationToken).ConfigureAwait(false));

    public string GetPath(Guid chapterId) => _session.ChapterStore.GetPath(chapterId);

    public Task<ChapterLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        _session.ChapterStore.LoadAsync(path, cancellationToken);

    public async Task SaveAsync(ChapterDocument chapter, CancellationToken cancellationToken = default)
    {
        if (chapter.Id != _chapterId) throw new InvalidOperationException("Store checkpoint không thuộc chapter này.");
        _storageRevision = await _repository.UpdateChapterIfStorageRevisionAsync(
            _session, chapter, _storageRevision, cancellationToken).ConfigureAwait(false);
    }
}
