using System.Collections.Immutable;

namespace QuickTranslator.AiEditor.Core.Importing;

public static class UnicodeLineScanner
{
    public static ImmutableArray<SourceLine> Scan(RawSourceSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Text.Length == 0) return [];
        var lines = ImmutableArray.CreateBuilder<SourceLine>();
        string text = source.Text;
        int start = 0, number = 1;
        while (start < text.Length)
        {
            int end = start;
            while (end < text.Length && text[end] is not '\r' and not '\n') end++;
            int newlineLength = 0;
            if (end < text.Length)
            {
                newlineLength = text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1;
            }
            var content = new SourceSpan(start, end - start);
            var terminator = new SourceSpan(end, newlineLength);
            var full = new SourceSpan(start, end - start + newlineLength);
            lines.Add(new(number++, content, terminator, full, text.Substring(start, end - start)));
            start = end + newlineLength;
        }
        return lines.ToImmutable();
    }
}
