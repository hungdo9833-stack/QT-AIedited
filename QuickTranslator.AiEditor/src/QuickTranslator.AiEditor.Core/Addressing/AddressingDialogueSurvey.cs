using System.Text;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Core.Addressing;

/// <summary>
/// Explicit: lead clause or same-paragraph verb. PreviousNarration (weaker): a standalone quote paragraph whose preceding
/// narration sentence opens with this name. AiScene: a scene-task turn that passed CPU verification (never set by step 1).
/// </summary>
public enum SpeakerEvidence { Explicit, Pronoun, Unattributed, PreviousNarration, AiScene }
public enum ListenerEvidence { Vocative, Clause, Scene, Unknown, AiScene }

/// <summary>One quote of a dialogue scene with the narration since the previous quote (paragraph breaks kept as '\n').</summary>
public sealed record SceneQuote(DialogueLine Line, string Before);

/// <summary>
/// Consecutive quotes of one chapter separated by at most one short narration paragraph. Names lists every candidate
/// name seen in the scene or its context, in order of first appearance (surface forms, not merged).
/// </summary>
public sealed record DialogueScene(Guid ChapterId, int ChapterNumber, IReadOnlyList<SceneQuote> Quotes, string Trailer, IReadOnlyList<string> Names);

/// <summary>One quoted utterance with CPU-only, evidence-graded attribution. Offsets are UTF-16 in the original chapter text.</summary>
public sealed record DialogueLine(Guid ChapterId, int ChapterNumber, int QuoteStartUtf16, int QuoteLengthUtf16, string Quote, string LeadClause,
    string? Speaker, SpeakerEvidence SpeakerEvidence, string? SpeakerCandidate, string? Listener, ListenerEvidence ListenerEvidence,
    IReadOnlyList<string> SelfTerms, IReadOnlyList<string> VocativeTerms, IReadOnlyList<string> MentionedTerms, int SecondPerson, int HonorificSecondPerson);

public sealed record AddressingSample(int ChapterNumber, int QuoteStartUtf16, string LeadClause, string Quote,
    SpeakerEvidence SpeakerEvidence, ListenerEvidence ListenerEvidence, IReadOnlyList<string> VocativeTerms);

/// <summary>Aggregated hypotheses for one directed pair. A null listener groups lines whose listener is unknown.</summary>
public sealed record AddressingPair(string Speaker, string? Listener, int Lines, int ExplicitSpeakerLines, int VocativeListenerLines,
    int ClauseListenerLines, int SceneListenerLines, int FirstChapter, int LastChapter,
    IReadOnlyDictionary<string, int> SelfTerms, IReadOnlyDictionary<string, int> VocativeTerms,
    int SecondPerson, int HonorificSecondPerson, IReadOnlyList<AddressingSample> Samples);

public sealed record AddressingSurveyReport(int Chapters, long SourceUtf16, int DialogueLines, int ExplicitSpeakerLines, int PronounSpeakerLines,
    int UnattributedLines, int ListenerKnownLines, IReadOnlyList<string> CandidateNames, IReadOnlyList<AddressingPair> Pairs)
{
    /// <summary>Surface alias hints (小锋 ≈ 柳轻锋). Only used to avoid self-pairs; identities are never merged here.</summary>
    public IReadOnlyDictionary<string, string> AliasHints { get; init; } = new Dictionary<string, string>();
    /// <summary>Lines attributed from the preceding narration sentence (weaker than Explicit).</summary>
    public int NarrationSpeakerLines { get; init; }
}

