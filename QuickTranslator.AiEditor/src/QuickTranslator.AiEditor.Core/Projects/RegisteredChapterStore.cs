using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

// Editor services may write only chapters registered by ProjectRepository.
public sealed class RegisteredChapterStore(ProjectSession session, ProjectRepository? repository = null) : IChapterStore
{
    private readonly ProjectRepository _repository = repository ?? new ProjectRepository();

    public string GetPath(Guid chapterId)
    {
        var entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapterId && x.DeletedAt is null)
            ?? throw new InvalidOperationException("Chương chưa được đăng ký trong dự án.");
        return session.Resolve(entry.RelativePath);
    }

    public async Task SaveAsync(ChapterDocument chapter, CancellationToken cancellationToken = default)
    {
        var entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapter.Id && x.DeletedAt is null)
            ?? throw new InvalidOperationException("Không lưu chương mồ côi vào thư mục dự án.");
        if (chapter.ProjectId != session.Manifest.ProjectId || chapter.ChapterNumber != entry.Number ||
            !string.Equals(GetPath(chapter.Id), session.ChapterStore.GetPath(chapter.Id), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Liên kết/path chương không khớp với manifest.");
        await _repository.UpdateChapterAsync(session, chapter, cancellationToken).ConfigureAwait(false);
    }

    public Task<ChapterLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!session.Manifest.Chapters.Any(x => x.DeletedAt is null &&
            string.Equals(session.Resolve(x.RelativePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Tệp không thuộc danh sách chương đang hoạt động.");
        return session.ChapterStore.LoadAsync(path, cancellationToken);
    }
}
