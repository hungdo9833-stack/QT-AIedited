using System.Security.Cryptography;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public enum ProjectTransactionState { Pending, Committed, RollingBack, RolledBack, RecoveryRequired }
public enum ProjectRecoveryDecision { RollBack, ConfirmCommitted }
public sealed record ProjectTransactionPreview(Guid Id, string Operation, ProjectTransactionState State,
    bool CanRollBack, bool CanConfirmCommitted, IReadOnlyList<string> Issues, string JournalPath)
{
    public string? SourceArchivePath { get; init; }
}
public sealed record OrphanChapterPreview(string RelativePath, Guid? ChapterId, string? Title, bool BackupOnly, string? Error);
public sealed record ProjectRecoveryReport(IReadOnlyList<ProjectTransactionPreview> Transactions,
    IReadOnlyList<OrphanChapterPreview> Orphans)
{
    public bool RequiresDecision => Transactions.Any(x => x.State is not (ProjectTransactionState.Committed or ProjectTransactionState.RolledBack));
}

public sealed class ProjectTransactionException(string message, Exception inner, Guid transactionId,
    bool rollbackComplete) : IOException(message, inner)
{
    public Guid TransactionId { get; } = transactionId;
    public bool RollbackComplete { get; } = rollbackComplete;
}

// Test-only interruption models process loss: no in-process catch/rollback runs after this checkpoint.
internal sealed class ProjectOperationInterruptedException(string point) : Exception(point);
internal sealed record PlannedProjectFile(string RelativePath, byte[]? After);
internal sealed record LegacySourceIdentity(Guid ChapterId, Guid? OriginalProjectId, Guid OriginalCharacterProjectId,
    int? OriginalNumber, Guid TargetProjectId, int TargetNumber);

internal sealed class ProjectTransactionJournal(Action<string> checkpoint)
{
    private static readonly JsonSerializerOptions Pretty = new(DocumentJson.Options) { WriteIndented = true };

