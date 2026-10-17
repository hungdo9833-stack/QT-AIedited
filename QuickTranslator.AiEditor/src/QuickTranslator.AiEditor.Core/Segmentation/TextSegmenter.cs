using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Segmentation;

public sealed class TextSegmenter
{
    public IReadOnlyList<TranslationSegment> Segment(string source, Guid chapterId, SegmenterOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        // Invalid UTF-16 cannot survive a UTF-8/JSON round trip unchanged; reject instead of replacing characters.
        try { new UTF8Encoding(false, true).GetByteCount(source); }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("Nguyên văn chứa chuỗi Unicode không hợp lệ. Hãy nhập lại từ tệp UTF-8 hoặc clipboard.", nameof(source), ex);
        }
        if (chapterId == Guid.Empty) throw new ArgumentException("ID chương không hợp lệ.", nameof(chapterId));
        options ??= new();
        options.Validate();
        if (source.Length == 0) return [];

        var units = ReadTextElements(source);
        var result = new List<TranslationSegment>();
        for (int start = 0; start < units.Count;)
        {
            int end = start, han = 0;
            while (end < units.Count && end - start < options.MaximumTextElements &&
                   han + units[end].HanCount <= options.MaximumHanCharacters)
                han += units[end++].HanCount;

            // A complete tail may be shorter than the minimum. Never drop or pad it.
            int chosen = end;
            if (end < units.Count)
            {
                int rank = 0, distance = int.MaxValue, cumulative = 0;
                for (int i = start; i < end; i++)
                {
                    cumulative += units[i].HanCount;
                    bool eligible = han >= options.MinimumHanCharacters
                        ? cumulative >= options.MinimumHanCharacters
                        : i - start + 1 >= Math.Min(options.MinimumHanCharacters, end - start);
                    int d = Math.Abs((han > 0 ? cumulative : i - start + 1) - options.TargetHanCharacters);
                    if (eligible && units[i].BoundaryRank > 0 &&
                        (units[i].BoundaryRank > rank || units[i].BoundaryRank == rank && d < distance))
                    {
                        rank = units[i].BoundaryRank;
                        distance = d;
                        chosen = i + 1;
                    }
                }
                if (rank == 0 && han >= options.TargetHanCharacters)
                {
                    cumulative = 0;
                    chosen = start;
                    // Punctuation and closing quotes following the target stay with that text.
                    while (chosen < end && cumulative + units[chosen].HanCount <= options.TargetHanCharacters)
                        cumulative += units[chosen++].HanCount;
                }
            }
            // StringInfo keeps surrogate pairs, combining characters and emoji sequences together.
            if (chosen <= start) chosen = start + 1;
            int startOffset = units[start].Start;
            int endOffset = chosen == units.Count ? source.Length : units[chosen].Start;
            string text = source[startOffset..endOffset];
            result.Add(new TranslationSegment
            {
                Id = CreateStableId(chapterId, startOffset, text),
                Order = result.Count + 1,
                StartUtf16 = startOffset,
                SourceText = text,
                HanCharacterCount = CountHanCharacters(text),
                Status = SegmentStatus.Untranslated
            });
            start = chosen;
        }
        return result;
    }

    public static string Reconstruct(IEnumerable<TranslationSegment> segments) =>
        string.Concat(segments.OrderBy(s => s.Order).Select(s => s.SourceText));

    public static Guid CreateStableId(Guid chapterId, int startUtf16, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes($"qtaie-segment-v1:{chapterId:D}:{startUtf16.ToString(CultureInfo.InvariantCulture)}:{text}");
        return new Guid(SHA256.HashData(bytes).AsSpan(0, 16));
    }

    public static int CountHanCharacters(string text) => text.EnumerateRunes().Count(IsHan);

    public static IReadOnlyList<int> GetSentenceBoundaries(string text)
    {
        var units = ReadTextElements(text);
        return units.Select((unit, index) => (unit, index)).Where(x => x.unit.BoundaryRank >= 3 && x.index + 1 < units.Count)
            .Select(x => units[x.index + 1].Start).ToArray();
    }

    private static bool IsHan(Rune rune) => rune.Value is
        0x3007 or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
        >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2A6DF or
        >= 0x2A700 and <= 0x2B73F or >= 0x2B740 and <= 0x2B81F or
        >= 0x2B820 and <= 0x2CEAF or >= 0x2CEB0 and <= 0x2EE5F or
        >= 0x2F800 and <= 0x2FA1F or >= 0x30000 and <= 0x3347F;

    private static List<TextUnit> ReadTextElements(string source)
    {
        var result = new List<TextUnit>();
        var enumerator = StringInfo.GetTextElementEnumerator(source);
        var quoteClosers = new Stack<char>();
        while (enumerator.MoveNext())
        {
            string element = enumerator.GetTextElement();
            char first = element[0];
            if (first is '“' or '「' or '『') quoteClosers.Push(first switch { '“' => '”', '「' => '」', _ => '』' });
            else if (first == '"')
            {
                if (quoteClosers.TryPeek(out char closer) && closer == '"') quoteClosers.Pop();
                else quoteClosers.Push('"');
            }
            else if (quoteClosers.TryPeek(out char closer) && first == closer) quoteClosers.Pop();

            bool newLine = element.Contains('\n') || element.Contains('\r');
            bool sentence = first is '。' or '！' or '？' or '!' or '?' or '…';
            bool semicolon = first is '；' or ';';
            bool closingQuote = first is '”' or '」' or '』' || first == '"' && quoteClosers.Count == 0;
            int rank = newLine ? quoteClosers.Count == 0 ? 5 : 1 : closingQuote && quoteClosers.Count == 0 ? 4 :
                sentence ? quoteClosers.Count == 0 ? 3 : 1 : semicolon ? 2 : 0;
            result.Add(new(enumerator.ElementIndex, CountHanCharacters(element), rank, first));
        }
        // Do not place a preferred cut between punctuation and the closing quote/ellipsis.
        for (int i = 0; i < result.Count - 1; i++)
            if (result[i + 1].First is '”' or '」' or '』' or '…' or '"')
                result[i] = result[i] with { BoundaryRank = 0 };
        return result;
    }

    private sealed record TextUnit(int Start, int HanCount, int BoundaryRank, char First);
}
