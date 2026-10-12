namespace QuickTranslator.AiEditor.Core.Traditional;

public enum TraditionalPane { Chinese, HanViet, VietPhrase }

public sealed record SelectionProjection(TextRange Chinese, TextRange HanViet, TextRange VietPhrase);

public static class SelectionAlignment
{
    public static SelectionProjection Project(TraditionalTranslationResult result, TraditionalPane origin, TextRange selection)
    {
        ArgumentNullException.ThrowIfNull(result);
        TextRange source = origin switch
        {
            TraditionalPane.Chinese => Clamp(selection, result.SourceText.Length),
            TraditionalPane.HanViet => ToSource(result.HanVietAlignment, selection, result.HanVietText.Length),
            TraditionalPane.VietPhrase => ToSource(result.VietPhraseAlignment, selection, result.VietPhraseText.Length),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        return new(source, ToTarget(result.HanVietAlignment, source), ToTarget(result.VietPhraseAlignment, source));
    }

    private static TextRange ToSource(IReadOnlyList<AlignmentSpan> spans, TextRange selected, int textLength)
    {
        selected = Clamp(selected, textLength);
        var matches = Intersections(spans, selected, target: true);
        return Bounds(matches.Select(s => s.Source));
    }

    private static TextRange ToTarget(IReadOnlyList<AlignmentSpan> spans, TextRange source)
    {
        var matches = Intersections(spans, source, target: false);
        return Bounds(matches.Select(s => s.Target));
    }

    private static IReadOnlyList<AlignmentSpan> Intersections(IReadOnlyList<AlignmentSpan> spans, TextRange selected, bool target)
    {
        if (selected.Length == 0)
        {
            var exact = spans.Where(span => ContainsCaret(target ? span.Target : span.Source, selected.Start)).ToArray();
            if (exact.Length > 0) return exact;
        }
        return spans.Where(span => (target ? span.Target : span.Source).Overlaps(selected)).ToArray();
    }

    private static bool ContainsCaret(TextRange range, int position) => range.Start <= position && position <= range.End;

    private static TextRange Bounds(IEnumerable<TextRange> ranges)
    {
        var array = ranges.ToArray();
        if (array.Length == 0) return new(0, 0);
        int start = array.Min(r => r.Start), end = array.Max(r => r.End);
        return new(start, end - start);
    }

    private static TextRange Clamp(TextRange range, int length)
    {
        int start = Math.Clamp(range.Start, 0, length);
        int end = Math.Clamp(range.End, start, length);
        return new(start, end - start);
    }
}
