using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Traditional;

/// <summary>
/// Bản chuyển thích nghi từ luồng ChineseToHanViet và ChineseToVietPhraseOneMeaning
/// của TranslatorEngine cũ. Engine chỉ đọc snapshot và tạo alignment trong cùng lần quét.
/// </summary>
public sealed class TraditionalTranslator
{
    public TraditionalTranslationResult Translate(string source, TraditionalDictionarySnapshot dictionaries,
        TraditionalTranslationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(dictionaries);
        options ??= new();
        var (hanText, hanAlignment) = TranslateHanViet(source, dictionaries, cancellationToken);
        var (phraseText, phraseAlignment) = TranslateVietPhrase(source, dictionaries, options, cancellationToken);
        return new()
        {
            SourceText = source,
            SourceVersion = Hash(source),
            HanVietText = hanText,
            VietPhraseText = phraseText,
            HanVietAlignment = hanAlignment,
            VietPhraseAlignment = phraseAlignment,
            DictionaryFingerprint = dictionaries.Manifest.Fingerprint
        };
    }

    private static (string Text, IReadOnlyList<AlignmentSpan> Alignment) TranslateHanViet(string source,
        TraditionalDictionarySnapshot dictionaries, CancellationToken cancellationToken)
    {
        var output = new StringBuilder(source.Length * 2);
        var alignment = new List<AlignmentSpan>(source.Length);
        int sourceIndex = 0;
        while (sourceIndex < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Rune rune = Rune.GetRuneAt(source, sourceIndex);
            int sourceLength = rune.Utf16SequenceLength;
            string raw = source.Substring(sourceIndex, sourceLength);
            bool translated = dictionaries.HanViet.TryGetValue(raw, out string? target);
            target ??= dictionaries.Marks.TryGetValue(raw, out string? mark) ? mark : raw;
            bool nextChinese = translated && TryReadRune(source, sourceIndex + sourceLength, out string? nextHan) && dictionaries.HanViet.ContainsKey(nextHan!);
            TextRange targetRange = Append(output, target, translated);
            alignment.Add(new(new(sourceIndex, sourceLength), targetRange));
            if (nextChinese && (output.Length == 0 || !char.IsWhiteSpace(output[^1]))) output.Append(' ');
            sourceIndex += sourceLength;
        }
        return (output.ToString(), alignment);
    }

    private static (string Text, IReadOnlyList<AlignmentSpan> Alignment) TranslateVietPhrase(string source,
        TraditionalDictionarySnapshot dictionaries, TraditionalTranslationOptions options, CancellationToken cancellationToken)
    {
        var output = new StringBuilder(source.Length * 2);
        var alignment = new List<AlignmentSpan>();
        int index = 0;
        while (index < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int matchedLength = 0;
            string? translated = null;
            int maximum = Math.Min(dictionaries.Manifest.MaximumPhraseLength, source.Length - index);
            for (int length = maximum; length > 0; length--)
            {
                if (!IsSafeBoundary(source, index + length)) continue;
                string phrase = source.Substring(index, length);
                if (!dictionaries.CombinedOneMeaning.TryGetValue(phrase, out string? value)) continue;
                bool exactName = dictionaries.Names.ContainsKey(phrase);
                if (options.PrioritizeNames && !exactName && ContainsNestedName(source, index, length, dictionaries)) continue;
                if (options.TranslationAlgorithm is 0 or 2 && !exactName &&
                    !IsLongestPhraseInSentence(source, index, length, dictionaries.CombinedOneMeaning,
                        dictionaries.Manifest.MaximumPhraseLength, options.TranslationAlgorithm)) continue;
                matchedLength = length;
                translated = value;
                break;
            }

            if (translated is null)
                TryTranslateRule(source, index, dictionaries, out matchedLength, out translated);

            if (translated is not null)
            {
                TextRange target = Append(output, translated, translatedWord: true);
                alignment.Add(new(new(index, matchedLength), target));
                int nextIndex = index + matchedLength;
                if (TryReadRune(source, nextIndex, out string? nextPhrase) && dictionaries.HanViet.ContainsKey(nextPhrase!) &&
                    (output.Length == 0 || !char.IsWhiteSpace(output[^1]))) output.Append(' ');
                index = nextIndex;
                continue;
            }

            Rune rune = Rune.GetRuneAt(source, index);
            int runeLength = rune.Utf16SequenceLength;
            string raw = source.Substring(index, runeLength);
            bool isHan = dictionaries.HanViet.TryGetValue(raw, out string? fallback);
            fallback ??= dictionaries.Marks.TryGetValue(raw, out string? mark) ? mark : raw;
            TextRange fallbackRange = Append(output, fallback, isHan);
            alignment.Add(new(new(index, runeLength), fallbackRange));
            int after = index + runeLength;
            if (isHan && TryReadRune(source, after, out string? nextFallback) && dictionaries.HanViet.ContainsKey(nextFallback!) &&
                (output.Length == 0 || !char.IsWhiteSpace(output[^1]))) output.Append(' ');
            index = after;
        }
        return (output.ToString(), alignment);
    }

