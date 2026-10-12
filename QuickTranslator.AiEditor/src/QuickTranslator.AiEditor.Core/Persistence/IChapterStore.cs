using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed record ChapterLoadResult(ChapterDocument Chapter, bool RecoveredFromBackup, string LoadedPath);

public interface IChapterStore
{
    string GetPath(Guid chapterId);
    Task SaveAsync(ChapterDocument chapter, CancellationToken cancellationToken = default);
    Task<ChapterLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default);
}
