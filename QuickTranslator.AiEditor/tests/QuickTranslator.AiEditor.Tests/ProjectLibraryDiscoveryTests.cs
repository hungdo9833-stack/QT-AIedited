using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ProjectLibraryDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-library-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void The_library_keeps_every_book_not_only_the_last_forty()
    {
        var library = new ProjectLibrary();
        for (int i = 0; i < 60; i++)
            library.RecentProjects.Add(new RecentProject { ProjectId = Guid.NewGuid(), ManifestPath = $"C:\\b{i}\\b.qtproj" });
        var repository = new ProjectRepository();
        ProjectSession session = repository.CreateAsync(_root, "Mới").GetAwaiter().GetResult();

        ProjectLibraryStore.Remember(library, session);

        Assert.Equal(61, library.RecentProjects.Count);
    }

    [Fact]
    public async Task Books_beside_a_listed_one_that_the_list_lost_come_back()
    {
        var repository = new ProjectRepository();
        ProjectSession listed = await repository.CreateAsync(Path.Combine(_root, "Truyện A"), "Truyện A");
        ProjectSession lost = await repository.CreateAsync(Path.Combine(_root, "Truyện B"), "Truyện B");
        var library = new ProjectLibrary { DefaultProjectsDirectory = Path.Combine(_root, "none") };
        ProjectLibraryStore.Remember(library, listed);

        int added = await ProjectLibraryStore.DiscoverAsync(library);

        Assert.Equal(1, added);
        RecentProject found = Assert.Single(library.RecentProjects, x => x.ProjectId == lost.Manifest.ProjectId);
        Assert.Equal("Truyện B", found.Title);
        Assert.Equal(0, await ProjectLibraryStore.DiscoverAsync(library));
    }
}
