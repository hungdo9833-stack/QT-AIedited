using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Core.Addressing;

/// <summary>One narration sentence (outside dialogue) that names the character. Offset is UTF-16 in the original chapter.</summary>
public sealed record NarrationSample(int ChapterNumber, int StartUtf16, string Text, char? FollowingPronoun);

/// <summary>CPU signal for how third-person narration refers to a character.</summary>
public sealed record NarrativeCharacter(string Name, int NarrationMentions, int He, int She, int It, int FirstChapter, int LastChapter,
    IReadOnlyList<NarrationSample> Samples)
{
    /// <summary>Source pronoun gender is clear when one form has at least 5 hits and outnumbers the other 3:1.</summary>
    public string SourceGender => He >= 5 && He >= 3 * She ? "male" : She >= 5 && She >= 3 * He ? "female" : "unknown";
}

/// <summary>
/// Addressing Ledger step 1b (see docs/ADDRESSING-LEDGER-DESIGN.vi.md): narration sentences that name each
/// candidate, and the 他/她/它 that follows the name in the same or next sentence before any other name.
/// Counts are evidence for review/AI, never an approved gender or pronoun.
/// </summary>
public static class NarrativeReferenceSurvey
{
    private const string Terminators = "。！？!?\n";

    public static IReadOnlyList<NarrativeCharacter> Analyze(IReadOnlyList<CensusSource> sources, IReadOnlyCollection<string> names,
        int samplesPerCharacter = 10, CancellationToken cancellationToken = default)
    {
        var matcher = new AddressingDialogueSurvey.NameMatcher(names);
        var hits = new List<(string Name, NarrationSample Sample)>();
        foreach (var source in sources.OrderBy(x => x.ChapterNumber).ThenBy(x => x.ChapterId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = AddressingDialogueSurvey.NormalizedText.From(source.OriginalText);
            var segments = Narration(text.Value);
            for (int s = 0; s < segments.Count; s++)
            {
                var (start, end) = segments[s];
                string sentence = text.Value[start..end];
                var found = matcher.Find(sentence);
                for (int h = 0; h < found.Count; h++)
                {
                    // Count a pronoun only for a name in subject position (opening a clause), when the pronoun opens a
                    // later clause or the next sentence of the same paragraph, and no other name or kinship/title term
                    // (妈妈, 姐姐… often refer to someone else) intervenes. Anything else is too ambiguous to count.
                    char? pronoun = null;
                    if (OpensClause(sentence, found[h].Start))
                    {
                        int from = found[h].Start + found[h].Name.Length;
                        int until = h + 1 < found.Count ? found[h + 1].Start : sentence.Length;
                        pronoun = ClausePronoun(sentence, from, until);
                        if (pronoun is null && h + 1 == found.Count && !HasTitle(sentence, from, sentence.Length) &&
                            s + 1 < segments.Count && SameParagraph(text.Value, end, segments[s + 1].Start))
                        {
                            string next = text.Value[segments[s + 1].Start..segments[s + 1].End].TrimStart();
                            if (next.Length > 0 && next[0] is '他' or '她' or '它' && !(next.Length > 1 && next[1] == '们')) pronoun = next[0];
                        }
                    }
                    string shown = sentence.Trim();
                    hits.Add((found[h].Name, new(source.ChapterNumber, text.Map[start], shown.Length <= 120 ? shown : shown[..120] + "…", pronoun)));
                }
            }
        }
        return hits.GroupBy(x => x.Name, StringComparer.Ordinal).Select(g =>
        {
            var samples = g.Select(x => x.Sample).ToArray();
            return new NarrativeCharacter(g.Key, samples.Length, samples.Count(x => x.FollowingPronoun == '他'), samples.Count(x => x.FollowingPronoun == '她'),
                samples.Count(x => x.FollowingPronoun == '它'), samples.Min(x => x.ChapterNumber), samples.Max(x => x.ChapterNumber),
                Spread(samples, samplesPerCharacter));
        }).OrderByDescending(x => x.NarrationMentions).ThenBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Prefer sentences with a following pronoun, round-robin across chapters so changes over the story stay visible.</summary>
    internal static IReadOnlyList<NarrationSample> Spread(IReadOnlyList<NarrationSample> samples, int count)
    {
        var byChapter = samples.GroupBy(x => x.ChapterNumber).OrderBy(x => x.Key)
            .Select(g => new Queue<NarrationSample>(g.OrderByDescending(x => x.FollowingPronoun is not null).ThenBy(x => x.StartUtf16))).ToList();
        var result = new List<NarrationSample>();
        while (result.Count < count && byChapter.Any(q => q.Count > 0))
            foreach (var queue in byChapter.Where(q => q.Count > 0))
            {
                if (result.Count == count) break;
                result.Add(queue.Dequeue());
            }
        return result.OrderBy(x => x.ChapterNumber).ThenBy(x => x.StartUtf16).ToArray();
    }

    private const string ClauseBreaks = "，,；;：:、";

    /// <summary>The name starts the sentence or a clause, allowing a leading connector or title (说着/姐姐柳月影…).</summary>
    private static bool OpensClause(string sentence, int start)
    {
        int clause = start;
        while (clause > 0 && ClauseBreaks.IndexOf(sentence[clause - 1]) < 0) clause--;
        string before = sentence[clause..start].Trim();
        return before.Length == 0 || before.Length <= 4 && (AddressingDialogueSurvey.AddressTerms.Contains(before) ||
            before is "说着" or "于是" or "然后" or "这时" or "只见" or "此时" or "随即" or "接着" or "而" or "但");
    }

    /// <summary>First 他/她/它 that opens a later clause before `until`, with no intervening title.</summary>
    private static char? ClausePronoun(string text, int from, int until)
    {
        for (int i = from; i < until && i < text.Length; i++)
        {
            if (text[i] is not ('他' or '她' or '它')) continue;
            if (i + 1 < text.Length && text[i + 1] == '们') continue;          // plural is not this person
            int j = i - 1; while (j >= from && text[j] is ' ' or '　') j--;
            if (j < from || ClauseBreaks.IndexOf(text[j]) < 0) continue;       // not clause-initial (其他, 给她, 看着他…)
            return HasTitle(text, from, i) ? null : text[i];
        }
        return null;
    }

    private static bool HasTitle(string text, int from, int until) =>
        until > from && AddressingDialogueSurvey.AddressTerms.Any(t => text.AsSpan(from, until - from).IndexOf(t, StringComparison.Ordinal) >= 0);

    /// <summary>Narration segments in normalized text: sentence pieces outside dialogue brackets.</summary>
    private static List<(int Start, int End)> Narration(string text)
    {
        var inQuote = new bool[text.Length];
        for (int i = 0; i < text.Length; i++)
            foreach (var b in AddressingDialogueSurvey.Brackets)
                if (text[i] == b.Open && AddressingDialogueSurvey.FindClose(text, i, b) is int close and >= 0)
                {
                    for (int k = i; k <= close; k++) inQuote[k] = true;
                    i = close; break;
                }
        var result = new List<(int, int)>(); int startAt = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool boundary = i == text.Length || inQuote[i] || Terminators.IndexOf(text[i]) >= 0;
            if (!boundary) { if (startAt < 0) startAt = i; continue; }
            if (startAt >= 0 && text.AsSpan(startAt, i - startAt).Trim().Length > 0) result.Add((startAt, i));
            startAt = -1;
        }
        return result;
    }

    private static bool SameParagraph(string text, int end, int nextStart) => text.AsSpan(end, Math.Max(0, nextStart - end)).IndexOf('\n') < 0;
}
