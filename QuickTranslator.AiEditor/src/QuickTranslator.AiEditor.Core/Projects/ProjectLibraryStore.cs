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
        if (!File.Exists(PathName)) return new();
        try
        {
            await using var stream = new FileStream(PathName, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            var library = await JsonSerializer.DeserializeAsync<ProjectLibrary>(stream, DocumentJson.Options, cancellationToken).ConfigureAwait(false)
                ?? new ProjectLibrary();
            Validate(library); RefreshMissing(library); return library;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            string backup = PathName + ".bak";
            if (!File.Exists(backup)) throw new InvalidDataException("Không đọc được danh sách dự án gần đây. Tệp dự án thật không bị ảnh hưởng.", ex);
            await using var stream = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.Asynchronous);
            var library = await JsonSerializer.DeserializeAsync<ProjectLibrary>(stream, DocumentJson.Options, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Bản dự phòng danh sách dự án bị trống.", ex);
            Validate(library); RefreshMissing(library); return library;
        }
    }

    public async Task SaveAsync(ProjectLibrary library, CancellationToken cancellationToken = default)
    {
        Validate(library);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(library,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await AtomicLocalFile.WriteAsync(PathName, bytes, cancellationToken).ConfigureAwait(false);
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
        library.RecentProjects = library.RecentProjects.OrderByDescending(x => x.LastOpenedAt).Take(40).ToList();
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
