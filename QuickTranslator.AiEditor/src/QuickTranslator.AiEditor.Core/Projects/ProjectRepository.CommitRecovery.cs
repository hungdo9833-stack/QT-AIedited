using System.Security.Cryptography;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed partial class ProjectRepository
{
    private static async Task RecoverInterruptedChapterMovesAsync(ProjectSession session, CancellationToken token)
    {
        var referenced = session.Manifest.Chapters.Select(x => session.Resolve(x.RelativePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectChapterEntry entry in session.Manifest.Chapters)
        {
            token.ThrowIfCancellationRequested();
            string expected = session.Resolve(entry.RelativePath);
            if (File.Exists(expected)) continue;
            IEnumerable<string> candidates = entry.DeletedAt is null
                ? Directory.EnumerateFiles(session.Resolve("Deleted"), $"*-{entry.ChapterId:D}.qtaie.json")
                : new[] { session.ChapterStore.GetPath(entry.ChapterId) };
            var matches = new List<string>();
            foreach (string candidate in candidates.Where(path => !referenced.Contains(path) && File.Exists(path)))
            {
                // A damaged candidate is not this chapter's recovery file; it must never stop the project from opening.
                try
                {
                    ChapterDocument chapter = await ChapterFileStore.ReadVerifiedFileAsync(candidate, token).ConfigureAwait(false);
                    if (chapter.Id == entry.ChapterId && chapter.ProjectId == session.Manifest.ProjectId)
                        matches.Add(candidate);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { System.Diagnostics.Trace.TraceWarning($"Skipped recovery candidate {candidate}: {error.Message}"); }
            }
            // Ambiguous or failed recovery leaves every file in place for manual resolution; opening still succeeds.
            if (matches.Count > 1)
            {
                System.Diagnostics.Trace.TraceWarning($"Multiple recovery files exist for chapter {entry.ChapterId:D}; none was moved.");
                continue;
            }
            if (matches.Count == 1)
            {
                // The persisted manifest decides which side of an interrupted move is authoritative.
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
                    File.Move(matches[0], expected);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { System.Diagnostics.Trace.TraceWarning($"Could not recover chapter {entry.ChapterId:D}: {error.Message}"); }
            }
        }
    }

    private static string ImportReceiptPath(string chapterPath) => chapterPath + ".import-sha256";

    internal static async Task<ChapterDocument?> ReadPendingImportAsync(ProjectSession session, Guid chapterId,
        CancellationToken token)
    {
        string path = session.ChapterStore.GetPath(chapterId);
        if (!File.Exists(path) || !File.Exists(ImportReceiptPath(path)) ||
            session.Manifest.Chapters.Any(entry => string.Equals(session.Resolve(entry.RelativePath), path,
                StringComparison.OrdinalIgnoreCase)))
            return null;
        byte[] receipt = await File.ReadAllBytesAsync(ImportReceiptPath(path), token).ConfigureAwait(false);
        byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        if (!SHA256.HashData(bytes).AsSpan().SequenceEqual(receipt)) return null;
        ChapterDocument chapter = ChapterFileStore.DeserializeDocument(bytes);
        return chapter.Id == chapterId && chapter.ProjectId == session.Manifest.ProjectId ? chapter : null;
    }

    private async Task WritePendingImportAsync(ProjectSession session, ChapterDocument chapter, CancellationToken token)
    {
        string path = session.ChapterStore.GetPath(chapter.Id);
        byte[] bytes = ChapterFileStore.SerializeDocument(chapter);
        string stage = path + ".import-stage-" + Guid.NewGuid().ToString("N");
        try
        {
            await SimpleFileStorage.WriteBytesAsync(stage, bytes, token).ConfigureAwait(false);
            await SimpleFileStorage.WriteBytesAsync(ImportReceiptPath(path), SHA256.HashData(bytes), token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // Publish a complete, flushed file without ever replacing a destination created by someone else.
            File.Move(stage, path);
        }
        finally { TryDeleteUnreferencedFile(session, stage); }
    }

    private static void TryDeleteUnreferencedFile(ProjectSession session, string path,
        IReadOnlySet<string>? referencedPaths = null)
    {
        if (!File.Exists(path)) return;
        if (referencedPaths?.Contains(path) ?? session.Manifest.Chapters.Any(entry =>
            string.Equals(session.Resolve(entry.RelativePath), path, StringComparison.OrdinalIgnoreCase))) return;
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning($"Could not clean up committed project file {path}: {error.Message}"); }
    }
}
