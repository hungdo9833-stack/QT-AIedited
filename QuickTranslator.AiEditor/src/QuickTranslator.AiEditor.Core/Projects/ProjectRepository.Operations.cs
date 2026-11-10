using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed partial class ProjectRepository
{
    internal Action<string>? FaultPoint { get; set; }

    public async Task<ProjectChapterEntry> AddChapterAsync(ProjectSession session, ChapterDocument chapter, int number,
        string? chineseTitle = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(chapter);
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
            if (session.Manifest.Chapters.Any(x => x.DeletedAt is null && x.Number == number || x.ChapterId == chapter.Id))
                throw new InvalidOperationException("Chương đã có ID hoặc số chương trong manifest.");
            string relative = $"Chapters/{chapter.Id:D}.qtaie.json";
            await EnsureDestinationEmptyAsync(session.Resolve(relative), cancellationToken).ConfigureAwait(false);
            ChapterDocument saved = PrepareRegisteredChapter(session, chapter, number, chineseTitle);
            ProjectManifest after = Clone(session.Manifest);
            after.Chapters.Add(ToEntry(saved, number));
            after.CurrentChapterId = saved.Id;
            NormalizeManifest(after);
            string chapterPath = session.ChapterStore.GetPath(saved.Id);
            await session.ChapterStore.SaveSerializedAsync(saved.Id, ChapterFileStore.SerializeDocument(saved), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                FaultPoint?.Invoke("AfterChapterWrite");
                await WriteFastManifestAsync(session.ManifestPath, after, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try { File.Delete(chapterPath); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Không lưu được manifest và không thể dọn file chương chưa đăng ký.", cleanupError);
                }
                throw;
            }
            session.Manifest = after;
            PublishManifestCommitted(session, "AddChapter");
            return session.Manifest.Chapters.Single(x => x.ChapterId == saved.Id);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Không đủ quyền hoặc file bị khóa; thao tác thêm chương chưa hoàn tất.", ex);
        }
        finally { session.OperationGate.Release(); }
    }

    public async Task<ProjectChapterEntry> InsertChapterAsync(ProjectSession session, ChapterDocument chapter, int number,
        string? chineseTitle = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(chapter);
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int lastNumber = session.Manifest.Chapters.Where(x => x.DeletedAt is null)
                .Select(x => x.Number).DefaultIfEmpty(0).Max();
            if (number <= 0 || number > lastNumber + 1) throw new ArgumentOutOfRangeException(nameof(number));
            if (session.Manifest.Chapters.Any(x => x.ChapterId == chapter.Id))
                throw new InvalidOperationException("Chương đã có ID trong manifest.");
            string relative = $"Chapters/{chapter.Id:D}.qtaie.json";
            await EnsureDestinationEmptyAsync(session.Resolve(relative), cancellationToken).ConfigureAwait(false);
            ChapterDocument saved = PrepareRegisteredChapter(session, chapter, number, chineseTitle);
            ProjectManifest after = Clone(session.Manifest);
            foreach (ProjectChapterEntry entry in after.Chapters.Where(x => x.DeletedAt is null && x.Number >= number))
                entry.Number++;
            after.Chapters.Add(ToEntry(saved, number));
            after.CurrentChapterId = saved.Id;
            NormalizeManifest(after);
            string chapterPath = session.ChapterStore.GetPath(saved.Id);
            await session.ChapterStore.SaveSerializedAsync(saved.Id, ChapterFileStore.SerializeDocument(saved), cancellationToken)
                .ConfigureAwait(false);
            try
            {
                FaultPoint?.Invoke("AfterChapterWrite");
                await WriteFastManifestAsync(session.ManifestPath, after, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try { File.Delete(chapterPath); }
                catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException)
                {
                    throw new IOException("Không lưu được manifest và không thể dọn file chương chưa đăng ký.", cleanupError);
                }
                throw;
            }
            session.Manifest = after;
            PublishManifestCommitted(session, "InsertChapter");
            return session.Manifest.Chapters.Single(x => x.ChapterId == saved.Id);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new IOException("Không đủ quyền hoặc file bị khóa; thao tác chèn chương chưa hoàn tất.", ex);
        }
        finally { session.OperationGate.Release(); }
    }

    public Task SoftDeleteChapterAsync(ProjectSession session, Guid chapterId, CancellationToken cancellationToken = default) =>
        SoftDeleteChaptersAsync(session, [chapterId], cancellationToken);
    public Task RestoreDeletedChapterAsync(ProjectSession session, Guid chapterId, CancellationToken cancellationToken = default) =>
        RestoreDeletedChaptersAsync(session, [chapterId], cancellationToken);
    public Task SoftDeleteChaptersAsync(ProjectSession session, IReadOnlyCollection<Guid> chapterIds,
        CancellationToken cancellationToken = default) => MoveChaptersAsync(session, chapterIds, false, cancellationToken);
    public Task RestoreDeletedChaptersAsync(ProjectSession session, IReadOnlyCollection<Guid> chapterIds,
        CancellationToken cancellationToken = default) => MoveChaptersAsync(session, chapterIds, true, cancellationToken);

    private async Task MoveChaptersAsync(ProjectSession session, IReadOnlyCollection<Guid> chapterIds, bool restore,
        CancellationToken token)
    {
        Guid[] ids = chapterIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        if (ids.Any(id => id == Guid.Empty)) throw new ArgumentException("ChapterId không hợp lệ.", nameof(chapterIds));
        SemaphoreSlim[] chapterGates = ids.Order().Select(session.GetChapterOperationGate).ToArray();
        int acquired = 0;
        try
        {
            for (; acquired < chapterGates.Length; acquired++)
                await chapterGates[acquired].WaitAsync(token).ConfigureAwait(false);
            await session.OperationGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ProjectManifest after = Clone(session.Manifest);
                ProjectChapterEntry[] entries = ids.Select(id => after.Chapters.SingleOrDefault(x =>
                    x.ChapterId == id && (x.DeletedAt is not null) == restore)
                    ?? throw new FileNotFoundException($"Không tìm thấy chương {id:D} trong trạng thái cần chuyển.")).ToArray();
                // Numbers stay consecutive: a deleted chapter closes its gap, a restored one goes back to its old place
                // and pushes the chapters from there on up, exactly undoing the delete (as Insert does).
                if (restore) ReopenNumbers(after, entries);
                else CloseNumbers(after, entries);
                DateTimeOffset movedAt = DateTimeOffset.UtcNow;
                var moves = new List<(string Source, string Destination)>();
                foreach (ProjectChapterEntry entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    string source = session.Resolve(entry.RelativePath);
                    _ = await ReadRegisteredChapterAsync(session, entry, token).ConfigureAwait(false);
                    string relative = restore ? $"Chapters/{entry.ChapterId:D}.qtaie.json"
                        : $"Deleted/{movedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}-{entry.ChapterId:D}.qtaie.json";
                    string destination = session.Resolve(relative);
                    await EnsureDestinationEmptyAsync(destination, token).ConfigureAwait(false);
                    moves.Add((source, destination));
                    entry.RelativePath = relative;
                    entry.DeletedAt = restore ? null : movedAt;
                }
                if (restore) after.CurrentChapterId = entries[^1].ChapterId;
                else if (after.CurrentChapterId is Guid current && ids.Contains(current))
                    after.CurrentChapterId = after.Chapters.FirstOrDefault(x => x.DeletedAt is null)?.ChapterId;
                NormalizeManifest(after);
                var completed = new List<(string Source, string Destination)>();
                try
                {
                    foreach (var move in moves)
                    {
                        token.ThrowIfCancellationRequested();
                        Directory.CreateDirectory(Path.GetDirectoryName(move.Destination)!);
                        FaultPoint?.Invoke("BeforeChapterMove");
                        File.Move(move.Source, move.Destination);
                        completed.Add(move);
                        FaultPoint?.Invoke("AfterChapterMove");
                    }
                    FaultPoint?.Invoke("BeforeMoveManifestWrite");
                    await WriteFastManifestAsync(session.ManifestPath, after, token).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    // Roll back without the cancelled token, and attempt every move even if one is locked.
                    var failures = new List<Exception> { failure };
                    foreach (var move in completed.AsEnumerable().Reverse())
                    {
                        try { File.Move(move.Destination, move.Source); }
                        catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                        { failures.Add(rollback); }
                    }
                    if (failures.Count > 1)
                        throw new IOException("Chapter move rollback was incomplete; reopen the project to recover.",
                            new AggregateException(failures));
                    throw;
                }
                session.Manifest = after;
                PublishManifestCommitted(session, restore ? "RestoreDeletedChapters" : "SoftDeleteChapters");
            }
            finally { session.OperationGate.Release(); }
        }
        finally
        {
            for (int index = acquired - 1; index >= 0; index--) chapterGates[index].Release();
        }
    }

    /// <summary>Active chapters after the deleted ones move down, so no number is left empty.</summary>
    private static void CloseNumbers(ProjectManifest manifest, IReadOnlyCollection<ProjectChapterEntry> deleted)
    {
        int[] removed = deleted.Select(entry => entry.Number).Order().ToArray();
        HashSet<Guid> ids = deleted.Select(entry => entry.ChapterId).ToHashSet();
        foreach (ProjectChapterEntry entry in manifest.Chapters.Where(x => x.DeletedAt is null && !ids.Contains(x.ChapterId)))
            entry.Number -= removed.Count(number => number < entry.Number);
    }

    /// <summary>Each restored chapter takes its old number back (at most one past the last) and the rest move up.</summary>
    private static void ReopenNumbers(ProjectManifest manifest, IReadOnlyCollection<ProjectChapterEntry> restored)
    {
        HashSet<Guid> ids = restored.Select(entry => entry.ChapterId).ToHashSet();
        var active = manifest.Chapters.Where(x => x.DeletedAt is null && !ids.Contains(x.ChapterId)).ToList();
        foreach (ProjectChapterEntry entry in restored.OrderBy(x => x.Number))
        {
            int last = active.Select(x => x.Number).DefaultIfEmpty(0).Max();
            int target = Math.Clamp(entry.Number, 1, last + 1);
            foreach (ProjectChapterEntry later in active.Where(x => x.Number >= target)) later.Number++;
            entry.Number = target;
            active.Add(entry);
        }
    }

    /// <summary>
    /// Renumbers the active chapters 1, 2, 3… in their current order. Fixes books where an older version left gaps
    /// (a chapter inserted and deleted again pushed every later number up for good). Returns how many changed.
    /// </summary>
    public async Task<int> CloseChapterNumberGapsAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectManifest after = Clone(session.Manifest);
            int changed = 0, next = 1;
            foreach (ProjectChapterEntry entry in after.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number).ToArray())
            {
                if (entry.Number != next) { entry.Number = next; changed++; }
                next++;
            }
            if (changed == 0) return 0;
            NormalizeManifest(after);
            await WriteFastManifestAsync(session.ManifestPath, after, cancellationToken).ConfigureAwait(false);
            session.Manifest = after;
            PublishManifestCommitted(session, "CloseChapterNumberGaps");
            return changed;
        }
        finally { session.OperationGate.Release(); }
    }

    /// <summary>Numbers missing between 1 and the last active chapter (empty when the book is numbered without gaps).</summary>
    public static IReadOnlyList<int> MissingChapterNumbers(ProjectManifest manifest)
    {
        HashSet<int> used = manifest.Chapters.Where(x => x.DeletedAt is null).Select(x => x.Number).ToHashSet();
        int last = used.DefaultIfEmpty(0).Max();
        return Enumerable.Range(1, Math.Max(0, last)).Where(number => !used.Contains(number)).ToArray();
    }

    internal static ChapterDocument PrepareRegisteredChapter(ProjectSession session, ChapterDocument chapter, int number,
        string? chineseTitle)
    {
        ChapterDocument saved = DocumentJson.Clone(chapter);
        saved.ProjectId = session.Manifest.ProjectId;
        saved.ChapterNumber = number;
        saved.ChineseTitle = chineseTitle?.Trim() ?? saved.ChineseTitle;
        saved.CharacterProject.Id = session.Manifest.ProjectId;
        DocumentJson.Validate(saved);
        return saved;
    }

    internal async Task<Guid> ImportDetectedChapterChunkAsync(ProjectSession session,
        IReadOnlyList<(ChapterDocument Source, int Number)> chapters, string operation,
        Action<Guid> staged, bool replaceSoftDeletedHistory, CancellationToken token)
    {
        Guid operationId = Guid.NewGuid();
        await session.OperationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (chapters.Count == 0) return operationId;
            ProjectManifest after = Clone(session.Manifest);
            var receipts = new List<string>(chapters.Count);
            var history = new List<(string Path, byte[] Bytes)>();
            foreach ((ChapterDocument source, int number) in chapters)
            {
                token.ThrowIfCancellationRequested();
                ProjectChapterEntry? replaced = after.Chapters.SingleOrDefault(x => x.ChapterId == source.Id);
                string? deletedPath = null;
                byte[]? deletedBytes = null;
                if (replaced is not null)
                {
                    if (!replaceSoftDeletedHistory || replaced.DeletedAt is null)
                        throw new InvalidOperationException("ID chương nhập đã có trong Project; bản active không được ghi đè.");
                    deletedPath = session.Resolve(replaced.RelativePath);
                    deletedBytes = await File.ReadAllBytesAsync(deletedPath, token).ConfigureAwait(false);
                    ChapterDocument deleted = ChapterFileStore.DeserializeDocument(deletedBytes);
                    if (deleted.Id != replaced.ChapterId || deleted.ProjectId != session.Manifest.ProjectId)
                        throw new InvalidDataException("File History không thuộc ChapterId/Project cần thay thế.");
                }
                if (after.Chapters.Any(x => x.ChapterId != source.Id && x.DeletedAt is null && x.Number == number))
                    throw new InvalidOperationException("Số chương nhập đã thuộc một chapter active khác.");
                string relative = $"Chapters/{source.Id:D}.qtaie.json";
                string destination = session.Resolve(relative);
                ChapterDocument? pending = await ReadPendingImportAsync(session, source.Id, token).ConfigureAwait(false);
                if (pending is not null && (pending.ChapterNumber != number || pending.Title != source.Title ||
                    pending.ChineseTitle != source.ChineseTitle || pending.OriginalText != source.OriginalText))
                    throw new IOException("Pending import does not match this import; existing content was preserved.");
                ChapterDocument saved = pending ?? PrepareRegisteredChapter(session, source, number, source.ChineseTitle);
                if (pending is null)
                {
                    await EnsureDestinationEmptyAsync(destination, token).ConfigureAwait(false);
                    FaultPoint?.Invoke("BeforeImportChapterWrite");
                    await WritePendingImportAsync(session, saved, token).ConfigureAwait(false);
                }
                FaultPoint?.Invoke("AfterImportChapterWrite");
                if (replaced is not null) after.Chapters.RemoveAll(x => x.ChapterId == replaced.ChapterId);
                after.Chapters.Add(ToEntry(saved, number));
                after.CurrentChapterId = saved.Id;
                receipts.Add(ImportReceiptPath(destination));
                if (deletedPath is not null) history.Add((deletedPath, deletedBytes!));
                staged(saved.Id);
            }
            // Until this single atomic replacement succeeds, the live manifest and all History files
            // remain untouched. Staged chapters keep their receipts so the entire chunk can resume.
            token.ThrowIfCancellationRequested();
            NormalizeManifest(after);
            FaultPoint?.Invoke("BeforeImportManifestWrite");
            await WriteFastManifestAsync(session.ManifestPath, after, token).ConfigureAwait(false);
            session.Manifest = after;
            FaultPoint?.Invoke("AfterImportManifestWrite");
            // Cleanup cannot undo a durable commit and must finish even if cancellation arrives now.
            var referencedPaths = after.Chapters.Select(entry => session.Resolve(entry.RelativePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string receipt in receipts) TryDeleteUnreferencedFile(session, receipt, referencedPaths);
            foreach (var old in history)
            {
                try
                {
                    if ((await File.ReadAllBytesAsync(old.Path, CancellationToken.None).ConfigureAwait(false))
                        .AsSpan().SequenceEqual(old.Bytes))
                        TryDeleteUnreferencedFile(session, old.Path, referencedPaths);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning($"Could not retire old History file: {error.Message}"); }
            }
            PublishManifestCommitted(session, operation);
            return operationId;
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

    internal static Task EnsureDestinationEmptyAsync(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (File.Exists(path) || Directory.Exists(path))
            throw new IOException($"Đích {Path.GetFileName(path)} đã tồn tại; không tự ghi đè.");
        return Task.CompletedTask;
    }
}