    internal async Task<Guid> ExecuteAsync(ProjectSession session, string operation, ProjectManifest after,
        IReadOnlyList<PlannedProjectFile> planned, CancellationToken cancellationToken, Func<Task>? verify = null, Action<string>? published = null,
        string? sourceArchiveRelative = null, IReadOnlyList<LegacySourceIdentity>? sourceIdentities = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (session.RecoveryReport.RequiresDecision)
            throw new InvalidOperationException("Dự án có transaction chưa đối chiếu; hãy xem báo cáo và chọn phục hồi trước khi ghi tiếp.");
        var diskBefore = await ProjectRepository.ReadManifestAsync(session.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (JsonSerializer.Serialize(diskBefore, DocumentJson.Options) != JsonSerializer.Serialize(session.Manifest, DocumentJson.Options))
            throw new InvalidOperationException("Manifest trong bộ nhớ đã khác ổ đĩa; lưu/đối chiếu thay đổi trước khi thao tác chương.");
        after.Validate();
        var journal = new Journal { ProjectId = session.Manifest.ProjectId, Operation = operation,
            Before = ProjectRepository.Clone(session.Manifest), After = ProjectRepository.Clone(after),
            SourceArchiveRelative = sourceArchiveRelative, SourceIdentities = sourceIdentities?.ToList() ?? [] };
        checkpoint("BeforeJournal");
        string directory = session.Resolve($"History/Transactions/{journal.Id:D}");
        Directory.CreateDirectory(directory);
        string manifestRelative = Path.GetRelativePath(session.RootDirectory, session.ManifestPath).Replace('\\', '/');
        byte[] manifestBytes = await File.ReadAllBytesAsync(session.ManifestPath, cancellationToken).ConfigureAwait(false);
        var changes = planned.Concat(new[] { new PlannedProjectFile(manifestRelative + ".bak", manifestBytes),
            new PlannedProjectFile(manifestRelative, ProjectRepository.SerializeManifest(after)) }).ToArray();
        if (changes.Select(x => x.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != changes.Length)
            throw new InvalidDataException("Transaction có đường dẫn trùng nhau.");
        for (int i = 0; i < changes.Length; i++)
        {
            string path = session.Resolve(changes[i].RelativePath);
            var before = await ReadOptionalAsync(path, cancellationToken).ConfigureAwait(false);
            var change = new FileChange { Path = changes[i].RelativePath, BeforeSha = HashNullable(before), AfterSha = HashNullable(changes[i].After),
                BeforeCopy = before is null ? null : $"before-{i}.bin", AfterCopy = changes[i].After is null ? null : $"after-{i}.bin" };
            if (before is not null) await WriteCopyAsync(Path.Combine(directory, change.BeforeCopy!), before, cancellationToken).ConfigureAwait(false);
            if (changes[i].After is { } bytes) await WriteCopyAsync(Path.Combine(directory, change.AfterCopy!), bytes, cancellationToken).ConfigureAwait(false);
            journal.Files.Add(change);
        }
        await SaveAsync(directory, journal, cancellationToken).ConfigureAwait(false); // Durable intent before any live change.
        try
        {
            checkpoint("AfterJournal");
            for (int i = 0; i < journal.Files.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint($"BeforeFile:{i}");
                await ApplyAsync(session, directory, journal.Files[i], false, cancellationToken).ConfigureAwait(false);
                journal.Files[i].Published = true;
                published?.Invoke(journal.Files[i].Path);
                await SaveAsync(directory, journal, CancellationToken.None).ConfigureAwait(false);
                checkpoint($"AfterFile:{i}");
            }
            checkpoint("BeforeCommit");
            if (verify is not null) await verify().ConfigureAwait(false);
            foreach (var file in journal.Files)
                if (HashNullable(await ReadOptionalAsync(session.Resolve(file.Path), cancellationToken).ConfigureAwait(false)) != file.AfterSha)
                    throw new InvalidDataException($"File {file.Path} không khớp kế hoạch sau ghi; chưa commit manifest.");
            journal.State = ProjectTransactionState.Committed;
            await SaveAsync(directory, journal, CancellationToken.None).ConfigureAwait(false);
            session.Manifest = ProjectRepository.Clone(after);
            return journal.Id;
        }
        catch (ProjectOperationInterruptedException)
        {
            session.Manifest = await ProjectRepository.ReadManifestAsync(session.ManifestPath, CancellationToken.None).ConfigureAwait(false);
            session.RecoveryReport = await InspectAsync(session, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception error)
        {
            var failures = new List<Exception>();
            journal.State = ProjectTransactionState.RollingBack;
            try { await SaveAsync(directory, journal, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception journalError) { failures.Add(journalError); }
            failures.AddRange(await RollBackFilesAsync(session, directory, journal).ConfigureAwait(false));
            journal.State = failures.Count == 0 ? ProjectTransactionState.RolledBack : ProjectTransactionState.RecoveryRequired;
            try { await SaveAsync(directory, journal, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception journalError) { failures.Add(journalError); }
            failures.AddRange(await RefreshAfterOperationAsync(session, journal.Id, operation).ConfigureAwait(false));
            error.Data["ProjectTransactionId"] = journal.Id;
            error.Data["RollbackComplete"] = failures.Count == 0;
            if (failures.Count == 0)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw new ProjectTransactionException("Thao tác chưa hoàn tất; rollback cần đối chiếu thủ công. File/journal và các bản sao được giữ nguyên.",
                new AggregateException(new[] { error }.Concat(failures)), journal.Id, false);
        }
    }

    internal async Task RecoverAsync(ProjectSession session, Guid id, ProjectRecoveryDecision decision,
        CancellationToken cancellationToken)
    {
        var report = await InspectAsync(session, cancellationToken).ConfigureAwait(false);
        var preview = report.Transactions.SingleOrDefault(x => x.Id == id)
            ?? throw new InvalidDataException("Không tìm thấy transaction trong project này.");
        string directory = Path.GetDirectoryName(preview.JournalPath)!;
        var journal = await ReadAsync(session, directory, cancellationToken).ConfigureAwait(false);
        try
        {
            if (decision == ProjectRecoveryDecision.ConfirmCommitted)
            {
                if (!preview.CanConfirmCommitted) throw new InvalidOperationException("File/manifest chưa khớp trạng thái sau; không thể xác nhận commit.");
                journal.State = ProjectTransactionState.Committed;
                await SaveAsync(directory, journal, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                if (!preview.CanRollBack) throw new InvalidOperationException("Dữ liệu có xung đột ngoài transaction; giữ nguyên để đối chiếu thủ công.");
                journal.State = ProjectTransactionState.RollingBack;
                await SaveAsync(directory, journal, cancellationToken).ConfigureAwait(false);
                var failures = await RollBackFilesAsync(session, directory, journal).ConfigureAwait(false);
                journal.State = failures.Count == 0 ? ProjectTransactionState.RolledBack : ProjectTransactionState.RecoveryRequired;
                await SaveAsync(directory, journal, CancellationToken.None).ConfigureAwait(false);
                if (failures.Count > 0) throw new ProjectTransactionException("Phục hồi chưa hoàn tất; giữ journal và thử lại sau khi xử lý quyền ghi/khóa file.",
                    new AggregateException(failures), id, false);
            }
        }
        catch (Exception error)
        {
            var failures = await RefreshAfterOperationAsync(session, id, journal.Operation).ConfigureAwait(false);
            if (failures.Count > 0)
                throw new ProjectTransactionException("Phục hồi lỗi và chưa đọc lại được trạng thái; giữ journal, xử lý quyền/khóa rồi mở lại dự án.",
                    new AggregateException(new[] { error }.Concat(failures)), id, false);
            throw;
        }
        var refreshErrors = await RefreshAfterOperationAsync(session, id, journal.Operation).ConfigureAwait(false);
        if (refreshErrors.Count > 0)
            throw new ProjectTransactionException("Đã ghi quyết định recovery nhưng chưa đọc lại được trạng thái; mở lại và đối chiếu trước khi ghi tiếp.",
                new AggregateException(refreshErrors), id, false);
    }

    private static async Task<List<Exception>> RefreshAfterOperationAsync(ProjectSession session, Guid id, string operation)
    {
        var failures = new List<Exception>();
        try { session.Manifest = await ProjectRepository.ReadManifestAsync(session.ManifestPath, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        try { session.RecoveryReport = await InspectAsync(session, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count > 0)
        {
            // Do not allow subsequent writes using a stale snapshot when rereading itself failed.
            var blocked = new ProjectTransactionPreview(id, operation, ProjectTransactionState.RecoveryRequired, false, false,
                failures.Select(x => "Không xác minh được trạng thái sau thao tác: " + x.Message).ToArray(), session.Resolve($"History/Transactions/{id:D}/journal.json"));
            session.RecoveryReport = new(session.RecoveryReport.Transactions.Where(x => x.Id != id).Append(blocked).ToArray(), session.RecoveryReport.Orphans);
        }
        return failures;
    }

    internal static async Task<ProjectRecoveryReport> InspectAsync(ProjectSession session, CancellationToken cancellationToken)
    {
        var transactions = new List<ProjectTransactionPreview>();
        string root = session.Resolve("History/Transactions");
        if (Directory.Exists(root))
        foreach (string directory in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var journal = await ReadAsync(session, directory, cancellationToken).ConfigureAwait(false);
                var issues = new List<string>(); bool allAfter = true;
                foreach (var file in journal.Files)
                {
                    string? hash = HashNullable(await ReadOptionalAsync(session.Resolve(file.Path), cancellationToken).ConfigureAwait(false));
                    if (hash != file.BeforeSha && hash != file.AfterSha) issues.Add($"Xung đột tại {file.Path}: không khớp trước/sau transaction.");
                    if (hash != file.AfterSha) allAfter = false;
                }
                bool pending = journal.State is not (ProjectTransactionState.Committed or ProjectTransactionState.RolledBack);
                transactions.Add(new(journal.Id, journal.Operation, journal.State, pending && issues.Count == 0, pending && allAfter && issues.Count == 0, issues,
                    Path.Combine(directory, "journal.json")) { SourceArchivePath = journal.SourceArchiveRelative is null ? null : session.Resolve(journal.SourceArchiveRelative) });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                transactions.Add(new(Guid.TryParse(Path.GetFileName(directory), out var id) ? id : Guid.Empty, "Journal không đọc được",
                    ProjectTransactionState.RecoveryRequired, false, false, [ex.Message], Path.Combine(directory, "journal.json")));
            }
        }
        var orphans = new List<OrphanChapterPreview>();
        var known = session.Manifest.Chapters.Select(x => session.Resolve(x.RelativePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in new[] { session.Resolve("Chapters"), session.Resolve("History/Deleted") }.Where(Directory.Exists))
        foreach (string path in Directory.GetFiles(directory, "*.qtaie.json*", SearchOption.TopDirectoryOnly)
                     .Where(x => x.EndsWith(".qtaie.json", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".qtaie.json.bak", StringComparison.OrdinalIgnoreCase)))
        {
            string primary = path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
            if (known.Contains(primary)) continue;
            string relative = Path.GetRelativePath(session.RootDirectory, path).Replace('\\', '/');
            try
            {
                var chapter = await ChapterFileStore.ReadVerifiedFileAsync(path, cancellationToken).ConfigureAwait(false);
                orphans.Add(new(relative, chapter.Id, chapter.Title, path != primary,
                    chapter.ProjectId != session.Manifest.ProjectId ? "ProjectId khác dự án đang mở; không tự nhận liên kết." : null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            { orphans.Add(new(relative, null, null, path != primary, ex.Message)); }
        }
        return new(transactions, orphans);
    }

    private async Task<List<Exception>> RollBackFilesAsync(ProjectSession session, string directory, Journal journal)
    {
        var failures = new List<Exception>();
        // Restore chapter files before restoring manifest references to them.
        var manifestFiles = journal.Files.Where(x => x.Path.EndsWith(".qtproj", StringComparison.OrdinalIgnoreCase) || x.Path.EndsWith(".qtproj.bak", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var file in journal.Files.Except(manifestFiles).Reverse().Concat(manifestFiles.Reverse()))
        {
            if (manifestFiles.Contains(file) && failures.Count > 0) break;
            try { checkpoint("Rollback:" + file.Path); await ApplyAsync(session, directory, file, true, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { failures.Add(ex); }
        }
        return failures;
    }

    private static async Task ApplyAsync(ProjectSession session, string directory, FileChange change, bool rollback, CancellationToken token)
    {
        string path = session.Resolve(change.Path);
        string? expected = rollback ? change.AfterSha : change.BeforeSha;
        string? desired = rollback ? change.BeforeSha : change.AfterSha;
        string? actual = HashNullable(await ReadOptionalAsync(path, token).ConfigureAwait(false));
        if (actual == desired) return; // Idempotent recovery.
        if (actual != expected)
        {
            if (rollback && change.BeforeSha is null && change.Published && actual is not null)
            {
                // The transaction created this path. Retain changed verification bytes rather than deleting them.
                File.Move(path, Path.Combine(directory, "retained-changed-" + Guid.NewGuid().ToString("N") + ".bin"));
                return;
            }
            throw new InvalidDataException($"File {change.Path} đổi ngoài transaction; không tự ghi đè.");
        }
        if (desired is null)
        {
            // Retain every removed/rolled-back file as evidence, never delete an unknown orphan.
            File.Move(path, Path.Combine(directory, "retained-" + Guid.NewGuid().ToString("N") + ".bin"));
        }
        else
        {
            string copy = rollback ? change.BeforeCopy! : change.AfterCopy!;
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(directory, copy), token).ConfigureAwait(false);
            if (HashNullable(bytes) != desired) throw new InvalidDataException("Bản sao transaction hỏng; không ghi dữ liệu cứu hộ chưa xác minh.");
            await AtomicLocalFile.WriteAsync(path, bytes, token, preserveExistingBackup: true).ConfigureAwait(false);
        }
    }

    internal static async Task<byte[]?> ReadOptionalAsync(string path, CancellationToken token)
    {
        try { return await File.ReadAllBytesAsync(path, token).ConfigureAwait(false); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private static string? HashNullable(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
    private static async Task WriteCopyAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false); await stream.FlushAsync(token).ConfigureAwait(false); stream.Flush(true);
    }
    private static Task SaveAsync(string directory, Journal journal, CancellationToken token)
    {
        var content = JsonSerializer.SerializeToElement(journal, DocumentJson.Options);
        return AtomicLocalFile.WriteAsync(Path.Combine(directory, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(
            new JournalEnvelope { Content = content, Sha256 = HashNullable(JsonSerializer.SerializeToUtf8Bytes(content, DocumentJson.Options))! }, Pretty), token, true);
    }
    private static async Task<Journal> ReadAsync(ProjectSession session, string directory, CancellationToken token)
    {
        var envelope = JsonSerializer.Deserialize<JournalEnvelope>(await File.ReadAllBytesAsync(Path.Combine(directory, "journal.json"), token).ConfigureAwait(false), DocumentJson.Options)
            ?? throw new InvalidDataException("Journal trống.");
        if (envelope.SchemaVersion != 1 || envelope.Content.ValueKind != JsonValueKind.Object ||
            HashNullable(JsonSerializer.SerializeToUtf8Bytes(envelope.Content, DocumentJson.Options)) != envelope.Sha256)
            throw new InvalidDataException("Schema/checksum journal không hợp lệ.");
        var journal = envelope.Content.Deserialize<Journal>(DocumentJson.Options) ?? throw new InvalidDataException("Thiếu journal.");
        if (journal.ProjectId != session.Manifest.ProjectId || journal.Id.ToString("D") != Path.GetFileName(directory) ||
            !Enum.IsDefined(journal.State) || journal.Files is null || journal.Files.Count == 0 || journal.Files.Any(x => x is null) ||
            journal.Before is null || journal.After is null || journal.SourceIdentities is null || journal.Before.ProjectId != journal.ProjectId || journal.After.ProjectId != journal.ProjectId ||
            journal.Files.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Count)
            throw new InvalidDataException("Journal không thuộc project hoặc có đường dẫn trùng.");
        journal.Before.Validate(); journal.After.Validate();
        if (journal.SourceIdentities.Any(x => x is null || x.ChapterId == Guid.Empty || x.TargetProjectId != journal.ProjectId ||
            x.TargetNumber <= 0 || !journal.After.Chapters.Any(entry => entry.ChapterId == x.ChapterId && entry.Number == x.TargetNumber)) ||
            journal.SourceIdentities.Select(x => x.ChapterId).Distinct().Count() != journal.SourceIdentities.Count)
            throw new InvalidDataException("Chuyển đổi identity trong journal không khớp manifest đích.");
        if (journal.SourceArchiveRelative is not null)
        {
            ProjectPath.EnsureSafeRelative(journal.SourceArchiveRelative);
            if (!journal.SourceArchiveRelative.StartsWith("Backups/", StringComparison.Ordinal) || !journal.SourceArchiveRelative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Đường dẫn SourceArchive trong journal không hợp lệ.");
        }
        string manifest = Path.GetRelativePath(session.RootDirectory, session.ManifestPath).Replace('\\', '/');
        var chapterPaths = journal.Before.Chapters.Concat(journal.After.Chapters).SelectMany(x => new[] { x.RelativePath, x.RelativePath + ".bak" })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in journal.Files)
        {
            ProjectPath.EnsureSafeRelative(file.Path);
            if (file.Path != manifest && file.Path != manifest + ".bak" &&
                !chapterPaths.Contains(file.Path))
                throw new InvalidDataException("Journal nhắm vào file ngoài phạm vi chapter/manifest.");
            if (file.Path != manifest && file.Path != manifest + ".bak" &&
                !(file.Path.EndsWith(".qtaie.json", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".qtaie.json.bak", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Journal có file ngoài định dạng chapter.");
            foreach (var pair in new[] { (file.BeforeCopy, file.BeforeSha), (file.AfterCopy, file.AfterSha) })
            {
                if ((pair.Item1 is null) != (pair.Item2 is null)) throw new InvalidDataException("Journal thiếu bản sao/hash.");
                if (pair.Item1 is null) continue;
                if (Path.GetFileName(pair.Item1) != pair.Item1) throw new InvalidDataException("Đường dẫn bản sao journal không hợp lệ.");
                if (HashNullable(await File.ReadAllBytesAsync(Path.Combine(directory, pair.Item1), token).ConfigureAwait(false)) != pair.Item2)
                    throw new InvalidDataException("Bản sao journal không khớp checksum.");
            }
        }
        return journal;
    }
    private sealed class Journal
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public Guid ProjectId { get; set; }
        public string Operation { get; set; } = "";
        public ProjectTransactionState State { get; set; }
        public ProjectManifest Before { get; set; } = new();
        public ProjectManifest After { get; set; } = new();
        public List<FileChange> Files { get; set; } = [];
        public string? SourceArchiveRelative { get; set; }
        public List<LegacySourceIdentity> SourceIdentities { get; set; } = [];
    }
    private sealed class FileChange
    {
        public string Path { get; set; } = "";
        public string? BeforeSha { get; set; }
        public string? AfterSha { get; set; }
        public string? BeforeCopy { get; set; }
        public string? AfterCopy { get; set; }
        public bool Published { get; set; }
    }
    private sealed class JournalEnvelope
    {
        public int SchemaVersion { get; set; } = 1;
        public string Sha256 { get; set; } = "";
        public JsonElement Content { get; set; }
    }
}