/// <summary>
/// Addressing Ledger step 1 (see docs/ADDRESSING-LEDGER-DESIGN.vi.md): CPU-only dialogue extraction and
/// grouping by directed character pair. Produces hypotheses for review/AI, never approved facts, and never
/// infers a speaker from a pronoun as certain. Reads source text only.
/// </summary>
public static class AddressingDialogueSurvey
{
    private const int MaximumQuoteLength = 2_000;
    private const int MaximumLeadClause = 80;
    internal static readonly (char Open, char Close)[] Brackets = [('「', '」'), ('“', '”'), ('『', '』')];
    private static Regex Rule(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex SpeechVerbAtEnd = Rule(
        @"(?:说道|问道|答道|喊道|叫道|笑道|骂道|嚷道|喝道|怒道|叹道|哼道|应道|回道|开口道|低声道|轻声道|冷声道|柔声道|沉声道|大声道|继续道|又道|说|问|道|答|喊|叫|嚷|嘀咕|喃喃)[：:，,]?\s*$");
    // "」柳轻锋说道。" only within the same paragraph, name first, a few modifiers, then a speech verb that is
    // not part of another word (知道/道歉/说明…).
    private static readonly Regex SpeechVerbAfterQuote = Rule(@"^[，,]?[ 　]*(?<rest>[^\r\n。！？「」“”『』，,]{2,12}?)(?:说道|问道|答道|笑道|说|问)(?=[，。！？,.!?\s]|$)");
    private static readonly Regex AliasForm = Rule(@"^[小阿老](?<core>\p{IsCJKUnifiedIdeographs})(?:姐|哥|妹|弟|儿)?$");
    private static readonly Regex Connector = Rule(@"^(?:说着|于是|然后|这时|这时候|只见|此时|随即|接着|便|又|就|而|却|但|但是|只是)[，,]?");
    // Longest first. Ambiguous single characters that commonly occur inside words (孤, 姐 alone) are excluded.
    internal static readonly string[] SelfTerms =
        ["本姑娘", "本少爷", "本小姐", "小女子", "本座", "老夫", "在下", "本王", "本宫", "哀家", "妾身", "奴婢", "奴家", "老子", "人家", "小弟", "弟子", "晚辈", "贫道", "贫僧", "末将", "属下", "小人", "朕", "俺", "咱", "我"];
    internal static readonly string[] AddressTerms =
        ["臭小子", "亲爱的", "老太爷", "老太太", "老姐", "老哥", "老妈", "老爸", "姐姐", "哥哥", "弟弟", "妹妹", "妈妈", "爸爸", "爹爹", "娘亲", "父亲", "母亲",
         "爷爷", "奶奶", "外公", "外婆", "叔叔", "阿姨", "舅舅", "姑姑", "伯父", "伯母", "大哥", "二哥", "三哥", "大姐", "二姐", "小姐", "少爷", "公子", "姑娘",
         "夫人", "老爷", "先生", "师父", "师傅", "师尊", "师兄", "师姐", "师弟", "师妹", "前辈", "大人", "殿下", "陛下", "王爷", "掌门", "长老", "宗主",
         "老公", "老婆", "夫君", "相公", "娘子", "宝贝", "老师", "学长", "学姐", "老板", "大叔", "大爷", "大婶", "小子", "丫头", "笨蛋", "傻瓜", "爹", "娘", "妈", "爸"];
    private const string VocativeFollow = "，,！!？?…~。";

    public static AddressingSurveyReport Analyze(IReadOnlyList<CensusSource> sources, IReadOnlyCollection<string> names,
        int samplesPerPair = 12, CancellationToken cancellationToken = default)
    {
        var matcher = new NameMatcher(names);
        var lines = new List<DialogueLine>();
        foreach (var source in sources.OrderBy(x => x.ChapterNumber).ThenBy(x => x.ChapterId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lines.AddRange(Extract(source, matcher, cancellationToken));
        }
        var pairs = lines.Where(x => x.Speaker is not null).GroupBy(x => (x.Speaker!, x.Listener))
            .Select(g => Aggregate(g.Key.Item1, g.Key.Listener, g.ToArray(), samplesPerPair))
            .OrderByDescending(x => x.Listener is not null).ThenByDescending(x => x.ExplicitSpeakerLines + x.VocativeListenerLines)
            .ThenByDescending(x => x.Lines).ThenBy(x => x.Speaker, StringComparer.Ordinal).ThenBy(x => x.Listener, StringComparer.Ordinal).ToArray();
        return new(sources.Count, sources.Sum(x => (long)x.OriginalText.Length), lines.Count,
            lines.Count(x => x.SpeakerEvidence == SpeakerEvidence.Explicit), lines.Count(x => x.SpeakerEvidence == SpeakerEvidence.Pronoun),
            lines.Count(x => x.SpeakerEvidence == SpeakerEvidence.Unattributed), lines.Count(x => x.Listener is not null),
            matcher.Names, pairs) { AliasHints = matcher.AliasHints, NarrationSpeakerLines = lines.Count(x => x.SpeakerEvidence == SpeakerEvidence.PreviousNarration) };
    }

    public static IReadOnlyList<DialogueLine> Extract(CensusSource source, IReadOnlyCollection<string> names) =>
        Extract(source, new NameMatcher(names), CancellationToken.None);

    /// <summary>Every dialogue line of the given chapters in reading order (input for step 2 sampling).</summary>
    public static IReadOnlyList<DialogueLine> ExtractAll(IReadOnlyList<CensusSource> sources, IReadOnlyCollection<string> names, CancellationToken cancellationToken = default)
    {
        var matcher = new NameMatcher(names);
        return sources.OrderBy(x => x.ChapterNumber).ThenBy(x => x.ChapterId).SelectMany(x => Extract(x, matcher, cancellationToken)).ToArray();
    }

    private const int SceneGapCharacters = 160, SceneContextCharacters = 240, SceneTrailerCharacters = 80;
    private static readonly Regex RepeatedBreaks = Rule(@"\n{2,}");

    /// <summary>
    /// Step 2b input: contiguous dialogue runs. A run ends at the chapter end, when more than one narration paragraph
    /// (or a long one) separates two quotes, or at the size limits. Paragraph breaks are kept: they mark turn changes.
    /// </summary>
    public static IReadOnlyList<DialogueScene> Scenes(IReadOnlyList<CensusSource> sources, IReadOnlyCollection<string> names,
        int maximumQuotes = 10, int maximumCharacters = 1_400, CancellationToken cancellationToken = default)
    {
        var matcher = new NameMatcher(names);
        var result = new List<DialogueScene>();
        foreach (var source in sources.OrderBy(x => x.ChapterNumber).ThenBy(x => x.ChapterId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = NormalizedText.From(source.OriginalText);
            string value = text.Value;
            var run = new List<(DialogueLine Line, int Open, int Close)>();
            int size = 0, floor = 0;
            void Flush(int limit)
            {
                if (run.Count == 0) return;
                var quotes = new List<SceneQuote>();
                for (int i = 0; i < run.Count; i++)
                    quotes.Add(new(run[i].Line, i == 0 ? Context(value, floor, run[0].Open) : Tidy(value[(run[i - 1].Close + 1)..run[i].Open])));
                int close = run[^1].Close, end = value.IndexOf('\n', close + 1);
                end = Math.Min(Math.Min(end < 0 ? value.Length : end, limit), close + 1 + SceneTrailerCharacters);
                string trailer = Tidy(value[(close + 1)..Math.Max(close + 1, end)]);
                var seen = matcher.Find(string.Concat(quotes.Select(q => q.Before + "\n" + q.Line.Quote + "\n")) + trailer)
                    .Select(h => h.Name).Distinct(StringComparer.Ordinal).ToArray();
                result.Add(new(source.ChapterId, source.ChapterNumber, quotes, trailer, seen));
                floor = close + 1; run.Clear(); size = 0;
            }
            foreach (var line in Extract(source, matcher, cancellationToken))
            {
                int open = Array.BinarySearch(text.Map, line.QuoteStartUtf16) - 1;
                if (open < 0 || !Brackets.Any(b => b.Open == value[open])) continue;
                int close = open + 1 + line.Quote.Length;
                if (run.Count > 0)
                {
                    string gap = value[(run[^1].Close + 1)..open];
                    bool joins = gap.Trim().Length <= SceneGapCharacters && gap.Split('\n').Count(p => p.Trim().Length > 0) <= 1 &&
                        run.Count < maximumQuotes && size + gap.Length + line.Quote.Length <= maximumCharacters;
                    if (!joins) Flush(open);
                    else size += gap.Length;
                }
                if (run.Count == 0) size = Math.Min(SceneContextCharacters, open - floor);
                run.Add((line, open, close)); size += line.Quote.Length;
            }
            Flush(value.Length);
        }
        return result;
    }

    /// <summary>Narration before a scene: the lead of its first quote plus up to two previous paragraphs, never an earlier quote.</summary>
    private static string Context(string text, int floor, int open)
    {
        int start = open, breaks = 0;
        while (start > floor) { if (text[start - 1] == '\n' && ++breaks == 3) break; start--; }
        string context = text[start..open];
        if (context.Length > SceneContextCharacters)
        {
            // Long paragraphs: start at a sentence boundary instead of mid-word.
            context = context[^SceneContextCharacters..];
            int boundary = context.IndexOfAny(['。', '！', '？', '!', '?', '…', '\n']);
            if (boundary >= 0 && boundary + 1 < context.Length) context = context[(boundary + 1)..].TrimStart('…');
        }
        return Tidy(context).TrimStart('\n');
    }

    // Indentation and blank lines removed, one '\n' per paragraph break; scene labels cannot be forged by source text.
    private static string Tidy(string text) =>
        RepeatedBreaks.Replace(string.Join('\n', text.Split('\n').Select(l => l.Trim(' ', '　', '\t', '\r'))), "\n").Replace("⟦", "").Replace("⟧", "");

    /// <summary>Names (from <paramref name="names"/>) used as a vocative inside the quote: "宁夕，你…", "…啊，陆霆骁！".</summary>
    internal static IReadOnlyList<string> VocativeNames(string quote, IReadOnlyCollection<string> names)
    {
        var matcher = new NameMatcher(names);
        return Vocatives(quote, matcher.Find(quote)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<DialogueLine> Extract(CensusSource source, NameMatcher names, CancellationToken cancellationToken)
    {
        var text = NormalizedText.From(source.OriginalText);
        var result = new List<DialogueLine>();
        int position = 0, previousQuoteEnd = 0;
        var recentExplicit = new List<string>();
        while (position < text.Value.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int open = -1; (char Open, char Close) bracket = default;
            foreach (var b in Brackets)
            {
                int i = text.Value.IndexOf(b.Open, position);
                if (i >= 0 && (open < 0 || i < open)) { open = i; bracket = b; }
            }
            if (open < 0) break;
            int close = FindClose(text.Value, open, bracket);
            if (close < 0) { position = open + 1; continue; }
            string quote = text.Value[(open + 1)..close];
            string lead = LeadClause(text.Value, open, previousQuoteEnd);
            string after = text.Value[(close + 1)..Math.Min(text.Value.Length, close + 24)];
            var (speaker, speakerEvidence, listenerFromClause) = Attribute(lead, after, names);
            // Style common in romance/urban novels: a narration paragraph, then the quote alone in its own paragraph.
            if (speakerEvidence == SpeakerEvidence.Unattributed && lead.Length == 0 && OpensParagraph(text.Value, open) &&
                !IsSoundOnly(quote) && PreviousNarrationSentence(text.Value, open) is { } narration)
            {
                var guess = FromNarration(narration, names);
                // People rarely say their own name: if the quote names the guessed speaker, the narration subject is
                // more likely the one being answered. Drop the guess instead of recording a wrong speaker.
                if (guess.Speaker is null || !names.Find(quote).Any(h => names.SamePerson(h.Name, guess.Speaker)))
                {
                    (speaker, speakerEvidence, listenerFromClause) = guess;
                    lead = narration; // The AI and the reviewer see the sentence the guess came from.
                }
            }
            if (IsSoundOnly(quote)) { speaker = null; speakerEvidence = SpeakerEvidence.Unattributed; listenerFromClause = null; }
            string? candidate = null;
            if (speakerEvidence == SpeakerEvidence.Pronoun) candidate = recentExplicit.LastOrDefault();
            // Vocative names inside the utterance are the strongest listener evidence.
            string? listener = Vocatives(quote, names.Find(quote)).FirstOrDefault(x => !names.SamePerson(x, speaker));
            var listenerEvidence = listener is not null ? ListenerEvidence.Vocative : ListenerEvidence.Unknown;
            if (listener is null && listenerFromClause is not null && !names.SamePerson(listenerFromClause, speaker)) { listener = listenerFromClause; listenerEvidence = ListenerEvidence.Clause; }
            if (listener is null && speaker is not null)
            {
                var scene = names.Find(Paragraphs(text.Value, open)).Select(x => x.Name).Where(x => !names.SamePerson(x, speaker)).Distinct().ToArray();
                if (scene.Length == 1) { listener = scene[0]; listenerEvidence = ListenerEvidence.Scene; }
            }
            var (self, vocativeTerms, mentioned) = Terms(quote);
            int start = text.Map[open + 1], end = close > open + 1 ? text.Map[close - 1] + 1 : start;
            result.Add(new(source.ChapterId, source.ChapterNumber, start, end - start, quote, lead, speaker, speakerEvidence, candidate,
                listener, listenerEvidence, self, vocativeTerms, mentioned, Count(quote, "你") - Count(quote, "你们"), Count(quote, "您")));
            if (speakerEvidence == SpeakerEvidence.Explicit && speaker is not null) { recentExplicit.Add(speaker); if (recentExplicit.Count > 3) recentExplicit.RemoveAt(0); }
            previousQuoteEnd = close + 1; position = close + 1;
        }
        return result;
    }

    internal static int FindClose(string text, int open, (char Open, char Close) bracket)
    {
        int depth = 0;
        for (int i = open; i < text.Length && i - open <= MaximumQuoteLength; i++)
        {
            if (text[i] == bracket.Open) depth++;
            else if (text[i] == bracket.Close && --depth == 0) return i;
        }
        return -1;
    }

    private static string LeadClause(string text, int open, int floor)
    {
        int start = open;
        while (start > floor && open - start < MaximumLeadClause && "。！？!?\n」”』".IndexOf(text[start - 1]) < 0) start--;
        return text[start..open].Trim();
    }

    /// <summary>Speaker = first name opening the lead sentence when it ends with a speech verb, or a name right after the quote.</summary>
    private static (string? Speaker, SpeakerEvidence Evidence, string? ClauseListener) Attribute(string lead, string after, NameMatcher names)
    {
        if (SpeechVerbAtEnd.IsMatch(lead))
        {
            // The subject is the nearest sub-clause (from the verb backwards) that opens with a name or a pronoun:
            // "柳月影点点头，又有些高兴的说道" → 柳月影; "说着她伸手…说道" → pronoun, never guessed as certain.
            foreach (string part in lead.Split('，', ',').Reverse())
            {
                string clause = Subject(part);
                if (clause.Length == 0) continue;
                if (clause[0] is '他' or '她' or '它') return (null, SpeakerEvidence.Pronoun, null);
                var opening = names.Find(clause).FirstOrDefault(x => x.Start == 0);
                if (opening is null) continue;
                var others = names.Find(lead).Select(x => x.Name).Where(x => !names.SamePerson(x, opening.Name)).Distinct().ToArray();
                return (opening.Name, SpeakerEvidence.Explicit, others.Length == 1 ? others[0] : null);
            }
            return (null, SpeakerEvidence.Unattributed, null);
        }
        if (after.StartsWith('\n')) return (null, SpeakerEvidence.Unattributed, null); // Next paragraph narrates someone else.
        var tail = SpeechVerbAfterQuote.Match(after);
        if (tail.Success && names.Find(tail.Groups["rest"].Value) is { Count: > 0 } named && tail.Groups["rest"].Value.StartsWith(named[0].Name, StringComparison.Ordinal))
            return (named[0].Name, SpeakerEvidence.Explicit, null);
        return (null, SpeakerEvidence.Unattributed, null);
    }

    /// <summary>Sound effects in quotes (“砰！”, “啪啪”, “轰隆……”) are not speech by anyone.</summary>
    internal static bool IsSoundOnly(string quote)
    {
        var han = quote.Where(c => c is >= '一' and <= '鿿').ToArray();
        if (han.Length == 0) return !quote.Any(char.IsLetter); // "……" is not speech; "OK" is.
        return han.Length <= 6 && han.All(c => "砰啪咚嘭哗轰隆咔嚓叮当嘀嗒呼哒噗嗖嗡吱咣铛滴答扑通".Contains(c));
    }

    private static bool OpensParagraph(string text, int position)
    {
        int i = position - 1;
        while (i >= 0 && text[i] is ' ' or '　' or '\t') i--;
        return i < 0 || text[i] == '\n';
    }

    /// <summary>Last sentence of the paragraph just before, only when that paragraph is pure narration (no quotes).</summary>
    private static string? PreviousNarrationSentence(string text, int position)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, position - 1));
        if (lineStart <= 0) return null;
        int previousStart = text.LastIndexOf('\n', lineStart - 1) + 1;
        string previous = text[previousStart..lineStart].Trim();
        if (previous.Length == 0 || Brackets.Any(b => previous.IndexOf(b.Open) >= 0 || previous.IndexOf(b.Close) >= 0)) return null;
        string? last = previous.Split(['。', '！', '？', '!', '?', '…'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(last) ? null : last.Length <= MaximumLeadClause ? last : last[^MaximumLeadClause..];
    }

    /// <summary>Same subject rule as a lead clause, but graded PreviousNarration (weaker than an explicit speech verb).</summary>
    private static (string? Speaker, SpeakerEvidence Evidence, string? ClauseListener) FromNarration(string sentence, NameMatcher names)
    {
        foreach (string part in sentence.Split('，', ',').Reverse())
        {
            string clause = Subject(part);
            if (clause.Length == 0) continue;
            if (clause[0] is '他' or '她' or '它') return (null, SpeakerEvidence.Pronoun, null);
            var opening = names.Find(clause).FirstOrDefault(x => x.Start == 0);
            if (opening is null) continue;
            var others = names.Find(sentence).Select(x => x.Name).Where(x => !names.SamePerson(x, opening.Name)).Distinct().ToArray();
            return (opening.Name, SpeakerEvidence.PreviousNarration, others.Length == 1 ? others[0] : null);
        }
        return (null, SpeakerEvidence.Unattributed, null);
    }

    /// <summary>Strip leading connectors and a kinship/title prefix ("姐姐柳月影说道" → "柳月影说道").</summary>
    private static string Subject(string clause)
    {
        string value = Connector.Replace(clause.Trim(), "").TrimStart();
        string? title = AddressTerms.Where(t => value.StartsWith(t, StringComparison.Ordinal)).OrderByDescending(t => t.Length).FirstOrDefault();
        return title is null ? value : value[title.Length..];
    }

    private static IEnumerable<string> Vocatives(string quote, IReadOnlyList<NameMatcher.Hit> hits) => hits
        .Where(h => h.Start + h.Name.Length < quote.Length && (VocativeFollow.Contains(quote[h.Start + h.Name.Length]) || quote[h.Start + h.Name.Length] is '你' or '您')
            && (h.Start == 0 || "，,。！!？?：:；;…、 ".Contains(quote[h.Start - 1])))
        .Select(h => h.Name);

    private static (string[] Self, string[] Vocative, string[] Mentioned) Terms(string quote)
    {
        var self = new List<string>(); var vocative = new List<string>(); var mentioned = new List<string>();
        foreach (var hit in LongestTerms(quote, SelfTerms))
            if (!(hit.Term == "我" && hit.Start + 1 < quote.Length && quote[hit.Start + 1] == '们')) self.Add(hit.Term);
        foreach (var hit in LongestTerms(quote, AddressTerms))
        {
            int end = hit.Start + hit.Term.Length;
            bool isVocative = end >= quote.Length || VocativeFollow.Contains(quote[end]) || quote[end] is '你' or '您' or '啊' or '呀';
            (isVocative ? vocative : mentioned).Add(hit.Term);
        }
        return (self.ToArray(), vocative.ToArray(), mentioned.ToArray());
    }

    private static IEnumerable<(string Term, int Start)> LongestTerms(string text, string[] terms)
    {
        var taken = new bool[text.Length];
        foreach (string term in terms.OrderByDescending(x => x.Length))
            for (int i = text.IndexOf(term, StringComparison.Ordinal); i >= 0; i = text.IndexOf(term, i + 1, StringComparison.Ordinal))
            {
                if (Enumerable.Range(i, term.Length).Any(k => taken[k])) continue;
                for (int k = i; k < i + term.Length; k++) taken[k] = true;
                yield return (term, i);
            }
    }

    /// <summary>The current paragraph plus the previous one (scene participants for a weak listener guess).</summary>
    private static string Paragraphs(string text, int position)
    {
        int start = text.LastIndexOf('\n', Math.Max(0, position - 1));
        if (start > 0) start = text.LastIndexOf('\n', start - 1);
        int end = text.IndexOf('\n', position);
        return text[(start < 0 ? 0 : start)..(end < 0 ? text.Length : end)];
    }

    private static AddressingPair Aggregate(string speaker, string? listener, DialogueLine[] lines, int samplesPerPair)
    {
        static IReadOnlyDictionary<string, int> Tally(IEnumerable<string> terms) => terms.GroupBy(x => x, StringComparer.Ordinal)
            .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        int Score(DialogueLine x) => (x.SpeakerEvidence == SpeakerEvidence.Explicit ? 2 : 0) + x.ListenerEvidence switch
            { ListenerEvidence.Vocative => 3, ListenerEvidence.Clause => 2, ListenerEvidence.Scene => 1, _ => 0 } + (x.VocativeTerms.Count > 0 ? 2 : 0) + (x.SelfTerms.Any(t => t != "我") ? 1 : 0);
        var samples = lines.OrderByDescending(Score).ThenBy(x => x.ChapterNumber).ThenBy(x => x.QuoteStartUtf16).Take(samplesPerPair)
            .OrderBy(x => x.ChapterNumber).ThenBy(x => x.QuoteStartUtf16)
            .Select(x => new AddressingSample(x.ChapterNumber, x.QuoteStartUtf16, x.LeadClause, Shorten(x.Quote), x.SpeakerEvidence, x.ListenerEvidence, x.VocativeTerms)).ToArray();
        return new(speaker, listener, lines.Length, lines.Count(x => x.SpeakerEvidence == SpeakerEvidence.Explicit),
            lines.Count(x => x.ListenerEvidence == ListenerEvidence.Vocative), lines.Count(x => x.ListenerEvidence == ListenerEvidence.Clause),
            lines.Count(x => x.ListenerEvidence == ListenerEvidence.Scene), lines.Min(x => x.ChapterNumber), lines.Max(x => x.ChapterNumber),
            Tally(lines.SelectMany(x => x.SelfTerms)), Tally(lines.SelectMany(x => x.VocativeTerms)),
            lines.Sum(x => x.SecondPerson), lines.Sum(x => x.HonorificSecondPerson), samples);
    }

    private static string Shorten(string quote) => quote.Length <= 120 ? quote : quote[..120] + "…";
    private static int Count(string text, string value) { int n = 0; for (int i = 0; (i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0; i += value.Length) n++; return n; }

    /// <summary>Non-overlapping longest-name matching over a fixed candidate list.</summary>
    internal sealed class NameMatcher
    {
        public sealed record Hit(string Name, int Start);
        private readonly string[] _names;
        public IReadOnlyList<string> Names => _names;
        /// <summary>Short form → the single full name ending with the same character ("小锋" → "柳轻锋"). Ambiguous forms get no hint.</summary>
        public IReadOnlyDictionary<string, string> AliasHints { get; }
        public NameMatcher(IEnumerable<string> names)
        {
            _names = names.Where(x => x.Length >= 2 && !AddressTerms.Contains(x) && !SelfTerms.Contains(x))
                .Distinct(StringComparer.Ordinal).OrderByDescending(x => x.Length).ThenBy(x => x, StringComparer.Ordinal).ToArray();
            var hints = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string alias in _names)
            {
                var match = AliasForm.Match(alias);
                if (!match.Success) continue;
                char core = match.Groups["core"].Value[0];
                var owners = _names.Where(n => n.Length >= 3 && n != alias && n[^1] == core && !AliasForm.IsMatch(n)).ToArray();
                if (owners.Length == 1) hints[alias] = owners[0];
            }
            AliasHints = hints;
        }
        public bool SamePerson(string a, string? b) => b is not null && (a == b || AliasHints.GetValueOrDefault(a) == b || AliasHints.GetValueOrDefault(b) == a ||
            AliasHints.TryGetValue(a, out var x) && AliasHints.TryGetValue(b, out var y) && x == y);
        public IReadOnlyList<Hit> Find(string text)
        {
            var hits = new List<Hit>(); var taken = new bool[text.Length];
            foreach (string name in _names)
                for (int i = text.IndexOf(name, StringComparison.Ordinal); i >= 0; i = text.IndexOf(name, i + 1, StringComparison.Ordinal))
                {
                    if (Enumerable.Range(i, name.Length).Any(k => taken[k])) continue;
                    for (int k = i; k < i + name.Length; k++) taken[k] = true;
                    hits.Add(new(name, i));
                }
            return hits.OrderBy(x => x.Start).ToArray();
        }
    }

    /// <summary>
    /// Hard-wrapped web novels break lines every ~38 characters and indent real paragraphs. When most
    /// non-empty lines are indented, a line break followed by a non-space character is a soft wrap and is
    /// removed for analysis. <see cref="Map"/> keeps every analysed character's original UTF-16 offset.
    /// </summary>
    internal sealed record NormalizedText(string Value, int[] Map)
    {
        public static NormalizedText From(string original)
        {
            var lines = original.Split('\n');
            int nonEmpty = lines.Count(l => l.Trim('\r').Length > 0);
            // Indented paragraphs mark a hard-wrapped layout even when most lines are continuations; a break is
            // then a soft wrap only when the line does not end a sentence/quote and the next line is not indented.
            int indented = lines.Count(l => l.Length > 0 && l[0] is ' ' or '　' or '\t');
            bool hardWrapped = nonEmpty >= 4 && indented >= 2 && indented * 10 >= nonEmpty;
            var value = new StringBuilder(original.Length); var map = new List<int>(original.Length);
            for (int i = 0; i < original.Length; i++)
            {
                char c = original[i];
                if (c is '\r' or '\n')
                {
                    int next = i; while (next < original.Length && original[next] is '\r' or '\n') next++;
                    bool softWrap = hardWrapped && next < original.Length && next - i <= 2 && !char.IsWhiteSpace(original[next]) &&
                        original.AsSpan(i, next - i).Count('\n') == 1 && (i == 0 || "。！？!?」”』…".IndexOf(original[i - 1]) < 0);
                    if (!softWrap) { value.Append('\n'); map.Add(i); }
                    i = next - 1; continue;
                }
                value.Append(c); map.Add(i);
            }
            return new(value.ToString(), map.ToArray());
        }
    }
}
