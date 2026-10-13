using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;

namespace QuickTranslator.AiEditor.Core.Projects;

public enum ProjectCatalogApprovalState { NotApproved, Approved }
public enum ProjectCatalogApprovalFilter { All, NotApproved, Approved }
public enum ProjectCatalogDeletionFilter { Active, Deleted, All }
public enum ProjectCatalogSort { ReadingOrder, Number, Title, Updated }

public sealed record ProjectCatalogQuery(
    string Search = "",
    ProjectChapterState? State = null,
    ProjectCatalogApprovalFilter Approval = ProjectCatalogApprovalFilter.All,
    ProjectCatalogDeletionFilter Deletion = ProjectCatalogDeletionFilter.Active,
    string? VolumeTitle = null,
    ProjectCatalogSort Sort = ProjectCatalogSort.ReadingOrder);

public sealed record ProjectCatalogChapter(
    Guid ChapterId,
    int Number,
    long DisplayOrder,
    string VietnameseTitle,
    string ChineseTitle,
    string? VolumeTitle,
    ProjectChapterState TranslationState,
    ProjectCatalogApprovalState ApprovalState,
    int SegmentCount,
    int CompletedSegmentCount,
    DateTimeOffset UpdatedAt,
    bool IsDeleted)
{
    public int OriginalChapterNumber => Number;
    public double Progress => SegmentCount == 0 ? 0 : (double)CompletedSegmentCount / SegmentCount;
}

public sealed record ProjectCatalogSnapshot(
    Guid ProjectId,
    string Revision,
    DateTimeOffset ManifestUpdatedAt,
    ImmutableArray<ProjectCatalogChapter> Chapters)
{
    public int ActiveCount => Chapters.Count(chapter => !chapter.IsDeleted);
    public int DeletedCount => Chapters.Count(chapter => chapter.IsDeleted);
}

public sealed class ProjectCatalogChangedEventArgs(Guid projectId, string operation) : EventArgs
{
    public Guid ProjectId { get; } = projectId;
    public string Operation { get; } = operation;
}

/// <summary>
/// In-memory read model derived exclusively from ProjectManifest metadata. It never reads chapter bodies
/// and never writes back to the manifest.
/// </summary>
public sealed class ProjectCatalog : IDisposable
{
    private readonly ProjectRepository _repository;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, ProjectCatalogSnapshot> _cache = [];
    private bool _disposed;

    public ProjectCatalog(ProjectRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _repository.ManifestCommitted += OnManifestCommitted;
    }

    public event EventHandler<ProjectCatalogChangedEventArgs>? Changed;

