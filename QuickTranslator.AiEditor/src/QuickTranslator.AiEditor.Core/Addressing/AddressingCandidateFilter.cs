using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Core.Addressing;

/// <summary>A candidate name the CPU pre-filter removed before it could become a task, and why. Names only, no source text.</summary>
public sealed record DroppedCandidate(string Name, string Reason);

/// <summary>
/// CPU pre-filter of junk character candidates (一下, 过来, 毕竟, 宁夕缓缓…) so no request is spent on them. Genre-neutral: no word
/// list, only what the book itself shows. Existing profile names and aliases are never dropped. Two rules:
/// <list type="number">
/// <item>Extension: a candidate that is a base candidate plus 1-3 trailing characters and is much rarer than the base. It is the
/// base followed by a verb, adverb or particle (宁夕缓缓, 宁夕这, 宁夕顿时), so only the base is kept; the text is still matched by
/// the base. It must be far rarer (5x), and the same tail must follow at least two different bases or the base must be 20x
/// commoner, so a real longer name (林青山 beside 林青) is not mistaken for it.</item>
/// <item>No person evidence: no 他/她/它 ever follows the name (nor any spelling of the same character) and it is never
/// the speaker or the addressee of a dialogue line, or it only speaks while not shaped like a name (no surname or 小/阿/老
/// prefix). People who are mentioned several times get a pronoun or a voice; words (过来, 关系) get neither.</item>
/// </list>
/// </summary>
internal static class CandidateFilter
{
    private const int ExtensionRatio = 5, StrongExtensionRatio = 20;

    public static IReadOnlyList<DroppedCandidate> Find(IReadOnlyList<CensusSource> sources, IReadOnlyList<string> names, IReadOnlySet<string> keep,
        IReadOnlyDictionary<string, int> frequency, CancellationToken ct)
    {
        int Freq(string name) => frequency.GetValueOrDefault(name);
        var dropped = new List<DroppedCandidate>();

        var extensions = names.Where(x => !keep.Contains(x)).Select(x => (Name: x, Base: names.Where(b => b.Length >= 2 && x.Length - b.Length is >= 1 and <= 3 &&
                x.StartsWith(b, StringComparison.Ordinal)).OrderByDescending(Freq).ThenBy(b => b, StringComparer.Ordinal).FirstOrDefault()))
            .Where(p => p.Base is not null && Freq(p.Base) >= ExtensionRatio * Math.Max(1, Freq(p.Name))).ToArray();
        var basesPerTail = extensions.GroupBy(p => p.Name[p.Base!.Length..], StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Base).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        foreach (var (name, baseName) in extensions)
            if (basesPerTail[name[baseName!.Length..]] >= 2 || Freq(baseName) >= StrongExtensionRatio * Math.Max(1, Freq(name)))
                dropped.Add(new(name, $"extends {baseName} (x{Freq(name)} vs x{Freq(baseName)})"));

        var gone = dropped.Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var rest = names.Where(n => !gone.Contains(n)).ToArray();
        if (rest.Length == 0) return dropped;
        // One character = a name and the spellings the CPU ties to it (秀兰姐 → 王秀兰): evidence of any spelling counts for the group.
        var variants = NameVariantSurvey.Analyze(sources, rest);
        var owner = variants.SelectMany(g => g.Variants.Select(v => (v.Form, g.FullName))).ToDictionary(x => x.Form, x => x.FullName, StringComparer.Ordinal);
        string Canonical(string name) => owner.GetValueOrDefault(name, name);
        var surface = rest.Concat(variants.SelectMany(v => v.Variants.Select(x => x.Form))).Distinct(StringComparer.Ordinal).ToArray();
        var pronouns = NarrativeReferenceSurvey.Analyze(sources, surface, 1, ct).GroupBy(x => Canonical(x.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.He + x.She + x.It), StringComparer.Ordinal);
        var talkers = AddressingDialogueSurvey.ExtractAll(sources, surface, ct).SelectMany(x => new[] { x.Speaker, x.Listener }).OfType<string>()
            .Select(Canonical).ToHashSet(StringComparer.Ordinal);
        foreach (var name in rest.Where(n => Canonical(n) == n))
        {
            var group = rest.Where(n => Canonical(n) == name).ToArray();
            if (group.Any(keep.Contains) || pronouns.GetValueOrDefault(name) > 0) continue;
            // Dialogue alone is weak: the speaker finder also latches onto words before a speech verb (想了一下：「…」), so a
            // speaker without any pronoun counts only when the spelling has the shape of a name.
            if (talkers.Contains(name) && NameShaped(name)) continue;
            dropped.AddRange(group.Select(n => new DroppedCandidate(n, $"no 他/她, {(talkers.Contains(name) ? "speaks but is not name-shaped" : "no dialogue")} (x{Freq(n)})")));
        }
        return dropped;
    }

    // A surname (compound, common or extended tier) or a nickname prefix (小夕, 阿秀, 老周) opens the spelling.
    private static bool NameShaped(string name) => name.Length >= 2 && (ChineseSurnames.HasCommonSurname(name) || ChineseSurnames.HasExtendedSurnameOnly(name) ||
        "小阿老".Contains(name[0]));
}
