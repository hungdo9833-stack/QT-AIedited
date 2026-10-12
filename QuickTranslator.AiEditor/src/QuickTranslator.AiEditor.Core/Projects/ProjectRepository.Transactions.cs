using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed partial class ProjectRepository
{
    internal Action<string>? FaultPoint { get; set; }
    private ProjectTransactionJournal Transactions => new(point => FaultPoint?.Invoke(point));

    public async Task<ProjectChapterEntry> AddChapterAsync(ProjectSession session, ChapterDocument chapter, int number,
        string? chineseTitle = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(chapter);
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
            if (session.Manifest.Chapters.Any(x => x.DeletedAt is null && x.Number == number || x.ChapterId == chapter.Id))
                throw new InvalidOperationException("Chương đã có ID hoặc số chương trong manifest.");
            string relative = $"Chapters/{chapter.Id:D}.qtaie.json";
            await EnsureDestinationEmptyAsync(session.Resolve(relative), cancellationToken).ConfigureAwait(false);
            var saved = PrepareRegisteredChapter(session, chapter, number, chineseTitle);
            var after = Clone(session.Manifest);
            var entry = ToEntry(saved, number); after.Chapters.Add(entry); after.CurrentChapterId = saved.Id;
            NormalizeManifest(after);
            await Transactions.ExecuteAsync(session, "AddChapter", after,
                [new(relative, ChapterFileStore.SerializeDocument(saved))], cancellationToken).ConfigureAwait(false);
            return session.Manifest.Chapters.Single(x => x.ChapterId == saved.Id);
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("Không đủ quyền hoặc file bị khóa; thao tác thêm chương chưa hoàn tất.", ex); }
        finally { session.OperationGate.Release(); }
    }

    public Task SoftDeleteChapterAsync(ProjectSession session, Guid chapterId, CancellationToken cancellationToken = default) =>
        MoveChapterAsync(session, chapterId, restore: false, cancellationToken);

    public Task RestoreDeletedChapterAsync(ProjectSession session, Guid chapterId, CancellationToken cancellationToken = default) =>
        MoveChapterAsync(session, chapterId, restore: true, cancellationToken);

    private async Task MoveChapterAsync(ProjectSession session, Guid chapterId, bool restore, CancellationToken token)
    {
        await session.OperationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var after = Clone(session.Manifest);
            var entry = after.Chapters.SingleOrDefault(x => x.ChapterId == chapterId && (x.DeletedAt is not null) == restore)
                ?? throw new FileNotFoundException("Không tìm thấy chương trong trạng thái cần chuyển.");
            if (restore && after.Chapters.Any(x => x.DeletedAt is null && x.Number == entry.Number))
                throw new InvalidOperationException("Số chương đã được dùng; không thể phục hồi.");
            string source = session.Resolve(entry.RelativePath);
            string relative = restore ? $"Chapters/{chapterId:D}.qtaie.json"
                : $"History/Deleted/{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{chapterId:D}.qtaie.json";
            string destination = session.Resolve(relative);
            await EnsureDestinationEmptyAsync(destination, token).ConfigureAwait(false);
            var primary = await ChapterFileStore.ReadVerifiedFileAsync(source, token).ConfigureAwait(false);
            if (primary.Id != chapterId || primary.ProjectId != session.Manifest.ProjectId)
                throw new InvalidDataException("File chương không thuộc liên kết manifest này.");
            byte[] bytes = await File.ReadAllBytesAsync(source, token).ConfigureAwait(false);
            byte[]? backup = await ProjectTransactionJournal.ReadOptionalAsync(source + ".bak", token).ConfigureAwait(false);
            if (backup is not null)
            {
                var verified = await ChapterFileStore.ReadVerifiedFileAsync(source + ".bak", token).ConfigureAwait(false);
                if (verified.Id != chapterId || verified.ProjectId != session.Manifest.ProjectId)
                    throw new InvalidDataException("Backup chương có ID khác; giữ nguyên để đối chiếu.");
            }
            var changes = new List<PlannedProjectFile> { new(entry.RelativePath, null), new(relative, bytes) };
            if (backup is not null) { changes.Add(new(entry.RelativePath + ".bak", null)); changes.Add(new(relative + ".bak", backup)); }
            entry.RelativePath = relative; entry.DeletedAt = restore ? null : DateTimeOffset.UtcNow;
            if (restore) after.CurrentChapterId = chapterId;
            else if (after.CurrentChapterId == chapterId) after.CurrentChapterId = after.Chapters.FirstOrDefault(x => x.DeletedAt is null)?.ChapterId;
            NormalizeManifest(after);
            await Transactions.ExecuteAsync(session, restore ? "RestoreDeletedChapter" : "SoftDeleteChapter", after, changes, token).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("Không đủ quyền hoặc file bị khóa; thao tác chuyển chương chưa hoàn tất.", ex); }
        finally { session.OperationGate.Release(); }
    }

    public async Task<ProjectRecoveryReport> InspectRecoveryAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session.RecoveryReport = await ProjectTransactionJournal.InspectAsync(session, cancellationToken).ConfigureAwait(false);
            return session.RecoveryReport;
        }
        finally { session.OperationGate.Release(); }
    }

    public async Task RestoreManifestBackupAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            session.ManifestRecovery = await RecoveryFiles.RestoreBackupAsync(session.ManifestRecovery ?? throw new InvalidOperationException("Chưa có preview manifest."),
                bytes => { var value = DecodeManifest(bytes); if (value.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("ProjectId manifest thay đổi."); return value; }, cancellationToken).ConfigureAwait(false);
            session.Manifest = await ReadManifestAsync(session.ManifestPath, cancellationToken).ConfigureAwait(false);
        }
        finally { session.OperationGate.Release(); }
    }

    public async Task RecoverTransactionAsync(ProjectSession session, Guid transactionId, ProjectRecoveryDecision decision,
        CancellationToken cancellationToken = default)
    {
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await Transactions.RecoverAsync(session, transactionId, decision, cancellationToken).ConfigureAwait(false); }
        finally { session.OperationGate.Release(); }
    }

    internal static ChapterDocument PrepareRegisteredChapter(ProjectSession session, ChapterDocument chapter, int number, string? chineseTitle)
    {
        var saved = DocumentJson.Clone(chapter);
        saved.ProjectId = session.Manifest.ProjectId; saved.ChapterNumber = number;
        saved.ChineseTitle = chineseTitle?.Trim() ?? saved.ChineseTitle;
        saved.CharacterProject.Id = session.Manifest.ProjectId;
        DocumentJson.Validate(saved); return saved;
    }
    private static void NormalizeManifest(ProjectManifest manifest)
    {
        manifest.UpdatedAt = DateTimeOffset.UtcNow;
        manifest.ChapterCount = manifest.Chapters.Count(x => x.DeletedAt is null);
        manifest.DeletedChapterCount = manifest.Chapters.Count(x => x.DeletedAt is not null);
        manifest.Validate();
    }
    internal static async Task EnsureDestinationEmptyAsync(string path, CancellationToken token)
    {
        foreach (string candidate in new[] { path, path + ".bak" })
            if (await ProjectTransactionJournal.ReadOptionalAsync(candidate, token).ConfigureAwait(false) is not null || Directory.Exists(candidate))
                throw new IOException($"Đích {Path.GetFileName(candidate)} đã tồn tại, có thể là orphan; không tự ghi đè.");
    }
}
