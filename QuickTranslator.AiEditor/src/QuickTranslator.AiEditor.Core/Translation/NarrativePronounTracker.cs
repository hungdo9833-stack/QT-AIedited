using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public enum NarrativePronounConfidence { High, Ambiguous }

public sealed record NarrativePronounReference(
    int StartUtf16,
    char Pronoun,
    CharacterGender Gender,
    Guid? CharacterId,
    NarrativePronounConfidence Confidence);

/// <summary>One source-derived character per grammatical gender for a segment, if its high-confidence references agree.</summary>
public sealed record NarrativePronounHint(Guid? MaleCharacterId, Guid? FemaleCharacterId)
{
    public Guid? For(CharacterGender gender) => gender switch
    {
        CharacterGender.Male => MaleCharacterId,
        CharacterGender.Female => FemaleCharacterId,
        _ => null
    };
}

/// <summary>
/// Resolves singular third-person Chinese narration pronouns to confirmed character ids. Pure, bounded and CPU-only;
/// dialogue is ignored and ambiguous surface names never identify a character.
/// </summary>
public sealed class NarrativePronounTracker
{
    private const int MaximumWindowCharacters = 400;

    public IReadOnlyList<NarrativePronounReference> Scan(string originalText, CharacterProject project)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(project);
        if (originalText.Length == 0 || project.Profiles.Count == 0) return [];

