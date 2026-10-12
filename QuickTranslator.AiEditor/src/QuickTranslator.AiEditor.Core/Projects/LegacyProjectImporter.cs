using System.IO.Compression;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed record LegacyChapterCandidate(string Path, Guid ChapterId, string Title, DateTimeOffset UpdatedAt,
    int SegmentCount, long FileSize, string? Error = null)
{
    public string? LoadedPath { get; init; }
    public bool RecoveredFromBackup { get; init; }
}
public sealed record LegacyChapterSelection(LegacyChapterCandidate Candidate, int ChapterNumber);
public enum LegacyImportChapterState { NotStarted, Imported, RolledBack, NeedsRecovery }
public sealed record LegacyImportChapterOutcome(Guid ChapterId, int Number, string SourcePath, string TargetPath, LegacyImportChapterState State)
{
    public string? LoadedSourcePath { get; init; }
}
public sealed record LegacyImportReport(string? SourceBackupPath, Guid? TransactionId, bool RollbackComplete,
    IReadOnlyList<LegacyImportChapterOutcome> Chapters, string? Error);
public sealed record LegacyImportResult(int ImportedCount, string SourceBackupPath, IReadOnlyList<Guid> ChapterIds)
{
    public LegacyImportReport? Report { get; init; }
}
public sealed class LegacyImportException(string message, Exception inner, LegacyImportReport report) : IOException(message, inner)
{
    public LegacyImportReport Report { get; } = report;
}
public sealed class LegacyImportCanceledException(Exception inner, LegacyImportReport report, CancellationToken token)
    : OperationCanceledException("Đã hủy import; target đã giữ/phục hồi trạng thái trước thao tác. Nguồn/ZIP/journal được giữ để đối chiếu.", inner, token)
{
    public LegacyImportReport Report { get; } = report;
}

