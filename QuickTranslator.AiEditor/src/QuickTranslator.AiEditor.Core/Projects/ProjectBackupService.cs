using System.IO.Compression;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed record ProjectBackupResult(string ArchivePath, int FileCount, long UncompressedBytes);

public sealed class ProjectBackupService
{
    public async Task<ProjectBackupResult> CreateAsync(ProjectSession session, string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        destinationPath ??= Path.Combine(session.RootDirectory, "Backups",
            $"{SafeName(session.Manifest.VietnameseTitle)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip");
        destinationPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath)) throw new IOException("Tệp sao lưu đã tồn tại; ứng dụng không ghi đè.");
        string temporary = destinationPath + ".tmp-" + Guid.NewGuid().ToString("N");
        int count = 0; long bytes = 0;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (string file in Directory.EnumerateFiles(session.RootDirectory, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relative = Path.GetRelativePath(session.RootDirectory, file).Replace('\\', '/');
                    if (relative.StartsWith("Backups/", StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith("Cache/", StringComparison.OrdinalIgnoreCase) ||
                        relative.Contains(".tmp-", StringComparison.OrdinalIgnoreCase)) continue;
                    var info = new FileInfo(file); bytes += info.Length; count++;
                    var entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
                    entry.LastWriteTime = info.LastWriteTimeUtc;
                    await using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read,
                        8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var target = entry.Open();
                    await input.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                }
            }
            await VerifyArchiveAsync(temporary, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, destinationPath);
            return new(destinationPath, count, bytes);
        }
        finally { TryDelete(temporary); }
    }

    public async Task<ProjectSession> RestoreAsync(string archivePath, string newProjectDirectory,
        CancellationToken cancellationToken = default)
    {
        archivePath = Path.GetFullPath(archivePath); newProjectDirectory = Path.GetFullPath(newProjectDirectory);
        if (!File.Exists(archivePath)) throw new FileNotFoundException("Không tìm thấy tệp sao lưu.", archivePath);
        if (Directory.Exists(newProjectDirectory) && Directory.EnumerateFileSystemEntries(newProjectDirectory).Any())
            throw new IOException("Thư mục khôi phục phải mới hoặc đang trống để tránh ghi đè dữ liệu.");
        await VerifyArchiveAsync(archivePath, cancellationToken).ConfigureAwait(false);
        string parent = Path.GetDirectoryName(newProjectDirectory)!;
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".qt-restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue;
                string destination = SafeArchiveDestination(staging, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var input = entry.Open();
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
            }
            string manifest = Directory.GetFiles(staging, "*.qtproj", SearchOption.TopDirectoryOnly).SingleOrDefault()
                ?? throw new InvalidDataException("Bản sao lưu không có đúng một tệp .qtproj ở thư mục gốc.");
            var opened = await new ProjectRepository().OpenAsync(manifest, cancellationToken).ConfigureAwait(false);
            foreach (var chapter in opened.Session.Manifest.Chapters.Where(x => x.DeletedAt is null))
                _ = await opened.Session.ChapterStore.LoadAsync(opened.Session.Resolve(chapter.RelativePath), cancellationToken).ConfigureAwait(false);
            if (Directory.Exists(newProjectDirectory)) Directory.Delete(newProjectDirectory);
            Directory.Move(staging, newProjectDirectory);
            string restoredManifest = Path.Combine(newProjectDirectory, Path.GetFileName(manifest));
            return (await new ProjectRepository().OpenAsync(restoredManifest, cancellationToken).ConfigureAwait(false)).Session;
        }
        catch { TryDeleteDirectory(staging); throw; }
    }

    public static async Task VerifyArchiveAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count(x =>
            string.Equals(Path.GetExtension(x.FullName), ".qtproj", StringComparison.OrdinalIgnoreCase) &&
            !x.FullName.Contains('/')) != 1)
            throw new InvalidDataException("Bản sao lưu không chứa đúng một manifest dự án.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] buffer = new byte[8192];
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = SafeArchiveDestination(Path.GetTempPath(), entry.FullName);
            if (!names.Add(entry.FullName)) throw new InvalidDataException("Bản sao lưu có tên tệp trùng.");
            if (string.IsNullOrEmpty(entry.Name)) continue;
            await using var stream = entry.Open();
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0) { }
        }
    }

    private static string SafeArchiveDestination(string root, string entry)
    {
        string rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string destination = Path.GetFullPath(Path.Combine(root, entry.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Bản sao lưu chứa đường dẫn không an toàn.");
        return destination;
    }
    private static string SafeName(string value) => string.Concat(value.Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x));
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceError($"Không dọn được artifact backup tạm {path}: {ex}"); }
    }
    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceError($"Không dọn được staging restore {path}: {ex}"); }
    }
}
