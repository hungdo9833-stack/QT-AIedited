namespace QuickTranslator.AiEditor.Core.Reading;

public sealed record ReaderDisplayParagraph(int Start, int Length);

public static class ReaderDisplayLayout
{
    public static IReadOnlyList<ReaderDisplayParagraph> Create(string text, bool collapseExtraBlankLines)
    {
        ArgumentNullException.ThrowIfNull(text);
        var paragraphs = new List<ReaderDisplayParagraph>();
        int start = 0;
        while (start <= text.Length)
        {
            int end = start;
            while (end < text.Length && text[end] is not ('\r' or '\n')) end++;
            int length = end - start;
            if (length > 0 || !collapseExtraBlankLines || paragraphs.Count == 0 && end == text.Length)
                paragraphs.Add(new(start, length));
            if (end >= text.Length) break;
            start = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }
        if (paragraphs.Count == 0) paragraphs.Add(new(0, 0));
        return paragraphs;
    }
}