    private static bool TryTranslateRule(string source, int start, TraditionalDictionarySnapshot dictionaries,
        out int matchedLength, out string? translated)
    {
        matchedLength = 0;
        translated = null;
        int maximumEnd = Math.Min(source.Length, start + dictionaries.Manifest.MaximumPhraseLength);
        int chineseEnd = start;
        while (chineseEnd < maximumEnd && TryReadRune(source, chineseEnd, out string? item) && dictionaries.HanViet.ContainsKey(item!))
            chineseEnd += Rune.GetRuneAt(source, chineseEnd).Utf16SequenceLength;
        int maximum = chineseEnd - start;
        if (maximum < 3) return false;
        for (int length = maximum; length >= 3; length--)
        {
            if (!IsSafeBoundary(source, start + length)) continue;
            string candidate = source.Substring(start, length);
            if (candidate.Contains('\n') || candidate.Contains('\t')) continue;
            foreach (var rule in dictionaries.Rules)
            {
                string expression = "^" + rule.Key.Replace("{0}", "(.+)", StringComparison.Ordinal) + "$";
                Match match;
                try { match = Regex.Match(candidate, expression, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50)); }
                catch (RegexMatchTimeoutException) { continue; }
                if (!match.Success || match.Groups.Count < 2 || !dictionaries.NhanOneMeaning.TryGetValue(match.Groups[1].Value, out string? value)) continue;
                translated = rule.Value.Replace("{0}", value, StringComparison.Ordinal);
                matchedLength = length;
                return true;
            }
        }
        return false;
    }

    private static bool ContainsNestedName(string source, int start, int phraseLength, TraditionalDictionarySnapshot dictionaries)
    {
        if (phraseLength < 2) return false;
        for (int offset = start + 1; offset < start + phraseLength; offset++)
            for (int length = Math.Min(dictionaries.Manifest.MaximumPhraseLength, source.Length - offset); length >= 2; length--)
                if (IsSafeBoundary(source, offset + length) && dictionaries.Names.ContainsKey(source.Substring(offset, length))) return true;
        return false;
    }

    private static bool IsLongestPhraseInSentence(string source, int start, int phraseLength,
        IReadOnlyDictionary<string, string> dictionary, int maximumPhraseLength, int algorithm)
    {
        if (phraseLength < 2) return true;
        int threshold = algorithm == 0 ? phraseLength : Math.Max(3, phraseLength);
        int end = start + phraseLength - 1;
        for (int offset = start + 1; offset <= end; offset++)
            for (int length = Math.Min(maximumPhraseLength, source.Length - offset); length > threshold; length--)
                if (IsSafeBoundary(source, offset + length) && dictionary.ContainsKey(source.Substring(offset, length))) return false;
        return true;
    }

    private static TextRange Append(StringBuilder output, string text, bool translatedWord)
    {
        text ??= "";
        if (translatedWord && text.Length > 0)
        {
            if (output.Length == 0 || EndsSentence(output)) text = UpperFirst(text);
            else if (!char.IsWhiteSpace(output[^1]) && output[^1] != '(' && !StartsWithClosingPunctuation(text)) output.Append(' ');
        }
        if (StartsWithClosingPunctuation(text) && output.Length > 0 && output[^1] == ' ') output.Length--;
        int start = output.Length;
        output.Append(text);
        return new(start, text.Length);
    }

    private static bool EndsSentence(StringBuilder value)
    {
        int index = value.Length - 1;
        while (index >= 0 && value[index] is ' ' or '\r') index--;
        return index < 0 || value[index] is '\n' or '\t' or '.' or '?' or '!' or ':' or '"' or '\'';
    }

    private static bool StartsWithClosingPunctuation(string text) => text.Length > 0 && text[0] is ',' or '.' or '?' or '!' or ':' or ';';
    private static string UpperFirst(string text) => text.Length == 0 ? text : char.ToUpper(text[0]) + text[1..];
    private static bool TryReadRune(string source, int index, out string? value)
    {
        if ((uint)index >= (uint)source.Length) { value = null; return false; }
        Rune rune = Rune.GetRuneAt(source, index);
        value = source.Substring(index, rune.Utf16SequenceLength);
        return true;
    }
    private static bool IsSafeBoundary(string text, int index) => index <= 0 || index >= text.Length ||
        !(char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
