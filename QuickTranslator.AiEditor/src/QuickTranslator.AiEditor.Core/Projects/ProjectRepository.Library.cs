using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed record ProjectLibrarySummary(Guid ProjectId, string VietnameseTitle, string ChineseTitle,
    string Author, ProjectStatus Status, int ChapterCount, int CompletedChapterCount, int CompletedSegmentCount,
    int TotalSegmentCount, string? CurrentChapterTitle, string? CoverImagePath, DateTimeOffset UpdatedAt,
    bool RecoveredFromBackup)
{
    public double Progress => TotalSegmentCount == 0 ? 0 : (double)CompletedSegmentCount / TotalSegmentCount;
}

public sealed partial class ProjectRepository
{
    public static async Task<ProjectLibrarySummary> ReadLibrarySummaryAsync(string manifestPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullPath = Path.GetFullPath(manifestPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".qtproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hãy chọn đúng tệp dự án .qtproj.");
        RecoveryRead<ProjectManifest> read = await RecoveryFiles.ReadAsync(fullPath, DecodeManifest,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ProjectManifest manifest = read.Value;
        ProjectChapterEntry[] active = manifest.Chapters.Where(chapter => chapter.DeletedAt is null).ToArray();
        string? cover = null;
        if (!string.IsNullOrWhiteSpace(manifest.CoverImagePath))
        {
            string candidate = ProjectPath.Resolve(Path.GetDirectoryName(fullPath)!, manifest.CoverImagePath);
            if (File.Exists(candidate)) cover = candidate;
        }
        return new(manifest.ProjectId, manifest.VietnameseTitle, manifest.ChineseTitle, manifest.Author,
            manifest.Status, active.Length, active.Count(chapter => chapter.State == ProjectChapterState.Approved),
            active.Sum(chapter => chapter.CompletedSegmentCount), active.Sum(chapter => chapter.SegmentCount),
            active.SingleOrDefault(chapter => chapter.ChapterId == manifest.CurrentChapterId)?.VietnameseTitle,
            cover, manifest.UpdatedAt, read.Info.Source == RecoverySource.Backup);
    }
}