    public ProjectCatalogSnapshot GetSnapshot(ProjectSession session)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(session);
        ProjectManifest manifest = session.Manifest;
        lock (_sync)
        {
            if (_cache.TryGetValue(manifest.ProjectId, out ProjectCatalogSnapshot? current) &&
                current.ManifestUpdatedAt == manifest.UpdatedAt &&
                current.Chapters.Length == manifest.Chapters.Count)
                return current;
            string revision = ComputeRevision(manifest);
            if (_cache.TryGetValue(manifest.ProjectId, out ProjectCatalogSnapshot? cached) && cached.Revision == revision)
                return cached;
            ProjectCatalogSnapshot rebuilt = Build(manifest, revision);
            _cache[manifest.ProjectId] = rebuilt;
            return rebuilt;
        }
    }

    public ImmutableArray<ProjectCatalogChapter> Query(ProjectSession session, ProjectCatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        IEnumerable<ProjectCatalogChapter> chapters = GetSnapshot(session).Chapters;
        chapters = query.Deletion switch
        {
            ProjectCatalogDeletionFilter.Active => chapters.Where(chapter => !chapter.IsDeleted),
            ProjectCatalogDeletionFilter.Deleted => chapters.Where(chapter => chapter.IsDeleted),
            _ => chapters
        };
        string search = query.Search.Trim();
        if (search.Length > 0)
            chapters = chapters.Where(chapter => chapter.Number.ToString().Contains(search, StringComparison.OrdinalIgnoreCase) ||
                chapter.VietnameseTitle.Contains(search, StringComparison.CurrentCultureIgnoreCase) ||
                chapter.ChineseTitle.Contains(search, StringComparison.OrdinalIgnoreCase));
        if (query.State is ProjectChapterState state)
            chapters = chapters.Where(chapter => chapter.TranslationState == state);
        chapters = query.Approval switch
        {
            ProjectCatalogApprovalFilter.Approved => chapters.Where(chapter => chapter.ApprovalState == ProjectCatalogApprovalState.Approved),
            ProjectCatalogApprovalFilter.NotApproved => chapters.Where(chapter => chapter.ApprovalState == ProjectCatalogApprovalState.NotApproved),
            _ => chapters
        };
        if (!string.IsNullOrWhiteSpace(query.VolumeTitle))
            chapters = chapters.Where(chapter => string.Equals(chapter.VolumeTitle, query.VolumeTitle,
                StringComparison.CurrentCultureIgnoreCase));
        chapters = query.Sort switch
        {
            ProjectCatalogSort.Number => chapters.OrderBy(chapter => chapter.Number).ThenBy(chapter => chapter.DisplayOrder),
            ProjectCatalogSort.Title => chapters.OrderBy(chapter => chapter.VietnameseTitle,
                StringComparer.CurrentCultureIgnoreCase).ThenBy(chapter => chapter.DisplayOrder),
            ProjectCatalogSort.Updated => chapters.OrderByDescending(chapter => chapter.UpdatedAt).ThenBy(chapter => chapter.DisplayOrder),
            _ => chapters.OrderBy(chapter => chapter.DisplayOrder)
        };
        return chapters.ToImmutableArray();
    }

    public void Invalidate(Guid projectId)
    {
        lock (_sync) _cache.Remove(projectId);
    }

    private void OnManifestCommitted(object? sender, ProjectCatalogChangedEventArgs change)
    {
        Invalidate(change.ProjectId);
        EventHandler<ProjectCatalogChangedEventArgs>? handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler<ProjectCatalogChangedEventArgs> handler in handlers.GetInvocationList().Cast<EventHandler<ProjectCatalogChangedEventArgs>>())
        {
            try { handler(this, change); }
            catch (Exception ex) { Trace.TraceError($"ProjectCatalog subscriber failed after {change.Operation}: {ex}"); }
        }
    }

    private static ProjectCatalogSnapshot Build(ProjectManifest manifest, string revision)
    {
        var manifestIndexes = manifest.Chapters.Select((chapter, index) => (chapter, index)).ToArray();
        Dictionary<Guid, long> readingOrder = manifestIndexes.Where(item => item.chapter.DeletedAt is null)
            .OrderBy(item => item.chapter.Number).ThenBy(item => item.index)
            .Select((item, index) => (item.chapter.ChapterId, Order: (long)index + 1))
            .ToDictionary(item => item.ChapterId, item => item.Order);
        long deletedOrder = readingOrder.Count;
        foreach ((ProjectChapterEntry chapter, int _) in manifestIndexes.Where(item => item.chapter.DeletedAt is not null)
                     .OrderBy(item => item.chapter.Number).ThenBy(item => item.index))
            readingOrder[chapter.ChapterId] = ++deletedOrder;

        ImmutableArray<ProjectCatalogChapter> chapters = manifestIndexes.Select(item => new ProjectCatalogChapter(
            item.chapter.ChapterId,
            item.chapter.Number,
            readingOrder[item.chapter.ChapterId],
            item.chapter.VietnameseTitle,
            item.chapter.ChineseTitle,
            null,
            item.chapter.State,
            item.chapter.State == ProjectChapterState.Approved
                ? ProjectCatalogApprovalState.Approved : ProjectCatalogApprovalState.NotApproved,
            item.chapter.SegmentCount,
            item.chapter.CompletedSegmentCount,
            item.chapter.UpdatedAt,
            item.chapter.DeletedAt is not null)).ToImmutableArray();
        return new(manifest.ProjectId, revision, manifest.UpdatedAt, chapters);
    }

    private static string ComputeRevision(ProjectManifest manifest) =>
        Convert.ToHexString(SHA256.HashData(ProjectRepository.SerializeManifest(manifest)));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _repository.ManifestCommitted -= OnManifestCommitted;
        lock (_sync) _cache.Clear();
    }
}
