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
            PublishManifestCommitted(session, "AddChapter");
            return session.Manifest.Chapters.Single(x => x.ChapterId == saved.Id);
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("Không đủ quyền hoặc file bị khóa; thao tác thêm chương chưa hoàn tất.", ex); }
        finally { session.OperationGate.Release(); }
    }

    public Task SoftDeleteChapterAsync(ProjectSession session, Guid chapterId, CancellationToken cancellationToken = default) =>
        SoftDeleteChaptersAsync(session, [chapterId], cancellationToken);

    public Task RestoreDeletedChapterAsync(ProjectSession session, Guid chapterId, CancellationToken cancellationToken = default) =>
        RestoreDeletedChaptersAsync(session, [chapterId], cancellationToken);

    public Task SoftDeleteChaptersAsync(ProjectSession session, IReadOnlyCollection<Guid> chapterIds,
        CancellationToken cancellationToken = default) => MoveChaptersAsync(session, chapterIds, restore: false, cancellationToken);

    public Task RestoreDeletedChaptersAsync(ProjectSession session, IReadOnlyCollection<Guid> chapterIds,
        CancellationToken cancellationToken = default) => MoveChaptersAsync(session, chapterIds, restore: true, cancellationToken);

    private async Task MoveChaptersAsync(ProjectSession session, IReadOnlyCollection<Guid> chapterIds, bool restore,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(chapterIds);
        Guid[] ids = chapterIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        if (ids.Any(id => id == Guid.Empty)) throw new ArgumentException("ChapterId không hợp lệ.", nameof(chapterIds));
        await session.OperationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var after = Clone(session.Manifest);
            ProjectChapterEntry[] entries = ids.Select(id => after.Chapters.SingleOrDefault(x =>
                    x.ChapterId == id && (x.DeletedAt is not null) == restore)
                ?? throw new FileNotFoundException($"Không tìm thấy chương {id:D} trong trạng thái cần chuyển.")).ToArray();
            if (restore)
            {
                int[] duplicateSelected = entries.GroupBy(entry => entry.Number).Where(group => group.Count() > 1)
                    .Select(group => group.Key).ToArray();
                int[] activeCollisions = after.Chapters.Where(entry => entry.DeletedAt is null)
                    .Select(entry => entry.Number).Intersect(entries.Select(entry => entry.Number)).ToArray();
                if (duplicateSelected.Length > 0 || activeCollisions.Length > 0)
                    throw new InvalidOperationException("Số chương đã được dùng; không thể phục hồi: " +
                        string.Join(", ", duplicateSelected.Concat(activeCollisions).Distinct().Order()));
            }
            DateTimeOffset movedAt = DateTimeOffset.UtcNow;
            var changes = new List<PlannedProjectFile>(entries.Length * 4);
            foreach (ProjectChapterEntry entry in entries)
            {
                token.ThrowIfCancellationRequested();
                string source = session.Resolve(entry.RelativePath);
                string relative = restore ? $"Chapters/{entry.ChapterId:D}.qtaie.json"
                    : $"History/Deleted/{movedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{entry.ChapterId:D}.qtaie.json";
                await EnsureDestinationEmptyAsync(session.Resolve(relative), token).ConfigureAwait(false);
                ChapterDocument primary = await ChapterFileStore.ReadVerifiedFileAsync(source, token).ConfigureAwait(false);
                if (primary.Id != entry.ChapterId || primary.ProjectId != session.Manifest.ProjectId)
                    throw new InvalidDataException("File chương không thuộc liên kết manifest này.");
                byte[] bytes = await File.ReadAllBytesAsync(source, token).ConfigureAwait(false);
                byte[]? backup = await ProjectTransactionJournal.ReadOptionalAsync(source + ".bak", token).ConfigureAwait(false);
                if (backup is not null)
                {
                    ChapterDocument verified = await ChapterFileStore.ReadVerifiedFileAsync(source + ".bak", token).ConfigureAwait(false);
                    if (verified.Id != entry.ChapterId || verified.ProjectId != session.Manifest.ProjectId)
                        throw new InvalidDataException("Backup chương có ID khác; giữ nguyên để đối chiếu.");
                }
                changes.Add(new(entry.RelativePath, null));
                changes.Add(new(relative, bytes));
                if (backup is not null)
                {
                    changes.Add(new(entry.RelativePath + ".bak", null));
                    changes.Add(new(relative + ".bak", backup));
                }
                entry.RelativePath = relative;
                entry.DeletedAt = restore ? null : movedAt;
            }
            if (restore) after.CurrentChapterId = entries[^1].ChapterId;
            else if (after.CurrentChapterId is Guid current && ids.Contains(current))
                after.CurrentChapterId = after.Chapters.FirstOrDefault(x => x.DeletedAt is null)?.ChapterId;
            NormalizeManifest(after);
            string operation = restore ? "RestoreDeletedChapters" : "SoftDeleteChapters";
            await Transactions.ExecuteAsync(session, operation, after, changes, token).ConfigureAwait(false);
            PublishManifestCommitted(session, operation);
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
            PublishManifestCommitted(session, "RestoreManifestBackup");
        }
        finally { session.OperationGate.Release(); }
    }

    public async Task RecoverTransactionAsync(ProjectSession session, Guid transactionId, ProjectRecoveryDecision decision,
        CancellationToken cancellationToken = default)
    {
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Transactions.RecoverAsync(session, transactionId, decision, cancellationToken).ConfigureAwait(false);
            PublishManifestCommitted(session, decision == ProjectRecoveryDecision.RollBack
                ? "RecoverTransactionRollback" : "RecoverTransactionCommit");
        }
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

    internal async Task<Guid> ImportChaptersTransactionAsync(ProjectSession session,
        IReadOnlyList<(ChapterDocument Source, int Number)> chapters, string sourceArchive, Action<Guid> published,
        Action<ChapterDocument, ChapterDocument> verify, CancellationToken token)
    {
        await session.OperationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var after = Clone(session.Manifest);
            var expected = new List<ChapterDocument>(); var files = new List<PlannedProjectFile>();
            foreach (var (source, number) in chapters)
            {
                if (after.Chapters.Any(x => x.ChapterId == source.Id || x.DeletedAt is null && x.Number == number))
                    throw new InvalidOperationException("ID/số chương nhập đã có trong target (kể cả ID xóa mềm).");
                string relative = $"Chapters/{source.Id:D}.qtaie.json";
                await EnsureDestinationEmptyAsync(session.Resolve(relative), token).ConfigureAwait(false);
                var saved = PrepareRegisteredChapter(session, source, number, null);
                expected.Add(saved); after.Chapters.Add(ToEntry(saved, number));
                files.Add(new(relative, ChapterFileStore.SerializeDocument(saved)));
                after.CurrentChapterId = saved.Id;
            }
            NormalizeManifest(after);
            Guid transactionId = await Transactions.ExecuteAsync(session, "LegacyImport", after, files, token, async () =>
            {
                foreach (var saved in expected)
                {
                    FaultPoint?.Invoke("BeforeChapterRead");
                    var actual = await ChapterFileStore.ReadVerifiedFileAsync(session.ChapterStore.GetPath(saved.Id), token).ConfigureAwait(false);
                    verify(saved, actual); // Expected already contains ONLY the explicit target identity conversion.
                }
            }, relative =>
            {
                var file = expected.SingleOrDefault(x => relative == $"Chapters/{x.Id:D}.qtaie.json");
                if (file is not null) published(file.Id);
            }, sourceArchiveRelative: Path.GetRelativePath(session.RootDirectory, sourceArchive).Replace('\\', '/'),
                sourceIdentities: chapters.Select(x => new LegacySourceIdentity(x.Source.Id, x.Source.ProjectId,
                    x.Source.CharacterProject.Id, x.Source.ChapterNumber, session.Manifest.ProjectId, x.Number)).ToArray()).ConfigureAwait(false);
            PublishManifestCommitted(session, "LegacyImport");
            return transactionId;
        }
        finally { session.OperationGate.Release(); }
    }

    internal async Task<Guid> ImportDetectedChapterChunkAsync(ProjectSession session,
        IReadOnlyList<(ChapterDocument Source, int Number)> chapters, string sourceArchive, string operation,
        Action<Guid> published, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        await session.OperationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var after = Clone(session.Manifest);
            var expected = new List<ChapterDocument>();
            var files = new List<PlannedProjectFile>();
            foreach (var (source, number) in chapters)
            {
                if (after.Chapters.Any(x => x.ChapterId == source.Id || x.DeletedAt is null && x.Number == number))
                    throw new InvalidOperationException("ID/số chương nhập đã có trong Project (kể cả ID xóa mềm).");
                string relative = $"Chapters/{source.Id:D}.qtaie.json";
                await EnsureDestinationEmptyAsync(session.Resolve(relative), token).ConfigureAwait(false);
                ChapterDocument saved = PrepareRegisteredChapter(session, source, number, source.ChineseTitle);
                expected.Add(saved);
                after.Chapters.Add(ToEntry(saved, number));
                files.Add(new(relative, ChapterFileStore.SerializeDocument(saved)));
                after.CurrentChapterId = saved.Id;
            }
            NormalizeManifest(after);
            Guid transactionId = await Transactions.ExecuteAsync(session, operation, after, files, token, async () =>
            {
                foreach (ChapterDocument saved in expected)
                {
                    FaultPoint?.Invoke("BeforeChapterRead");
                    ChapterDocument actual = await ChapterFileStore.ReadVerifiedFileAsync(
                        session.ChapterStore.GetPath(saved.Id), token).ConfigureAwait(false);
                    if (System.Text.Json.JsonSerializer.Serialize(saved, DocumentJson.Options) !=
                        System.Text.Json.JsonSerializer.Serialize(actual, DocumentJson.Options))
                        throw new InvalidDataException("Chương vừa nhập không khớp kế hoạch; transaction chưa commit.");
                }
            }, relative =>
            {
                ChapterDocument? file = expected.SingleOrDefault(x => relative == $"Chapters/{x.Id:D}.qtaie.json");
                if (file is not null) published(file.Id);
            }, sourceArchiveRelative: Path.GetRelativePath(session.RootDirectory, sourceArchive).Replace('\\', '/'))
                .ConfigureAwait(false);
            PublishManifestCommitted(session, operation);
            return transactionId;
        }
        finally { session.OperationGate.Release(); }
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
