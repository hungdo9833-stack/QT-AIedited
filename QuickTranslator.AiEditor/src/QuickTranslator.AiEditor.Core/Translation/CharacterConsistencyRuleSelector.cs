using System.Text;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed record CharacterConsistencySelection(
    string PromptBlock,
    IReadOnlyList<Guid> CharacterIds,
    IReadOnlySet<string> CoveredChineseTerms,
    int NarrativeRuleCount,
    int DialogueRuleCount,
    int OmittedRuleCount)
{
    public static CharacterConsistencySelection Empty { get; } =
        new("", [], new HashSet<string>(StringComparer.Ordinal), 0, 0, 0);
}

/// <summary>Selects only confirmed, source-relevant character rules. This class performs no I/O and calls no model.</summary>
public sealed class CharacterConsistencyRuleSelector(
    int maximumCharacters = 16,
    int maximumDialogueRules = 16,
    int maximumBlockCharacters = 2_400)
{
    private readonly object _cacheSync = new();
    private Guid _cachedProjectId;
    private DateTimeOffset _cachedUpdatedAt;
    private int _cachedProfileCount = -1;
    private NameOwner[] _cachedNames = [];

    public CharacterConsistencySelection Select(CharacterProject project, string sourceText)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(sourceText);
        if (!project.ConsistencyLockEnabled || sourceText.Length == 0) return CharacterConsistencySelection.Empty;
        if (maximumCharacters <= 0 || maximumDialogueRules < 0 || maximumBlockCharacters < 320)
            throw new InvalidOperationException("Giới hạn Character Consistency không hợp lệ.");

        var occurrences = FindUnambiguousOccurrences(project, sourceText);
        var relevant = occurrences.GroupBy(item => item.Profile.Id)
            .Select(group => new RelevantCharacter(group.First().Profile, group.Min(item => item.Start)))
            .OrderBy(item => item.FirstOffset).ThenBy(item => item.Profile.Id)
            .Take(maximumCharacters).ToArray();
        var relevantIds = relevant.Select(item => item.Profile.Id).ToHashSet();

        bool hasDialogueEvidence = sourceText.IndexOfAny(['“', '”', '「', '」', '『', '』', '"']) >= 0;
        var dialogueRules = hasDialogueEvidence
            ? relevant.SelectMany(owner => owner.Profile.Relationships.Select(relation => new DialogueRule(owner.Profile, relation)))
                .Where(item => item.Relation.Enabled && relevantIds.Contains(item.Relation.EffectiveListenerCharacterId) &&
                    item.Relation.EffectiveSpeakerCharacterId(item.Owner.Id) == item.Owner.Id &&
                    !string.IsNullOrWhiteSpace(item.Relation.SelfReference) &&
                    !string.IsNullOrWhiteSpace(item.Relation.AddresseeReference))
                .OrderBy(item => relevant.First(r => r.Profile.Id == item.Owner.Id).FirstOffset)
                .ThenBy(item => relevant.First(r => r.Profile.Id == item.Relation.EffectiveListenerCharacterId).FirstOffset)
                .ThenBy(item => item.Owner.Id).ThenBy(item => item.Relation.EffectiveListenerCharacterId)
                .Take(maximumDialogueRules).ToArray()
            : [];
        var narrative = relevant.Where(item => item.Profile.NarrativePronounLocked &&
            !string.IsNullOrWhiteSpace(item.Profile.PrimaryNarrativePronoun)).ToArray();
        if (narrative.Length == 0 && dialogueRules.Length == 0) return CharacterConsistencySelection.Empty;

        var requiredProfiles = narrative.Select(item => item.Profile.Id)
            .Concat(dialogueRules.SelectMany(item => new[] { item.Owner.Id, item.Relation.EffectiveListenerCharacterId }))
            .ToHashSet();
        var builder = new StringBuilder("CHARACTER CONSISTENCY (confirmed project rules):\n");
        int omitted = Math.Max(0, occurrences.Select(item => item.Profile.Id).Distinct().Count() - relevant.Length);
        var includedIds = new List<Guid>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        int narrativeCount = 0;
        int dialogueCount = 0;

        foreach (RelevantCharacter item in relevant.Where(item => requiredProfiles.Contains(item.Profile.Id)))
        {
            string identity = $"- CHARACTER {DisplaySourceName(item.Profile)} = {item.Profile.VietnameseName}";
            if (item.Profile.NarrativePronounLocked)
                identity += $"; narrative pronoun = {item.Profile.PrimaryNarrativePronoun} (LOCKED)";
            if (!TryAppendRule(builder, identity)) { omitted++; continue; }
            includedIds.Add(item.Profile.Id);
            if (item.Profile.NarrativePronounLocked) narrativeCount++;
            foreach (string term in SourceNames(item.Profile)) covered.Add(term);
        }

        var includedSet = includedIds.ToHashSet();
        foreach (DialogueRule item in dialogueRules)
        {
            Guid listenerId = item.Relation.EffectiveListenerCharacterId;
            if (!includedSet.Contains(item.Owner.Id) || !includedSet.Contains(listenerId)) { omitted++; continue; }
            CharacterProfile listener = relevant.First(r => r.Profile.Id == listenerId).Profile;
            string line = $"- DIALOGUE {item.Owner.VietnameseName} -> {listener.VietnameseName}: self = {item.Relation.SelfReference}; address = {item.Relation.AddresseeReference}" +
                (item.Relation.Locked ? " (LOCKED)" : "");
            if (!TryAppendRule(builder, line)) { omitted++; continue; }
            dialogueCount++;
        }

        if (narrativeCount == 0 && dialogueCount == 0) return CharacterConsistencySelection.Empty;
        const string policy = "Use these rules only when the character and dialogue roles are explicit in the source. Never invent a speaker, listener, relationship, or pronoun. Keep these rules out of the translated output.";
        if (builder.Length + policy.Length + 1 <= maximumBlockCharacters) builder.Append(policy);
        else omitted++;
        return new(builder.ToString().TrimEnd(), includedIds, covered, narrativeCount, dialogueCount, omitted);

        bool TryAppendRule(StringBuilder target, string line)
        {
            if (target.Length + line.Length + 1 > maximumBlockCharacters) return false;
            target.AppendLine(line);
            return true;
        }
    }

    private IReadOnlyList<NameOccurrence> FindUnambiguousOccurrences(CharacterProject project, string source)
    {
        NameOwner[] names = GetCompiledNames(project);
        var raw = new List<NameOccurrence>();
        foreach (NameOwner item in names)
        {
            int offset = 0;
            while ((offset = source.IndexOf(item.Name, offset, StringComparison.Ordinal)) >= 0)
            {
                raw.Add(new(item.Profile, item.Name, offset));
                offset += item.Name.Length;
            }
        }

        var accepted = new List<NameOccurrence>();
        foreach (NameOccurrence item in raw.OrderByDescending(item => item.Name.Length).ThenBy(item => item.Start))
        {
            if (accepted.Any(other => RangesOverlap(item.Start, item.Name.Length, other.Start, other.Name.Length))) continue;
            accepted.Add(item);
        }
        return accepted.OrderBy(item => item.Start).ThenByDescending(item => item.Name.Length).ToArray();
    }

    private NameOwner[] GetCompiledNames(CharacterProject project)
    {
        lock (_cacheSync)
        {
            if (_cachedProjectId == project.Id && _cachedUpdatedAt == project.UpdatedAt &&
                _cachedProfileCount == project.Profiles.Count) return _cachedNames;
            _cachedNames = project.Profiles
                .SelectMany(profile => SourceNames(profile).Select(name => new NameOwner(name, profile)))
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .Where(group => group.Select(item => item.Profile.Id).Distinct().Count() == 1)
                .Select(group => group.First()).ToArray();
            _cachedProjectId = project.Id;
            _cachedUpdatedAt = project.UpdatedAt;
            _cachedProfileCount = project.Profiles.Count;
            return _cachedNames;
        }
    }

    private static IEnumerable<string> SourceNames(CharacterProfile profile) =>
        new[] { profile.ChineseName }.Concat(profile.Aliases)
            .Where(name => !string.IsNullOrWhiteSpace(name) && name.Any(IsHan))
            .Select(name => name.Trim()).Distinct(StringComparer.Ordinal);

    private static string DisplaySourceName(CharacterProfile profile) =>
        SourceNames(profile).FirstOrDefault() ?? profile.VietnameseName;

    private static bool IsHan(char value) => value is >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff';
    private static bool RangesOverlap(int firstStart, int firstLength, int secondStart, int secondLength) =>
        firstStart < secondStart + secondLength && secondStart < firstStart + firstLength;

    private sealed record NameOwner(string Name, CharacterProfile Profile);
    private sealed record NameOccurrence(CharacterProfile Profile, string Name, int Start);
    private sealed record RelevantCharacter(CharacterProfile Profile, int FirstOffset);
    private sealed record DialogueRule(CharacterProfile Owner, CharacterRelationship Relation);
}
