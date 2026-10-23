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
        var entry = session.Manifest.Chapters.SingleOrDefault(x => x.DeletedAt is null &&
            string.Equals(session.Resolve(x.RelativePath), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new InvalidOperationException("Tệp không thuộc danh sách chương đang hoạt động.");
        return _repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken);
    }
}

/// <summary>
/// Queue-only adapter. It serializes the final business-state check with the chapter write so a
/// completed AI request cannot replace a draft, approval, source edit, or segment-boundary edit
/// committed while that request was running.
/// </summary>
internal sealed class TranslationChapterStore : IChapterStore
{
    private readonly ProjectSession _session;
    private readonly ProjectRepository _repository;
    private readonly string _expectedSource;
    private readonly string _expectedProtected;

    public TranslationChapterStore(ProjectSession session, ProjectRepository repository, ChapterDocument baseline)
    {
        _session = session;
        _repository = repository;
        _expectedSource = ProjectTranslationFingerprints.Source(baseline);
        _expectedProtected = ProjectTranslationFingerprints.Protected(baseline);
    }

    public string GetPath(Guid chapterId) => new RegisteredChapterStore(_session, _repository).GetPath(chapterId);

    public Task SaveAsync(ChapterDocument chapter, CancellationToken cancellationToken = default) =>
        _repository.UpdateTranslatedChapterAsync(_session, chapter, _expectedSource, _expectedProtected,
            cancellationToken);

    public Task<ChapterLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        new RegisteredChapterStore(_session, _repository).LoadAsync(path, cancellationToken);
}
