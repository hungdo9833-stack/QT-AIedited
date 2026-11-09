using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.TextCleaning;

/// <summary>What a sweep did to one book.</summary>
public sealed record HanSweepResult(string Title, int Chapters, int FixedSegments, int StillHanSegments,
    int FailedChapters, string? Error = null);

/// <summary>Where a sweep is: book i of n, chapter j of m.</summary>
public sealed record HanSweepProgress(string Book, int BookIndex, int BookCount, int Chapter, int ChapterCount, int FixedSegments);

/// <summary>
/// Repairs Chinese left in books that are already translated, with the same repair new translations get
/// (<see cref="HanResidueRepairClient"/>): Hán-Việt for names, Cantonese meanings, the paragraph again on the GPU,
/// Hán-Việt for what is left. Each repaired segment is written as a batch replacement, so its draft history keeps
/// the old text and the change can be undone per chapter like any other batch replacement.
/// </summary>
public sealed class HanResidueSweep(ProjectRepository repository, HanResidueRepairClient repairer, ModelOptions options,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public const string SourceLabel = "Tự sửa chữ Hán sót";

    public async Task<HanSweepResult> SweepAsync(ProjectSession session, IProgress<HanSweepProgress>? progress = null,
        int bookIndex = 1, int bookCount = 1, CancellationToken cancellationToken = default)
    {
        string title = session.Manifest.VietnameseTitle;
        ProjectChapterEntry[] entries = session.Manifest.Chapters.Where(entry => entry.DeletedAt is null)
            .OrderBy(entry => entry.Number).ToArray();
        var store = new RegisteredChapterStore(session, repository);
        int fixedSegments = 0, stillHan = 0, failed = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(title, bookIndex, bookCount, i + 1, entries.Length, fixedSegments));
            try
            {
                ChapterDocument chapter = (await repository.LoadChapterAsync(session, entries[i].ChapterId, cancellationToken)
                    .ConfigureAwait(false)).Chapter;
                var items = new List<BatchReplacementPreviewItem>();
                foreach (TranslationSegment segment in chapter.Segments)
                {
                    string before = segment.EditableText;
                    if (segment.Status == SegmentStatus.Translating || !HanResidueRepairClient.HasHan(before)) continue;
                    string after = await repairer.RepairTranslationAsync(PromptFor(chapter, segment), before, options,
                        cancellationToken).ConfigureAwait(false);
                    if (HanResidueRepairClient.HasHan(after)) stillHan++;
                    if (string.Equals(after.Trim(), before.Trim(), StringComparison.Ordinal)) continue;
                    items.Add(ChineseRetranslationPreviewBuilder.WholeSegment(chapter, segment, after, SourceLabel));
                }
                if (items.Count == 0) continue;
                BatchReplacementEngine.ApplyToChapter(chapter, items, allowProtected: true,
                    BatchReplacementScope.CurrentProject, Guid.NewGuid(), _clock.GetUtcNow());
                await store.SaveAsync(chapter, cancellationToken).ConfigureAwait(false);
                fixedSegments += items.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }
        progress?.Report(new(title, bookIndex, bookCount, entries.Length, entries.Length, fixedSegments));
        return new(title, entries.Length, fixedSegments, stillHan, failed);
    }

    /// <summary>Puts back the text every earlier sweep replaced in this book (newest first, and only while a sweep's
    /// change is the chapter's latest batch replacement, so an edit made afterwards is never thrown away).</summary>
    public async Task<HanSweepResult> UndoAsync(ProjectSession session, IProgress<HanSweepProgress>? progress = null,
        int bookIndex = 1, int bookCount = 1, CancellationToken cancellationToken = default)
    {
        string title = session.Manifest.VietnameseTitle;
        ProjectChapterEntry[] entries = session.Manifest.Chapters.Where(entry => entry.DeletedAt is null)
            .OrderBy(entry => entry.Number).ToArray();
        var store = new RegisteredChapterStore(session, repository);
        int reverted = 0, failed = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(title, bookIndex, bookCount, i + 1, entries.Length, reverted));
            try
            {
                ChapterDocument chapter = (await repository.LoadChapterAsync(session, entries[i].ChapterId, cancellationToken)
                    .ConfigureAwait(false)).Chapter;
                int before = reverted;
                while (chapter.BatchReplacementHistory.Where(run => !run.Reverted && !run.Automatic)
                           .OrderByDescending(run => run.CreatedAt).FirstOrDefault() is { } latest &&
                       latest.Segments.Count > 0 &&
                       latest.Segments.All(segment => segment.Changes.All(change => change.SourceLabel == SourceLabel)))
                {
                    BatchApplyResult undone = BatchReplacementEngine.UndoLatest(chapter, _clock.GetUtcNow(), latest.Id);
                    reverted += undone.SegmentsChanged;
                }
                if (reverted > before) await store.SaveAsync(chapter, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }
        return new(title, entries.Length, reverted, 0, failed);
    }

    /// <summary>The instTrans prompt the segment was translated with, or a fresh one from its source and the
    /// chapter's confirmed names when it was translated with an older prompt format.</summary>
    internal static string PromptFor(ChapterDocument chapter, TranslationSegment segment)
    {
        string? saved = segment.LatestAiAttempt?.Prompt;
        if (!string.IsNullOrEmpty(saved) && saved.Contains("【源文】", StringComparison.Ordinal) &&
            saved.Contains(TranslationPromptBuilder.InstTransMarker, StringComparison.Ordinal) &&
            saved.Contains(segment.SourceText.Trim(), StringComparison.Ordinal))
            return saved;
        GlossaryEntry[] glossary = chapter.ConfirmedTerms
            .Where(term => segment.SourceText.Contains(term.Chinese, StringComparison.Ordinal))
            .Select(term => new GlossaryEntry(term.Chinese, term.Vietnamese)).DistinctBy(term => term.Chinese).ToArray();
        return new TranslationPromptBuilder().Build(segment.SourceText, glossary, chapter.ConfirmedContextNotes ?? "");
    }
}
