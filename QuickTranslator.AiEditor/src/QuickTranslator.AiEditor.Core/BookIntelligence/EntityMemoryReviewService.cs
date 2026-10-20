using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public static class EntityMemoryProjectInputs
{
    public static async Task<ProjectSession> ReloadSessionAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        var manifest = await ProjectRepository.ReadManifestAsync(session.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (manifest.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Manifest now belongs to another Project.");
        return new ProjectSession(session.ManifestPath, manifest); // read-only; no OpenAsync/synchronization writes
    }
    public static async Task<IReadOnlyList<CensusSource>> LoadSourcesAsync(ProjectSession session, ProjectRepository repository, CancellationToken cancellationToken = default)
    {
        session = await ReloadSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var result = new List<CensusSource>();
        foreach (var entry in session.Manifest.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number).ThenBy(x => x.ChapterId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapter = (await repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken).ConfigureAwait(false)).Chapter;
            if (chapter.Id != entry.ChapterId || chapter.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Memory source chapter belongs to another Project.");
            result.Add(new(entry.ChapterId, entry.Number, chapter.OriginalText ?? ""));
        }
        if (result.Select(x => x.ChapterId).Distinct().Count() != result.Count || result.Select(x => x.ChapterNumber).Distinct().Count() != result.Count)
            throw new InvalidDataException("Duplicate active chapter identity/number.");
        return result;
    }
}
