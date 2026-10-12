using System.IO.Compression;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed record LegacyChapterCandidate(string Path, Guid ChapterId, string Title, DateTimeOffset UpdatedAt,
    int SegmentCount, long FileSize, string? Error = null);
public sealed record LegacyChapterSelection(LegacyChapterCandidate Candidate, int ChapterNumber);
public sealed record LegacyImportResult(int ImportedCount, string SourceBackupPath, IReadOnlyList<Guid> ChapterIds);

public sealed class LegacyProjectImporter(ProjectRepository repository)
{
    public async Task<IReadOnlyList<LegacyChapterCandidate>> ScanAsync(string legacyDirectory,
        CancellationToken cancellationToken = default)
    {
        var store = new ChapterFileStore(legacyDirectory);
        var results = new List<LegacyChapterCandidate>();
        foreach (string path in store.EnumerateChapterPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var chapter = (await store.LoadAsync(path, cancellationToken).ConfigureAwait(false)).Chapter;
                results.Add(new(path, chapter.Id, chapter.Title, chapter.UpdatedAt, chapter.Segments.Count, new FileInfo(path).Length));
            }
            catch (Exception ex) { results.Add(new(path, Guid.Empty, Path.GetFileName(path), default, 0, new FileInfo(path).Length, ex.Message)); }
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
        if (selected.GroupBy(x => x.ChapterNumber).Any(x => x.Count() > 1) || selected.Any(x => x.ChapterNumber <= 0))
            throw new InvalidDataException("Số chương nhập phải dương và không trùng nhau.");
        if (selected.Any(x => target.Manifest.Chapters.Any(c => c.DeletedAt is null &&
            (c.Number == x.ChapterNumber || c.ChapterId == x.Candidate.ChapterId))))
            throw new InvalidOperationException("Chương nhập trùng ID hoặc số chương với dự án đích.");

        string backup = Path.Combine(target.RootDirectory, "Backups",
            $"legacy-before-import-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
        await CreateSourceBackupAsync(selected.Select(x => x.Candidate.Path), backup, cancellationToken).ConfigureAwait(false);
        var before = ProjectRepository.Clone(target.Manifest);
        var added = new List<Guid>();
        try
        {
            foreach (var item in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceStore = new ChapterFileStore(Path.GetDirectoryName(item.Candidate.Path)!);
                var source = (await sourceStore.LoadAsync(item.Candidate.Path, cancellationToken).ConfigureAwait(false)).Chapter;
                await repository.AddChapterAsync(target, source, item.ChapterNumber, source.ChineseTitle, cancellationToken).ConfigureAwait(false);
                var verify = (await repository.LoadChapterAsync(target, source.Id, cancellationToken).ConfigureAwait(false)).Chapter;
                VerifyEquivalent(source, verify);
                added.Add(source.Id);
            }
            return new(added.Count, backup, added);
        }
        catch
        {
            foreach (Guid id in added)
            {
                TryDelete(target.ChapterStore.GetPath(id)); TryDelete(target.ChapterStore.GetPath(id) + ".bak");
            }
            target.Manifest = before;
            await repository.SaveManifestAsync(target, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void VerifyEquivalent(ChapterDocument source, ChapterDocument imported)
    {
        if (!string.Equals(source.OriginalText, imported.OriginalText, StringComparison.Ordinal) ||
            source.Segments.Count != imported.Segments.Count ||
            source.Segments.Sum(x => x.Attempts.Count) != imported.Segments.Sum(x => x.Attempts.Count) ||
            source.Segments.Sum(x => x.Approvals.Count) != imported.Segments.Sum(x => x.Approvals.Count) ||
            source.Segments.Sum(x => x.DraftHistory.Count) != imported.Segments.Sum(x => x.DraftHistory.Count))
            throw new InvalidDataException("Kiểm tra sau nhập không khớp; đã dừng và hoàn tác dự án đích.");
    }

    private static async Task CreateSourceBackupAsync(IEnumerable<string> sources, string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            foreach (string source in sources.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(Path.GetFileName(source), CompressionLevel.Optimal);
                await using var input = File.OpenRead(source); await using var output = entry.Open();
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                string oldBackup = source + ".bak";
                if (File.Exists(oldBackup))
                {
                    var backupEntry = archive.CreateEntry(Path.GetFileName(oldBackup), CompressionLevel.Optimal);
                    await using var backupInput = File.OpenRead(oldBackup); await using var backupOutput = backupEntry.Open();
                    await backupInput.CopyToAsync(backupOutput, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        using var verify = ZipFile.OpenRead(destination);
        if (verify.Entries.Count < sources.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new InvalidDataException("Không xác minh được bản sao lưu nguồn trước khi nhập.");
        byte[] buffer = new byte[8192];
        foreach (var entry in verify.Entries)
        {
            await using var input = entry.Open();
            while (await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0) { }
        }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
