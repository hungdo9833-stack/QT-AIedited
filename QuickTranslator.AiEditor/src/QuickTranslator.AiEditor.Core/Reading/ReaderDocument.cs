using System.Text;
using QuickTranslator.AiEditor.Core.Models;

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
}

public static class ReaderDocumentComposer
{
    public static ReaderDocument Compose(ChapterDocument chapter)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        var text = new StringBuilder();
        var spans = new List<ReaderSegmentSpan>();
        var incomplete = new List<Guid>();
        var errors = new List<Guid>();
        var inspection = new List<Guid>();

        foreach (var segment in chapter.Segments.OrderBy(s => s.Order))
        {
            string translated = segment.EditableText ?? string.Empty;
            spans.Add(new(segment.Id, segment.Order, text.Length, translated.Length));
            text.Append(translated); // Reader never invents whitespace at an AI segment boundary.

            if (string.IsNullOrWhiteSpace(translated) || segment.RequiresRetranslation)
                incomplete.Add(segment.Id);
            if (segment.Status == SegmentStatus.Error)
                errors.Add(segment.Id);
            if (segment.Status == SegmentStatus.InspectionRequired || segment.RequiresRetranslation)
                inspection.Add(segment.Id);
        }

        return new(text.ToString(), spans, incomplete, errors, inspection);
    }
}
