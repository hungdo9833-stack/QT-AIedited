using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Core.Addressing;

public sealed record NameVariant(string Form, int Occurrences, string Rule);

/// <summary>Candidate ways the story may refer to one character (王秀兰 / 秀兰 / 小兰 / 兰儿 / 王小姐). Hypotheses only.</summary>
public sealed record NameVariantGroup(string FullName, string Surname, string GivenName, IReadOnlyList<NameVariant> Variants,
    IReadOnlyList<string> SharedForms);

/// <summary>
/// Addressing Ledger step 1c: generates conventional Chinese name variants for each full name, keeps only those
/// that occur in the source, and refuses forms that two characters could own (they are reported as shared).
/// Never merges identities; step 2 asks the AI to confirm and step 3 asks the user.
/// </summary>
public static class NameVariantSurvey
{
    private static readonly Regex Surname = new("^" + ChineseMentionDiscovery.Family, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly string[] SurnameTitles = ["小姐", "先生", "老师", "公子", "姑娘", "夫人", "大哥", "少爷", "总", "少", "姐", "哥"];

    public static IReadOnlyList<NameVariantGroup> Analyze(IReadOnlyList<CensusSource> sources, IReadOnlyCollection<string> names, int minimumOccurrences = 2)
    {
        string corpus = string.Join("\n", sources.Select(s => AddressingDialogueSurvey.NormalizedText.From(s.OriginalText).Value));
        var full = names.Where(n => n.Length is >= 3 and <= 5 && Surname.IsMatch(n)).Distinct(StringComparer.Ordinal).ToArray();
        var generated = full.ToDictionary(n => n, n => Generate(n).ToArray(), StringComparer.Ordinal);
        // Every generated form maps to all full names that could produce it; more than one owner = shared, never assigned.
        var owners = generated.SelectMany(x => x.Value.Select(v => (v.Form, Owner: x.Key))).Concat(full.Select(n => (Form: n, Owner: n)))
            .GroupBy(x => x.Form, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Owner).Distinct().ToArray(), StringComparer.Ordinal);
        var result = new List<NameVariantGroup>();
        foreach (string name in full)
        {
            var match = Surname.Match(name); string surname = match.Value, given = name[surname.Length..];
            var variants = new List<NameVariant>(); var shared = new List<string>();
            foreach (var (form, rule) in generated[name].DistinctBy(x => x.Form))
            {
                if (form == name || full.Contains(form, StringComparer.Ordinal)) continue; // Another full name is a person, not a variant.
                // 小子/老子… are ordinary words; a common addressing or self term is never a name variant.
                if (AddressingDialogueSurvey.AddressTerms.Contains(form) || AddressingDialogueSurvey.SelfTerms.Contains(form)) continue;
                int count = Count(corpus, form, name);
                if (count < minimumOccurrences && !names.Contains(form, StringComparer.Ordinal)) continue;
                if (owners[form].Length > 1) { shared.Add(form); continue; }
                variants.Add(new(form, count, rule));
            }
            result.Add(new(name, surname, given, variants.OrderByDescending(v => v.Occurrences).ThenBy(v => v.Form, StringComparer.Ordinal).ToArray(),
                shared.Order(StringComparer.Ordinal).ToArray()));
        }
        return result.OrderBy(x => x.FullName, StringComparer.Ordinal).ToArray();
    }

    internal static IEnumerable<(string Form, string Rule)> Generate(string fullName)
    {
        var match = Surname.Match(fullName);
        if (!match.Success || match.Length >= fullName.Length) yield break;
        string surname = match.Value, given = fullName[surname.Length..];
        char first = given[0], last = given[^1];
        if (given.Length >= 2) yield return (given, "GivenName");
        yield return ("小" + last, "Diminutive");
        yield return ("阿" + last, "Diminutive");
        if (given.Length >= 2) yield return ("小" + first, "Diminutive");
        yield return (last + "儿", "Diminutive");
        yield return (new string(last, 2), "Reduplication");
        yield return ("小" + last + "姐", "DiminutiveTitle");
        foreach (string suffix in new[] { "姐", "哥", "妹", "弟" })
            if (given.Length >= 2) yield return (given + suffix, "GivenNameTitle");
        foreach (string title in SurnameTitles) yield return (surname + title, "SurnameTitle");
        if (surname.Length == 1) yield return ("老" + surname, "SurnameFamiliar");
    }

    /// <summary>Occurrences of a form that are not just part of the full name itself (秀兰 inside 王秀兰).</summary>
    private static int Count(string corpus, string form, string fullName)
    {
        int count = 0;
        for (int i = corpus.IndexOf(form, StringComparison.Ordinal); i >= 0; i = corpus.IndexOf(form, i + form.Length, StringComparison.Ordinal))
        {
            int offset = fullName.IndexOf(form, StringComparison.Ordinal);
            bool insideFull = offset >= 0 && i - offset >= 0 && i - offset + fullName.Length <= corpus.Length &&
                string.CompareOrdinal(corpus, i - offset, fullName, 0, fullName.Length) == 0;
            if (!insideFull) count++;
        }
        return count;
    }
}
