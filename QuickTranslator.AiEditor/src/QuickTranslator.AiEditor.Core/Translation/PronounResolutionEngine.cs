using System.Globalization;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class PronounResolutionEngine
{
    private static readonly HashSet<string> KinshipContinuations = new(StringComparer.OrdinalIgnoreCase)
    {
        "nội", "ngoại", "cố", "mẹ", "cha", "bố", "má", "cô", "dì", "chú", "bác", "thím", "cậu", "mợ",
        "trai", "gái", "rể", "dâu", "chồng", "vợ", "con", "em"
    };

    public IReadOnlyList<PronounSuggestion> ScanSegment(Guid segmentId, int order, string sourceText,
        string translatedText, string previousContext, CharacterProject project, bool protectedByUser,
        IReadOnlyList<ConfirmedTerm>? glossary = null)
    {
        project.Validate();
        if (string.IsNullOrEmpty(translatedText)) return [];
        var profiles = project.Profiles.Where(p => p.NormalizationEnabled &&
            (!project.ConsistencyLockEnabled || p.NarrativePronounLocked)).ToArray();
        if (profiles.Length == 0) return [];
        var sentenceSpans = SentenceSpans(translatedText);
        var dialogueSpans = DialogueSpans(translatedText);
        var terms = profiles.SelectMany(profile =>
                profile.DisallowedNarrativePronouns.Select(term => new PronounTerm(term, profile, false))
                    .Concat(string.IsNullOrWhiteSpace(profile.PrimaryNarrativePronoun) ? [] :
                        [new PronounTerm(profile.PrimaryNarrativePronoun, profile, true)])
                    .Concat(string.IsNullOrWhiteSpace(profile.SecondaryNarrativePronoun) ? [] :
                        [new PronounTerm(profile.SecondaryNarrativePronoun, profile,
                            !project.ConsistencyLockEnabled || !profile.NarrativePronounLocked)]))
            .Where(t => !string.IsNullOrWhiteSpace(t.Text))
            .OrderByDescending(t => t.Text.Length).ToArray();
        var occupied = new bool[translatedText.Length];
        var occurrences = new List<(int Start, int Length, string Text)>();
        foreach (string term in terms.Select(t => t.Text).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(s => s.Length))
        {
            int search = 0;
            while (search < translatedText.Length)
            {
                int start = CultureInfo.InvariantCulture.CompareInfo.IndexOf(translatedText, term, search,
                    CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
                if (start < 0) break;
                int length = term.Length;
                search = start + Math.Max(1, length);
                if (!IsWordBoundary(translatedText, start, length) || Enumerable.Range(start, length).Any(i => occupied[i])) continue;
                for (int i = start; i < start + length; i++) occupied[i] = true;
                occurrences.Add((start, length, translatedText.Substring(start, length)));
            }
        }

        var suggestions = new List<PronounSuggestion>();
        foreach (var occurrence in occurrences.OrderBy(o => o.Start))
        {
            var sentence = sentenceSpans.First(s => occurrence.Start >= s.Start && occurrence.Start < s.End);
            string sentenceText = translatedText[sentence.Start..sentence.End].Trim();
            var matching = terms.Where(t => string.Equals(t.Text, occurrence.Text, StringComparison.OrdinalIgnoreCase))
                .Select(t => t.Profile).DistinctBy(p => p.Id).ToArray();
            if (matching.Length == 0) continue;
            if (dialogueSpans.Any(s => occurrence.Start >= s.Start && occurrence.Start < s.End))
            {
                suggestions.Add(Item(PronounSuggestionKind.Valid, null, "Giữ nguyên đại từ trong lời thoại trực tiếp."));
                continue;
            }
            if (IsKinshipExpression(translatedText, occurrence.Start, occurrence.Length))
            {
                suggestions.Add(Item(PronounSuggestionKind.Valid, null, "Giữ nguyên danh xưng quan hệ gia đình."));
                continue;
            }

            string before = NarrativeOnly(translatedText[sentence.Start..occurrence.Start]);
            string antecedentContext = before;
            var currentMentions = matching.Where(p => ContainsAnyName(before, p, glossary)).ToArray();
            CharacterProfile? resolved = currentMentions.Length == 1 ? currentMentions[0] : null;
            bool multiple = currentMentions.Length > 1;
            if (resolved is null && !multiple)
            {
                string priorSentence = NarrativeOnly(PreviousSentence(translatedText, sentenceSpans, sentence.Start, previousContext));
                var priorMentions = matching.Where(p => ContainsAnyName(priorSentence, p, glossary)).ToArray();
                resolved = priorMentions.Length == 1 ? priorMentions[0] : null;
                multiple = priorMentions.Length > 1;
                antecedentContext = priorSentence;
            }
            bool unknownCharacter = resolved is not null && ContainsUnregisteredProperName(antecedentContext, profiles, glossary);
            if (unknownCharacter) resolved = null;
            if (resolved is null)
            {
                string reason = unknownCharacter
                    ? "Ngữ cảnh gần có tên riêng chưa đăng ký; không tự suy đoán chủ thể đại từ."
                    : multiple
                    ? "Có nhiều nhân vật phù hợp trong ngữ cảnh gần; không tự đổi chủ thể."
                    : "Không có tên nhân vật đã xác nhận làm tiền ngữ rõ ràng; không tự thay thế.";
                suggestions.Add(Item(protectedByUser ? PronounSuggestionKind.Protected : PronounSuggestionKind.Ambiguous, null, reason));
                continue;
            }

            bool alreadyAllowed = string.Equals(occurrence.Text, resolved.PrimaryNarrativePronoun, StringComparison.OrdinalIgnoreCase) ||
                (!project.ConsistencyLockEnabled &&
                 string.Equals(occurrence.Text, resolved.SecondaryNarrativePronoun, StringComparison.OrdinalIgnoreCase));
            if (alreadyAllowed)
            {
                suggestions.Add(Item(PronounSuggestionKind.Valid, resolved, $"Đại từ đã phù hợp hồ sơ {resolved.VietnameseName}."));
                continue;
            }
            bool lockedSecondary = project.ConsistencyLockEnabled && resolved.NarrativePronounLocked &&
                string.Equals(occurrence.Text, resolved.SecondaryNarrativePronoun, StringComparison.OrdinalIgnoreCase);
            if (!lockedSecondary && !resolved.DisallowedNarrativePronouns.Contains(occurrence.Text, StringComparer.OrdinalIgnoreCase)) continue;
            string replacement = MatchCapitalization(occurrence.Text, resolved.PrimaryNarrativePronoun);
            suggestions.Add(Item(protectedByUser ? PronounSuggestionKind.Protected : PronounSuggestionKind.SafeReplacement,
                resolved, protectedByUser
                    ? "Đoạn đã được người dùng sửa hoặc duyệt; chỉ cảnh báo, không ghi đè."
                    : $"Tên {resolved.VietnameseName} là tiền ngữ duy nhất phù hợp trong lời kể." , replacement));

            PronounSuggestion Item(PronounSuggestionKind kind, CharacterProfile? character, string reason, string? replacement = null) =>
                new(Guid.NewGuid(), segmentId, order, occurrence.Start, occurrence.Length, occurrence.Text, replacement,
                    kind, character?.Id, character?.VietnameseName ?? "", sentenceText, sourceText, reason, translatedText);
        }
        return suggestions;
    }

    public static string Apply(string text, IEnumerable<PronounSuggestion> suggestions)
    {
        string result = text;
        foreach (var item in suggestions.Where(i => i.Kind == PronounSuggestionKind.SafeReplacement && i.Replacement is not null)
                     .OrderByDescending(i => i.StartUtf16))
        {
            if (item.StartUtf16 < 0 || item.StartUtf16 + item.Length > result.Length ||
                !string.Equals(result.Substring(item.StartUtf16, item.Length), item.Original, StringComparison.Ordinal))
                throw new InvalidOperationException("Bản dịch đã thay đổi sau khi quét; hãy quét lại trước khi áp dụng.");
            result = string.Concat(result.AsSpan(0, item.StartUtf16), item.Replacement, result.AsSpan(item.StartUtf16 + item.Length));
        }
        return result;
    }

    private static bool ContainsAnyName(string text, CharacterProfile profile, IReadOnlyList<ConfirmedTerm>? glossary) =>
        new[] { profile.VietnameseName }.Concat(profile.Aliases)
            .Concat(glossary?.Where(t => !string.IsNullOrWhiteSpace(profile.ChineseName) &&
                                  string.Equals(t.Chinese, profile.ChineseName, StringComparison.Ordinal))
                .Select(t => t.Vietnamese) ?? [])
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Any(name => ContainsWhole(text, name));

    private static bool ContainsWhole(string text, string value)
    {
        int start = 0;
        while ((start = text.IndexOf(value, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (IsWordBoundary(text, start, value.Length)) return true;
            start += value.Length;
        }
        return false;
    }

    private static bool ContainsUnregisteredProperName(string text, IReadOnlyList<CharacterProfile> profiles,
        IReadOnlyList<ConfirmedTerm>? glossary)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string[] known = profiles.SelectMany(profile => new[] { profile.VietnameseName }.Concat(profile.Aliases))
            .Concat(glossary?.Select(term => term.Vietnamese) ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (Match match in Regex.Matches(text,
                     @"(?<![\p{L}\p{M}])\p{Lu}[\p{L}\p{M}'’.-]*(?:\s+\p{Lu}[\p{L}\p{M}'’.-]*)+",
                     RegexOptions.CultureInvariant))
        {
            if (!known.Any(value => string.Equals(value, match.Value, StringComparison.OrdinalIgnoreCase))) return true;
        }
        return false;
    }

    private static bool IsWordBoundary(string text, int start, int length) =>
        (start == 0 || !char.IsLetterOrDigit(text[start - 1])) &&
        (start + length == text.Length || !char.IsLetterOrDigit(text[start + length]));

    private static bool IsKinshipExpression(string text, int start, int length)
    {
        int next = start + length;
        while (next < text.Length && char.IsWhiteSpace(text[next])) next++;
        int end = next;
        while (end < text.Length && char.IsLetter(text[end])) end++;
        return end > next && KinshipContinuations.Contains(text[next..end]);
    }

    private static string MatchCapitalization(string original, string replacement)
    {
        if (replacement.Length == 0 || original.Length == 0 || !char.IsUpper(original[0])) return replacement;
        return char.ToUpper(replacement[0], CultureInfo.GetCultureInfo("vi-VN")) + replacement[1..];
    }

    private static string NarrativeOnly(string text)
    {
        if (text.Length == 0) return text;
        char[] result = text.ToCharArray();
        foreach (var span in DialogueSpans(text))
            for (int i = span.Start; i < span.End; i++) result[i] = ' ';
        return new string(result);
    }

    private static string PreviousSentence(string text, IReadOnlyList<TextSpan> spans, int currentStart, string previousContext)
    {
        int index = spans.ToList().FindIndex(s => s.Start == currentStart);
        if (index > 0)
        {
            int first = Math.Max(0, index - 2);
            return text[spans[first].Start..spans[index - 1].End];
        }
        var prior = SentenceSpans(previousContext);
        if (prior.Count == 0) return "";
        int priorFirst = Math.Max(0, prior.Count - 2);
        return previousContext[prior[priorFirst].Start..prior[^1].End];
    }

    private static List<TextSpan> SentenceSpans(string text)
    {
        var spans = new List<TextSpan>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('.' or '!' or '?' or '…' or '\n' or '\r')) continue;
            int end = i + 1;
            while (end < text.Length && text[end] is '”' or '"' or '\'' or '’' or '」' or '』') end++;
            if (end > start) spans.Add(new(start, end));
            start = end;
            i = end - 1;
        }
        if (start < text.Length) spans.Add(new(start, text.Length));
        if (spans.Count == 0) spans.Add(new(0, text.Length));
        return spans;
    }

    private static List<TextSpan> DialogueSpans(string text)
    {
        var spans = new List<TextSpan>();
        var stack = new Stack<(char Close, int Start)>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (stack.Count > 0 && c == stack.Peek().Close)
            {
                var open = stack.Pop();
                if (stack.Count == 0) spans.Add(new(open.Start, i + 1));
                continue;
            }
            char? close = c switch { '“' => '”', '「' => '」', '『' => '』', '"' => '"', '\'' => '\'', _ => null };
            if (close is not null && (stack.Count == 0 || c != stack.Peek().Close)) stack.Push((close.Value, i));
        }
        if (stack.Count > 0)
        {
            int start = stack.Min(x => x.Start);
            spans.Add(new(start, text.Length));
        }
        return spans;
    }

    private sealed record PronounTerm(string Text, CharacterProfile Profile, bool Allowed);
    private readonly record struct TextSpan(int Start, int End);
}
