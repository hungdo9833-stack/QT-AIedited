using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.TextCleaning;

public sealed class BatchProjectReplacementService(ChapterFileStore store, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<IReadOnlyList<ChapterDocument>> LoadProjectChaptersAsync(Guid characterProjectId,
        ChapterDocument? currentSnapshot = null, CancellationToken cancellationToken = default)
    {
        var chapters = new Dictionary<Guid, ChapterDocument>();
        foreach (string path in store.EnumerateChapterPaths())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChapterLoadResult loaded;
            try { loaded = await store.LoadAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                throw new InvalidDataException($"Không thể đọc chương {Path.GetFileName(path)} khi nạp phạm vi dự án.", ex);
            }
            if (loaded.Chapter.CharacterProject.Id == characterProjectId)
                chapters[loaded.Chapter.Id] = loaded.Chapter;
        }
        if (currentSnapshot is not null)
        {
            if (currentSnapshot.CharacterProject.Id != characterProjectId)
                throw new InvalidOperationException("Chương đang mở không thuộc dự án được chọn.");
            chapters[currentSnapshot.Id] = DocumentJson.Clone(currentSnapshot);
        }
        return chapters.Values.OrderBy(x => x.CreatedAt).ThenBy(x => x.Title, StringComparer.CurrentCulture).ToArray();
    }

    public async Task<BatchProjectApplyReport> ApplyAsync(IEnumerable<ChapterDocument> chapters,
        IEnumerable<BatchReplacementPreviewItem> selectedItems, bool allowProtected,
        CancellationToken cancellationToken = default)
    {
        var chapterList = chapters.ToArray();
        var items = selectedItems.ToArray();
        if (chapterList.Length == 0 || items.Length == 0)
            throw new InvalidOperationException("Không có chương hoặc vị trí được chọn để thay thế.");
        Guid projectId = chapterList[0].CharacterProject.Id;
        if (chapterList.Any(x => x.CharacterProject.Id != projectId) || items.Any(x => x.CharacterProjectId != projectId))
            throw new InvalidOperationException("Không thể áp dụng thay thế sang bộ truyện/dự án khác.");
        Guid batchId = Guid.NewGuid();
        var targetIds = items.Select(x => x.ChapterId).Distinct().ToArray();
        if (targetIds.Any(id => chapterList.All(x => x.Id != id)))
            throw new InvalidOperationException("Có kết quả xem trước thuộc chương không còn trong dự án.");
        var results = new List<BatchProjectChapterResult>();
        foreach (var chapter in chapterList.Where(x => targetIds.Contains(x.Id)))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(CancelledChapter(chapter));
                continue;
            }
            try
            {
                var snapshot = DocumentJson.Clone(chapter);
                var result = BatchReplacementEngine.ApplyToChapter(snapshot,
                    items.Where(x => x.ChapterId == chapter.Id), allowProtected,
                    BatchReplacementScope.CurrentProject, batchId, _clock.GetUtcNow());
                await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
                results.Add(new(chapter.Id, chapter.Title, true, result.OccurrencesChanged, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(CancelledChapter(chapter));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new(chapter.Id, chapter.Title, false, 0, ex.Message));
            }
        }
        return new(batchId, results);
    }

    public async Task<BatchProjectApplyReport> UndoAsync(IEnumerable<ChapterDocument> chapters, Guid batchId,
        CancellationToken cancellationToken = default)
    {
        var targets = chapters.Where(x => x.BatchReplacementHistory.Any(r => r.Id == batchId && !r.Reverted)).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("Không tìm thấy đợt thay thế trong các chương của dự án.");
        Guid projectId = targets[0].CharacterProject.Id;
        if (targets.Any(x => x.CharacterProject.Id != projectId))
            throw new InvalidOperationException("Không thể hoàn tác lẫn nhiều dự án.");
        var results = new List<BatchProjectChapterResult>();
        foreach (var chapter in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                results.Add(CancelledChapter(chapter));
                continue;
            }
            try
            {
                var snapshot = DocumentJson.Clone(chapter);
                var result = BatchReplacementEngine.UndoLatest(snapshot, _clock.GetUtcNow(), batchId);
                await store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
                results.Add(new(chapter.Id, chapter.Title, true, result.OccurrencesChanged, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                results.Add(CancelledChapter(chapter));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new(chapter.Id, chapter.Title, false, 0, ex.Message));
            }
        }
        return new(batchId, results);
    }

    private static BatchProjectChapterResult CancelledChapter(ChapterDocument chapter) => new(
        chapter.Id, chapter.Title, false, 0,
        "Đã hủy; chương này chưa được ghi. Các chương đã lưu trước đó được báo riêng.");
}