        NameMention[] mentions = FindMentions(originalText, project);
        IReadOnlyList<TextSpan> quotes = QuoteRanges(originalText);
        var references = new List<NarrativePronounReference>();
        for (int position = 0; position < originalText.Length; position++)
        {
            char pronoun = originalText[position];
            CharacterGender gender = pronoun switch
            {
                '他' => CharacterGender.Male,
                '她' => CharacterGender.Female,
                _ => CharacterGender.Unknown
            };
            if (gender == CharacterGender.Unknown || IsPlural(originalText, position) ||
                quotes.Any(range => position >= range.Start && position < range.End)) continue;

            int windowStart = WindowStart(originalText, position);
            NameMention? antecedent = mentions.LastOrDefault(mention => mention.Start < position && mention.Start >= windowStart &&
                mention.Profile.Gender == gender && !quotes.Any(range => mention.Start >= range.Start && mention.Start < range.End));
            if (antecedent is null)
            {
                references.Add(new(position, pronoun, gender, null, NarrativePronounConfidence.Ambiguous));
                continue;
            }

            int sentenceStart = SentenceStart(originalText, antecedent.Start, windowStart);
            bool competing = mentions.Any(mention => mention.Profile.Gender == gender && mention.Profile.Id != antecedent.Profile.Id &&
                mention.Start >= sentenceStart && mention.Start < position &&
                !quotes.Any(range => mention.Start >= range.Start && mention.Start < range.End));
            references.Add(competing
                ? new(position, pronoun, gender, null, NarrativePronounConfidence.Ambiguous)
                : new(position, pronoun, gender, antecedent.Profile.Id, NarrativePronounConfidence.High));
        }
        return references;
    }

    public IReadOnlyList<NarrativePronounReference> ForSegment(string chapterText, TranslationSegment segment,
        IReadOnlyList<NarrativePronounReference> chapterReferences)
    {
        ArgumentNullException.ThrowIfNull(chapterText);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(chapterReferences);
        int start = segment.StartUtf16;
        int length = segment.SourceText.Length;
        int prefix = Math.Min(8, length);
        if (start < 0 || start > chapterText.Length - length ||
            !chapterText.AsSpan(start, prefix).SequenceEqual(segment.SourceText.AsSpan(0, prefix))) return [];
        int end = start + length;
        return chapterReferences.Where(reference => reference.StartUtf16 >= start && reference.StartUtf16 < end).ToArray();
    }

    public NarrativePronounHint? HintForSegment(string chapterText, TranslationSegment segment,
        IReadOnlyList<NarrativePronounReference> chapterReferences)
    {
        IReadOnlyList<NarrativePronounReference> segmentReferences = ForSegment(chapterText, segment, chapterReferences);
        Guid? male = AgreedCharacter(segmentReferences, CharacterGender.Male);
        Guid? female = AgreedCharacter(segmentReferences, CharacterGender.Female);
        return male is null && female is null ? null : new(male, female);
    }

    private static Guid? AgreedCharacter(IEnumerable<NarrativePronounReference> references, CharacterGender gender)
    {
        Guid[] ids = references.Where(reference => reference.Gender == gender &&
                reference.Confidence == NarrativePronounConfidence.High && reference.CharacterId is not null)
            .Select(reference => reference.CharacterId!.Value).Distinct().ToArray();
        return ids.Length == 1 ? ids[0] : null;
    }

    private static NameMention[] FindMentions(string text, CharacterProject project)
    {
        NameOwner[] owners = project.Profiles.SelectMany(profile => new[] { profile.ChineseName }.Concat(profile.Aliases)
                .Where(name => !string.IsNullOrWhiteSpace(name) && name.Any(IsHan))
                .Select(name => new NameOwner(name.Trim(), profile)))
            .GroupBy(owner => owner.Name, StringComparer.Ordinal)
            .Where(group => group.Select(owner => owner.Profile.Id).Distinct().Count() == 1)
            .Select(group => group.First()).OrderByDescending(owner => owner.Name.Length).ToArray();
        var accepted = new List<NameMention>();
        foreach (NameOwner owner in owners)
        {
            for (int start = text.IndexOf(owner.Name, StringComparison.Ordinal); start >= 0;
                 start = text.IndexOf(owner.Name, start + owner.Name.Length, StringComparison.Ordinal))
            {
                if (accepted.Any(item => Overlaps(start, owner.Name.Length, item.Start, item.Length))) continue;
                accepted.Add(new(start, owner.Name.Length, owner.Profile));
            }
        }
        return accepted.OrderBy(item => item.Start).ToArray();
    }

    private static int WindowStart(string text, int position)
    {
        int paragraphStart = position;
        int breaks = 0;
        while (paragraphStart > 0 && breaks < 3)
            if (text[--paragraphStart] == '\n') breaks++;
        if (breaks == 3) paragraphStart++;
        else paragraphStart = 0;
        return Math.Max(paragraphStart, Math.Max(0, position - MaximumWindowCharacters));
    }

    private static int SentenceStart(string text, int position, int floor)
    {
        int start = position;
        while (start > floor && text[start - 1] is not ('。' or '！' or '？' or '!' or '?' or '…' or '\n' or '\r')) start--;
        return start;
    }

    // 他们 and compounds that are not a person pronoun: 其他 (other), 他人/她人 (others), 他乡, 他日/他年, 他国, 吉他.
    private static bool IsPlural(string text, int position) =>
        position + 1 < text.Length && text[position + 1] is '们' or '人' or '乡' or '日' or '年' or '国' ||
        position > 0 && text[position - 1] is '其' or '吉';

    private static IReadOnlyList<TextSpan> QuoteRanges(string text)
    {
        var spans = new List<TextSpan>();
        var stack = new Stack<(char Close, int Start)>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (stack.Count > 0 && c == stack.Peek().Close)
            {
                (char _, int start) = stack.Pop();
                if (stack.Count == 0) spans.Add(new(start, i + 1));
                continue;
            }
            char? close = c switch { '「' => '」', '“' => '”', '『' => '』', '\"' => '\"', '‘' => '’', _ => null };
            if (close is not null && (stack.Count == 0 || c != stack.Peek().Close)) stack.Push((close.Value, i));
        }
        if (stack.Count > 0) spans.Add(new(stack.Min(item => item.Start), text.Length));
        return spans;
    }

    private static bool IsHan(char value) => value is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
    private static bool Overlaps(int firstStart, int firstLength, int secondStart, int secondLength) =>
        firstStart < secondStart + secondLength && secondStart < firstStart + firstLength;

    private sealed record NameOwner(string Name, CharacterProfile Profile);
    private sealed record NameMention(int Start, int Length, CharacterProfile Profile);
    private readonly record struct TextSpan(int Start, int End);
}
