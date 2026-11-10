using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed class ProjectLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public string DefaultProjectsDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QuickTranslator AI Editor");
    public List<RecentProject> RecentProjects { get; set; } = [];
}

public sealed class RecentProject
{
    public Guid ProjectId { get; set; }
    public string Title { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public DateTimeOffset LastOpenedAt { get; set; }
    public bool Missing { get; set; }
}

public sealed class ProjectLibraryStore(string? path = null)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "Projects", "library.json");
    public string PathName { get; } = System.IO.Path.GetFullPath(path ?? DefaultPath);

    public async Task<ProjectLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        ProjectLibrary result = await SimpleFileStorage.ReadOptionalAsync(PathName, Decode, () => new ProjectLibrary(), cancellationToken).ConfigureAwait(false);
        RefreshMissing(result); return result;
    }
    private static ProjectLibrary Decode(byte[] bytes) => JsonFileCodec.DecodeDirect<ProjectLibrary>(bytes, Validate, "schemaVersion", "recentProjects");
    public async Task SaveAsync(ProjectLibrary library, CancellationToken cancellationToken = default)
    {
        Validate(library);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(library,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await SimpleFileStorage.WriteAsync(PathName, bytes, Decode, cancellationToken).ConfigureAwait(false);
    }

    public static void Remember(ProjectLibrary library, ProjectSession session)
    {
        var item = library.RecentProjects.FirstOrDefault(x => x.ProjectId == session.Manifest.ProjectId);
        item ??= new RecentProject { ProjectId = session.Manifest.ProjectId };
        if (!library.RecentProjects.Contains(item)) library.RecentProjects.Add(item);
        item.Title = session.Manifest.VietnameseTitle;
        item.ManifestPath = session.ManifestPath;
        item.LastOpenedAt = DateTimeOffset.UtcNow;
        item.Missing = false;
        // The library is the list of every book, not a recent-files menu: a cap of 40 once dropped 110 of 150 books.
        library.RecentProjects = library.RecentProjects.OrderByDescending(x => x.LastOpenedAt).ToList();
    }

    /// <summary>
    /// Adds the projects the list lost: every *.qtproj in a folder beside a listed book (the books' common parent
    /// folders) or in the default projects folder. Returns how many were added.
    /// </summary>
    public static async Task<int> DiscoverAsync(ProjectLibrary library, CancellationToken cancellationToken = default)
    {
        var known = library.RecentProjects.Select(x => SafeFullPath(x.ManifestPath)).Where(x => x is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = library.RecentProjects.Select(x => x.ProjectId).ToHashSet();
        IEnumerable<string> roots = library.RecentProjects
            .Select(x => Path.GetDirectoryName(Path.GetDirectoryName(SafeFullPath(x.ManifestPath) ?? "")))
            .Append(library.DefaultProjectsDirectory)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        int added = 0;
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> manifests;
            try
            {
                manifests = Directory.EnumerateDirectories(root)
                    .SelectMany(folder => Directory.EnumerateFiles(folder, "*.qtproj", SearchOption.TopDirectoryOnly)).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string manifestPath in manifests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!known.Add(manifestPath)) continue;
                try
                {
                    ProjectManifest manifest = await ProjectRepository.ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                    if (!ids.Add(manifest.ProjectId)) continue;
                    library.RecentProjects.Add(new RecentProject
                    {
                        ProjectId = manifest.ProjectId, Title = manifest.VietnameseTitle, ManifestPath = manifestPath,
                        LastOpenedAt = manifest.LastOpenedAt
                    });
                    added++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { } // Not a readable project: leave it out.
            }
        }
        if (added > 0) library.RecentProjects = library.RecentProjects.OrderByDescending(x => x.LastOpenedAt).ToList();
        return added;
    }

    private static string? SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    public static void RemoveShortcut(ProjectLibrary library, Guid projectId) =>
        library.RecentProjects.RemoveAll(x => x.ProjectId == projectId);

    public static void Relocate(ProjectLibrary library, Guid projectId, string newManifestPath)
    {
        var item = library.RecentProjects.SingleOrDefault(x => x.ProjectId == projectId)
            ?? throw new KeyNotFoundException("Dự án không còn trong danh sách gần đây.");
        item.ManifestPath = System.IO.Path.GetFullPath(newManifestPath); item.Missing = !File.Exists(item.ManifestPath);
    }

    public static IReadOnlyList<RecentProject> Search(ProjectLibrary library, string? query) =>
        library.RecentProjects.Where(x => string.IsNullOrWhiteSpace(query) ||
            x.Title.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
            x.ManifestPath.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(x => x.LastOpenedAt).ToArray();

    private static void RefreshMissing(ProjectLibrary library)
    {
        foreach (var item in library.RecentProjects) item.Missing = !File.Exists(item.ManifestPath);
    }

    private static void Validate(ProjectLibrary library)
    {
        if (library.SchemaVersion != 1 || library.RecentProjects is null || library.DefaultProjectsDirectory is null)
            throw new InvalidDataException("Danh sách dự án gần đây sai định dạng.");
        if (library.RecentProjects.Any(x => x.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(x.ManifestPath)) ||
            library.RecentProjects.GroupBy(x => x.ProjectId).Any(x => x.Count() > 1))
            throw new InvalidDataException("Danh sách dự án gần đây có mục không hợp lệ.");
    }
}
