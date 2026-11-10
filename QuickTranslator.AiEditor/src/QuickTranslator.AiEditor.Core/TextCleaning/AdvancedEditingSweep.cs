using System.Threading.Channels;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.TextCleaning;

/// <summary>A backend the sweep sends editing requests to, with how many it may run at once.</summary>
public sealed record AdvancedEditingLane(ILlmClient Client, ModelOptions Options, int Slots = 1);

/// <summary>
/// "Biên tập nâng cao" on chapters that are already translated: one editing request per translated segment (the saved
/// text plus its Chinese source), no new translation. Each edit is written as a batch replacement, so the segment's
/// draft history keeps the old text and the edit can be undone per chapter. Edits that fail the safety check of
/// <see cref="AdvancedEditing.Accept"/> leave the segment as it was.
/// </summary>
public sealed class AdvancedEditingSweep(ProjectRepository repository, IReadOnlyList<AdvancedEditingLane> lanes,
    TimeProvider? clock = null)
{
    public const string SourceLabel = "Biên tập nâng cao";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>A segment the sweep would edit: it has a translation and is not mid-translation.</summary>
    public static bool HasTranslation(TranslationSegment segment) =>
        segment.Status != SegmentStatus.Translating && !string.IsNullOrWhiteSpace(segment.EditableText);

    /// <summary>Drafted, edited or approved by the user: edited only when the user confirmed it.</summary>
    public static bool IsUserText(TranslationSegment segment) =>
        segment.UserDraft is not null || segment.Status == SegmentStatus.Approved || segment.Approvals.Count > 0;

    /// <summary>Translated segments of the chapters, and how many of them hold the user's own text.</summary>
    public async Task<(int Segments, int UserSegments)> CountAsync(ProjectSession session, IReadOnlyList<Guid> chapterIds,
        CancellationToken cancellationToken = default)
    {
        int segments = 0, user = 0;
        foreach (Guid id in chapterIds)
        {
            ChapterDocument chapter = (await repository.LoadChapterAsync(session, id, cancellationToken).ConfigureAwait(false)).Chapter;
            foreach (TranslationSegment segment in chapter.Segments.Where(HasTranslation))
            {
                segments++;
                if (IsUserText(segment)) user++;
            }
        }
        return (segments, user);
    }

    public async Task<HanSweepResult> EditAsync(ProjectSession session, IReadOnlyList<Guid> chapterIds, bool includeUserText,
        IProgress<HanSweepProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (lanes.Count == 0) throw new InvalidOperationException("Chưa có nguồn dịch nào để biên tập.");
        string title = session.Manifest.VietnameseTitle;
        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync(cancellationToken).ConfigureAwait(false);
        ProjectChapterEntry[] entries = session.Manifest.Chapters
            .Where(entry => entry.DeletedAt is null && chapterIds.Contains(entry.ChapterId)).OrderBy(entry => entry.Number).ToArray();
        // One token per slot of every lane: a chapter runs on whichever backend has a free slot.
        var tokens = Channel.CreateUnbounded<AdvancedEditingLane>();
        foreach (AdvancedEditingLane lane in lanes)
            for (int i = 0; i < Math.Max(1, lane.Slots); i++) tokens.Writer.TryWrite(lane);
        var store = new RegisteredChapterStore(session, repository);
        using var saveGate = new SemaphoreSlim(1, 1);
        int done = 0, edited = 0, kept = 0, failed = 0;
        progress?.Report(new(title, 1, 1, 0, entries.Length, 0));

        async Task RunAsync(ProjectChapterEntry entry)
        {
            AdvancedEditingLane lane = await tokens.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ChapterDocument chapter = (await repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken)
                    .ConfigureAwait(false)).Chapter;
                var items = new List<BatchReplacementPreviewItem>();
                foreach (TranslationSegment segment in chapter.Segments.Where(HasTranslation))
                {
                    if (!includeUserText && IsUserText(segment)) continue;
                    string draft = segment.EditableText;
                    AdvancedEditingContext context = AdvancedEditingContext.For(resources.Characters, resources.Terms,
                        segment.SourceText, draft, entry.Number);
                    string prompt = AdvancedEditing.BuildPrompt(segment.SourceText, draft, context);
                    LlmCompletion completion = await lane.Client.TranslateAsync(prompt, lane.Options, cancellationToken)
                        .ConfigureAwait(false);
                    string? text = completion.Done == false ? null : AdvancedEditing.Review(draft, completion.Text, context).Text;
                    if (text is null || string.Equals(text.Trim(), draft.Trim(), StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref kept);
                        continue;
                    }
                    items.Add(ChineseRetranslationPreviewBuilder.WholeSegment(chapter, segment, text, SourceLabel));
                }
                if (items.Count > 0)
                {
                    await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        BatchReplacementEngine.ApplyToChapter(chapter, items, allowProtected: true,
                            BatchReplacementScope.CurrentProject, Guid.NewGuid(), _clock.GetUtcNow());
                        await store.SaveAsync(chapter, cancellationToken).ConfigureAwait(false);
                    }
                    finally { saveGate.Release(); }
                    Interlocked.Add(ref edited, items.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
            }
            finally
            {
                tokens.Writer.TryWrite(lane);
                progress?.Report(new(title, 1, 1, Interlocked.Increment(ref done), entries.Length, Volatile.Read(ref edited)));
            }
        }

        await Task.WhenAll(entries.Select(RunAsync)).ConfigureAwait(false);
        return new(title, entries.Length, edited, kept, failed);
    }

    /// <summary>Puts back the text earlier editing runs replaced in these chapters, newest first, only while such a run is
    /// the chapter's latest batch replacement (an edit made afterwards is never thrown away).</summary>
    public async Task<HanSweepResult> UndoAsync(ProjectSession session, IReadOnlyList<Guid> chapterIds,
        IProgress<HanSweepProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string title = session.Manifest.VietnameseTitle;
        ProjectChapterEntry[] entries = session.Manifest.Chapters
            .Where(entry => entry.DeletedAt is null && chapterIds.Contains(entry.ChapterId)).OrderBy(entry => entry.Number).ToArray();
        var store = new RegisteredChapterStore(session, repository);
        int reverted = 0, failed = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new(title, 1, 1, i + 1, entries.Length, reverted));
            try
            {
                ChapterDocument chapter = (await repository.LoadChapterAsync(session, entries[i].ChapterId, cancellationToken)
                    .ConfigureAwait(false)).Chapter;
                int before = reverted;
                while (chapter.BatchReplacementHistory.Where(run => !run.Reverted && !run.Automatic)
                           .OrderByDescending(run => run.CreatedAt).FirstOrDefault() is { } latest &&
                       latest.Segments.Count > 0 &&
                       latest.Segments.All(segment => segment.Changes.All(change => change.SourceLabel == SourceLabel)))
                    reverted += BatchReplacementEngine.UndoLatest(chapter, _clock.GetUtcNow(), latest.Id).SegmentsChanged;
                if (reverted > before) await store.SaveAsync(chapter, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
            }
        }
        return new(title, entries.Length, reverted, 0, failed);
    }
}
