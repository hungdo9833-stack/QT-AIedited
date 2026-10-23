using System.Globalization;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

internal static class SegmentBoundaryContext
{
    internal static string TranslatedTail(string text)
    {
        text = text.TrimEnd();
        int[] positions = StringInfo.ParseCombiningCharacters(text);
        int start = positions.Length <= 250 ? 0 : positions[^250];
        string tail = Excerpt(text.AsSpan(), start);
        return tail.Length == 0 ? "" : "… " + tail;
    }

    internal static string PrecedingSource(ChapterDocument chapter, TranslationSegment segment)
    {
        string original = chapter.OriginalText;
        int end = Math.Clamp(segment.StartUtf16, 0, original.Length);
        if (end == 0 || segment.SourceText.Length == 0 || segment.SourceText.Length > original.Length - end ||
            !original.AsSpan(end, segment.SourceText.Length).SequenceEqual(segment.SourceText.AsSpan())) return "";
        int start = Math.Max(0, end - 200);
        if (start > 0 && char.IsLowSurrogate(original[start]) && char.IsHighSurrogate(original[start - 1])) start++;
        return Excerpt(original.AsSpan(0, end), start);
    }

    internal static bool OpensWithQuote(string text)
    {
        var source = text.AsSpan().TrimStart();
        return source.Length > 0 && source[0] is '“' or '‘' or '「' or '『' or '"' or '\'' or '«' or '‹' or '„' or '‚' or '＂' or '＇';
    }

    private static string Excerpt(ReadOnlySpan<char> text, int start)
    {
        if (start > 0 && !IsBoundary(text[start - 1]))
        {
            int limit = start + (text.Length - start) / 2;
            for (int i = start; i < limit; i++)
            {
                if (!IsBoundary(text[i])) continue;
                start = i + 1;
                while (start < text.Length && text[start] is '”' or '’' or '」' or '』' or '"' or '\'' or '»' or '›') start++;
                break;
            }
        }
        var tail = text[start..].Trim();
        return tail.ToString();
    }

    private static bool IsBoundary(char value) => value is '.' or '!' or '?' or '。' or '！' or '？' or '\r' or '\n' or '\u2028' or '\u2029';
}