public sealed class LegacyProjectImporter(ProjectRepository repository)
{
    public async Task<IReadOnlyList<LegacyChapterCandidate>> ScanAsync(string legacyDirectory,
        CancellationToken cancellationToken = default)
    {
        var store = new ChapterFileStore(legacyDirectory);
        var paths = store.EnumerateChapterPaths().Concat(Directory.Exists(legacyDirectory)
            ? Directory.GetFiles(legacyDirectory, "*.qtaie.json.bak").Select(x => x[..^4]) : [])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var results = new List<LegacyChapterCandidate>();
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var loaded = await store.LoadAsync(path, cancellationToken).ConfigureAwait(false);
                var chapter = loaded.Chapter;
                results.Add(new(path, chapter.Id, chapter.Title, chapter.UpdatedAt, chapter.Segments.Count, new FileInfo(loaded.LoadedPath).Length)
                    { LoadedPath = loaded.LoadedPath, RecoveredFromBackup = loaded.RecoveredFromBackup });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                long size = 0;
                if (File.Exists(path)) size = new FileInfo(path).Length;
                results.Add(new(path, Guid.Empty, Path.GetFileName(path), default, 0, size, ex.Message));
            }
        }
        return results.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public async Task<LegacyImportResult> ImportAsync(ProjectSession target, IEnumerable<LegacyChapterSelection> selections,
        CancellationToken cancellationToken = default)
    {
        var selected = selections.ToArray();
        if (selected.Length == 0) throw new ArgumentException("Chưa chọn chương cũ để nhập.");
        if (selected.Any(x => x.Candidate.Error is not null || x.Candidate.ChapterId == Guid.Empty))
            throw new InvalidDataException("Danh sách chọn có tệp chương lỗi.");
        if (selected.Any(x => x.ChapterNumber <= 0) || selected.Select(x => x.ChapterNumber).Distinct().Count() != selected.Length ||
            selected.Select(x => x.Candidate.ChapterId).Distinct().Count() != selected.Length ||
            selected.Select(x => Path.GetFullPath(x.Candidate.Path)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Length)
            throw new InvalidDataException("Số chương/ID/đường dẫn nguồn nhập trùng hoặc không hợp lệ.");
        if (selected.Any(x => target.Manifest.Chapters.Any(c => c.ChapterId == x.Candidate.ChapterId || c.DeletedAt is null && c.Number == x.ChapterNumber)))
            throw new InvalidOperationException("Chương nhập trùng ID (kể cả xóa mềm) hoặc số chương trong target.");

        string? backup = null; var created = new HashSet<Guid>(); Guid? transactionId = null; var loadedPaths = new Dictionary<Guid, string>();
        LegacyImportReport Report(LegacyImportChapterState committed, bool rollbackComplete, string? error) => new(backup, transactionId, rollbackComplete,
            selected.Select(x => new LegacyImportChapterOutcome(x.Candidate.ChapterId, x.ChapterNumber, Path.GetFullPath(x.Candidate.Path),
                target.ChapterStore.GetPath(x.Candidate.ChapterId), committed == LegacyImportChapterState.Imported ? committed :
                    created.Contains(x.Candidate.ChapterId) ? committed : LegacyImportChapterState.NotStarted)
                    { LoadedSourcePath = loadedPaths.GetValueOrDefault(x.Candidate.ChapterId) }).ToArray(), error);
        try
        {
            var sourceCopies = new List<SourceCopy>(); var chapters = new List<(ChapterDocument Source, int Number)>();
            foreach (var item in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ProjectRepository.EnsureDestinationEmptyAsync(target.ChapterStore.GetPath(item.Candidate.ChapterId), cancellationToken).ConfigureAwait(false);
                string path = Path.GetFullPath(item.Candidate.Path);
                byte[]? primary = await RecoveryFiles.ReadOptionalAsync(path, cancellationToken).ConfigureAwait(false);
                byte[]? oldBackup = await RecoveryFiles.ReadOptionalAsync(path + ".bak", cancellationToken).ConfigureAwait(false);
                // Decode the exact immutable bytes included in the source rescue, not a later read of legacy files.
                ChapterDocument source;
                string loadedPath = path;
                try { source = primary is null ? throw new FileNotFoundException("Primary thiếu", path) : ChapterFileStore.DeserializeDocument(primary); }
                catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or JsonException or ArgumentException)
                {
                    if (oldBackup is null) throw new InvalidDataException("Nguồn không có backup hợp lệ để nhập.", ex);
                    source = ChapterFileStore.DeserializeDocument(oldBackup);
                    loadedPath = path + ".bak";
                }
                if (source.Id != item.Candidate.ChapterId) throw new InvalidDataException("ID source đã đổi sau scan; đọc lại preview trước khi nhập.");
                loadedPaths[source.Id] = loadedPath;
                if (primary is not null) sourceCopies.Add(new(path, primary));
                if (oldBackup is not null) sourceCopies.Add(new(path + ".bak", oldBackup));
                chapters.Add((source, item.ChapterNumber));
            }
            backup = Path.Combine(target.RootDirectory, "Backups", "legacy-before-import-" + Guid.NewGuid().ToString("N") + ".zip");
            await CreateSourceBackupAsync(sourceCopies, backup, cancellationToken).ConfigureAwait(false);
            transactionId = await repository.ImportChaptersTransactionAsync(target, chapters, backup, id => created.Add(id), VerifyEquivalent, cancellationToken).ConfigureAwait(false);
            return new(selected.Length, backup, selected.Select(x => x.Candidate.ChapterId).ToArray())
                { Report = Report(LegacyImportChapterState.Imported, false, null) };
        }
        catch (ProjectOperationInterruptedException) { throw; } // Simulated crash: durable journal is reconciled on reopen.
        catch (OperationCanceledException ex)
        {
            transactionId = ex.Data["ProjectTransactionId"] as Guid?;
            throw new LegacyImportCanceledException(ex, Report(LegacyImportChapterState.RolledBack, true, ex.Message), cancellationToken);
        }
        catch (Exception ex)
        {
            transactionId = ex is ProjectTransactionException transaction ? transaction.TransactionId : ex.Data["ProjectTransactionId"] as Guid?;
            bool complete = created.Count == 0 && ex is not ProjectTransactionException || ex.Data["RollbackComplete"] is true;
            var report = Report(complete ? LegacyImportChapterState.RolledBack : LegacyImportChapterState.NeedsRecovery, complete, ex.Message);
            throw new LegacyImportException(complete
                ? "Import chưa hoàn tất; target đã giữ/phục hồi trạng thái trước thao tác, nguồn legacy và ZIP cứu hộ được giữ. Có thể thử lại."
                : "Import chưa hoàn tất và rollback cần phục hồi thủ công; giữ journal/source/ZIP, mở báo cáo recovery trước khi ghi tiếp.", ex, report);
        }
    }

    private static void VerifyEquivalent(ChapterDocument expected, ChapterDocument imported)
    {
        if (JsonSerializer.Serialize(expected, DocumentJson.Options) != JsonSerializer.Serialize(imported, DocumentJson.Options))
            throw new InvalidDataException("Nội dung import không tương đương expected (đã áp dụng chuyển ProjectId/số chương có chủ đích); chưa commit, rollback toàn đợt.");
    }

    private static async Task CreateSourceBackupAsync(IReadOnlyList<SourceCopy> sources, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    var entry = archive.CreateEntry($"{i:D4}-{Path.GetFileName(sources[i].Path)}", CompressionLevel.Optimal);
                    await using var stream = entry.Open(); await stream.WriteAsync(sources[i].Bytes, token).ConfigureAwait(false);
                }
                await using var metadata = archive.CreateEntry("legacy-source-manifest.json").Open();
                await JsonSerializer.SerializeAsync(metadata, sources.Select((source, i) => new { SourcePath = source.Path,
                    Entry = $"{i:D4}-{Path.GetFileName(source.Path)}", Sha256 = RecoveryFiles.Hash(source.Bytes), Length = source.Bytes.Length }), DocumentJson.Options, token).ConfigureAwait(false);
            }
            await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true);
        }
        using var verified = ZipFile.OpenRead(destination);
        for (int i = 0; i < sources.Count; i++)
        {
            await using var input = verified.GetEntry($"{i:D4}-{Path.GetFileName(sources[i].Path)}")!.Open();
            using var bytes = new MemoryStream(); await input.CopyToAsync(bytes, token).ConfigureAwait(false);
            if (RecoveryFiles.Hash(bytes.ToArray()) != RecoveryFiles.Hash(sources[i].Bytes))
                throw new InvalidDataException("ZIP nguồn không khớp checksum; chưa ghi target.");
        }
    }
    private sealed record SourceCopy(string Path, byte[] Bytes);
}
