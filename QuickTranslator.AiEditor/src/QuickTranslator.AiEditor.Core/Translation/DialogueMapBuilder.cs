using System.Text;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// Per-quote "who speaks to whom" for one segment, from the survey's CPU dialogue extraction run over the whole
/// chapter: a speaker named just before a segment boundary still attributes the first quote of the next segment.
/// Only strong evidence is used (speech verb or the narration sentence right before); a pronoun speaker or a listener
/// guessed from scene presence is left out. A kinship listener (母亲, “妈！”) is resolved through the speaker's own
/// confirmed rules: the one rule whose form of address matches. Performs no I/O and calls no model.
/// </summary>
public static class DialogueMapBuilder
{
    private const int MaximumLines = 20, SnippetCharacters = 18;
    private static readonly (string[] Zh, string[] Vi)[] Kinship =
    [
        (["母亲", "妈妈", "老妈", "娘亲", "妈", "娘"], ["mẹ", "má"]),
        (["父亲", "爸爸", "老爸", "爹爹", "爸", "爹"], ["ba", "bố", "cha"]),
        (["老公", "夫君", "相公"], ["anh", "chồng", "chàng"]),
        (["老婆", "娘子"], ["em", "vợ", "nàng"]),
        (["爷爷", "外公"], ["ông"]),
        (["奶奶", "外婆"], ["bà"]),
        (["哥哥", "大哥"], ["anh"]),
        (["姐姐", "大姐"], ["chị"]),
        (["弟弟", "妹妹"], ["em"]),
        (["叔叔"], ["chú"]),
        (["阿姨"], ["dì", "cô"]),
    ];
    // "看着自己的母亲：" — a kinship noun the narration turns towards, not one merely mentioned.
    private static readonly Regex FacingKin = new(
        @"(?:看着|看向|望着|望向|盯着|对着|冲着|朝着|对|向|朝|冲)(?:自己的|他的|她的)?(?<kin>" +
        string.Join("|", Kinship.SelectMany(k => k.Zh).OrderByDescending(z => z.Length)) + ")",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // As in SPC's alias table: generic gender nouns, pronouns and numbered placeholders never name a speaker.
    private static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
        { "男", "女", "男人", "女人", "男生", "女生", "男孩", "女孩", "他", "她", "它", "他们", "她们", "它们" };

    internal static bool IsCharacterName(string name) => !GenericNames.Contains(name) && !name.Any(char.IsAsciiDigit);

    public static string Build(ChapterDocument chapter, TranslationSegment segment)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(segment);
        var project = chapter.CharacterProject;
        if (project.Profiles.Count == 0 || string.IsNullOrEmpty(chapter.OriginalText) || segment.SourceText.Length == 0) return "";
        // A surface name belongs to one profile; a name shared by two profiles attributes nothing.
        var owners = project.Profiles.SelectMany(p => new[] { p.ChineseName }.Concat(p.Aliases)
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (Name: n.Trim(), Profile: p)))
            .Where(x => IsCharacterName(x.Name))
            .GroupBy(x => x.Name, StringComparer.Ordinal).Where(g => g.Select(x => x.Profile.Id).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Profile, StringComparer.Ordinal);
        if (owners.Count == 0) return "";

        int start = segment.StartUtf16, end = segment.StartUtf16 + segment.SourceText.Length;
        var lines = AddressingDialogueSurvey.Extract(new CensusSource(chapter.Id, chapter.ChapterNumber ?? 0, chapter.OriginalText), owners.Keys.ToArray())
            // Offsets are in the original text; the opening words guard against a segment whose start went stale.
            .Where(l => l.QuoteStartUtf16 >= start && l.QuoteStartUtf16 < end &&
                segment.SourceText.Contains(l.Quote[..Math.Min(8, l.Quote.Length)], StringComparison.Ordinal));
        var builder = new StringBuilder();
        int count = 0;
        foreach (var line in lines)
        {
            if (line.Speaker is null || line.SpeakerEvidence is not (SpeakerEvidence.Explicit or SpeakerEvidence.PreviousNarration) ||
                !owners.TryGetValue(line.Speaker, out var speaker)) continue;
            CharacterProfile? listener = line.Listener is not null && (line.ListenerEvidence is ListenerEvidence.Vocative or ListenerEvidence.Clause) &&
                owners.TryGetValue(line.Listener, out var named) && named.Id != speaker.Id ? named : null;
            listener ??= KinListener(project, speaker, line, chapter.ChapterNumber);
            // Weakest: the only other person named in this and the previous paragraph. Marked so the model can overrule it.
            bool likely = false;
            if (listener is null && line.Listener is not null && line.ListenerEvidence == ListenerEvidence.Scene &&
                owners.TryGetValue(line.Listener, out var present) && present.Id != speaker.Id) { listener = present; likely = true; }
            var rule = listener is null ? null : RuleFor(speaker, listener.Id, chapter.ChapterNumber);
            string who = listener is null ? $"{speaker.VietnameseName} → (listener unclear)"
                : $"{speaker.VietnameseName} → {listener.VietnameseName}{(likely ? " (likely)" : "")}";
            string terms = rule is null ? "" : $" (self={rule.SelfReference.Trim()}; address={rule.AddresseeReference.Trim()})";
            if (count == 0) builder.AppendLine("DIALOGUE MAP (quotes of this passage in order; speaker → listener):");
            builder.AppendLine($"- “{Snippet(line.Quote)}”: {who}{terms}");
            if (++count >= MaximumLines) break;
        }
        if (count == 0) return "";
        builder.Append("Translate each listed quote with that speaker's self-reference and form of address for that listener. Keep this map out of the translation.");
        return builder.ToString();
    }

    private static CharacterProfile? KinListener(CharacterProject project, CharacterProfile speaker, DialogueLine line, int? chapter)
    {
        var kin = line.VocativeTerms.Concat(FacingKin.Matches(line.LeadClause).Select(m => m.Groups["kin"].Value))
            .SelectMany(term => Kinship.Where(k => k.Zh.Contains(term)).SelectMany(k => k.Vi)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (kin.Count == 0) return null;
        var candidates = speaker.Relationships.Where(r => r.Enabled && r.AppliesTo(chapter) &&
                kin.Contains(r.AddresseeReference.Trim()) && r.EffectiveListenerCharacterId != speaker.Id)
            .Select(r => r.EffectiveListenerCharacterId).Distinct().ToArray();
        return candidates.Length == 1 ? project.Profiles.FirstOrDefault(p => p.Id == candidates[0]) : null;
    }

    private static CharacterRelationship? RuleFor(CharacterProfile speaker, Guid listenerId, int? chapter) => speaker.Relationships
        .Where(r => r.Enabled && r.EffectiveListenerCharacterId == listenerId && r.EffectiveSpeakerCharacterId(speaker.Id) == speaker.Id &&
            r.AppliesTo(chapter) && !string.IsNullOrWhiteSpace(r.SelfReference) && !string.IsNullOrWhiteSpace(r.AddresseeReference))
        .OrderBy(r => r.EffectiveFromChapter is null && r.EffectiveToChapter is null).FirstOrDefault();

    private static string Snippet(string quote)
    {
        string flat = quote.ReplaceLineEndings(" ").Trim();
        return flat.Length <= SnippetCharacters ? flat : flat[..SnippetCharacters] + "…";
    }
}
