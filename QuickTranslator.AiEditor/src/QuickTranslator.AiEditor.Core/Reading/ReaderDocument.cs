using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Reading;

public sealed record ReaderSegmentSpan(Guid SegmentId, int Order, int Start, int Length);

public sealed record ReaderDocument(
    string Text,
    IReadOnlyList<ReaderSegmentSpan> Spans,
    IReadOnlyList<Guid> IncompleteSegmentIds,
    IReadOnlyList<Guid> ErrorSegmentIds,
    IReadOnlyList<Guid> InspectionSegmentIds)
{
    public bool IsComplete => IncompleteSegmentIds.Count == 0 && ErrorSegmentIds.Count == 0;

    public Guid? FindSegmentAt(int characterOffset)
    {
        if (Spans.Count == 0) return null;
        int offset = Math.Clamp(characterOffset, 0, Text.Length);
        var exact = Spans.FirstOrDefault(s => offset >= s.Start && offset < s.Start + s.Length);
        if (exact is not null) return exact.SegmentId;
        return Spans.LastOrDefault(s => s.Start <= offset)?.SegmentId ?? Spans[0].SegmentId;
    }

    public int ResolveCharacterOffset(Guid? segmentId, int? segmentOffset, int fallbackOffset)
    {
        if (segmentId is Guid id && segmentOffset is int relative &&
            Spans.FirstOrDefault(span => span.SegmentId == id) is { } span)
            return span.Start + Math.Clamp(relative, 0, span.Length);
        return Math.Clamp(fallbackOffset, 0, Text.Length);
    }
}

public static class ReaderDocumentComposer
{
    public static ReaderDocument Compose(ChapterDocument chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ComposedChapter composed = ChapterTranslationComposer.Compose(chapter);
        return new(composed.Text,
            composed.Spans.Select(span => new ReaderSegmentSpan(
                span.SegmentId, span.Order, span.Start, span.Length)).ToArray(),
            composed.MissingIds, composed.ErrorIds, composed.InspectionIds);
    }
}
