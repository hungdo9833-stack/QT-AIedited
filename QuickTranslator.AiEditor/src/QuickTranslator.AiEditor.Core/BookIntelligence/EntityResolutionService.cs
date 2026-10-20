using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public static class EntityResolutionService
{
    public static CandidateIdentity Resolve(BookIntelligenceState state, ProjectResources resources,
        string chineseName, string vietnameseName, IEnumerable<string> aliases, IEnumerable<Guid> evidenceIds)
    {
        string[] names = new[] { chineseName }.Concat(aliases).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToArray();
        Guid[] evidence = evidenceIds.Distinct().ToArray();
        CandidateIdentity[] matches = state.CandidateIdentities.Where(c =>
            names.Contains(c.ChineseName, StringComparer.Ordinal) || c.Aliases.Any(names.Contains)).ToArray();
        CandidateIdentity identity;
        if (matches.Length == 1) identity = matches[0];
        else if (matches.Length > 1)
        {
            identity = matches[0];
            identity.Status = CandidateIdentityStatus.IdentityConflict;
            identity.Conflicts.Add($"Tên/bí danh giao nhau với candidate: {string.Join(", ", matches.Skip(1).Select(x => x.Id))}");
        }
        else
        {
            identity = new CandidateIdentity { ChineseName = chineseName.Trim(), VietnameseName = vietnameseName.Trim() };
            state.CandidateIdentities.Add(identity);
        }

        foreach (string alias in names.Where(x => x != identity.ChineseName && !identity.Aliases.Contains(x, StringComparer.Ordinal)))
            identity.Aliases.Add(alias);
        foreach (Guid id in evidence.Where(x => !identity.EvidenceHistory.Contains(x))) identity.EvidenceHistory.Add(id);
        AddBackwardEvidence(state, identity);

        CharacterProfile[] owners = resources.Characters.Profiles.Where(p => names.Any(n =>
            p.ChineseName == n || p.Aliases.Contains(n, StringComparer.Ordinal))).ToArray();
        if (owners.Length == 1 && identity.Status != CandidateIdentityStatus.ConfirmedIdentityMerge)
        {
            identity.ProposedExistingCharacterId = owners[0].Id;
            identity.Status = CandidateIdentityStatus.ProposedAliasLink;
        }
        else if (owners.Length > 1)
        {
            identity.Status = CandidateIdentityStatus.IdentityConflict;
            identity.Conflicts.Add("Bí danh đang thuộc nhiều hồ sơ Characters; cần người dùng quyết định.");
        }
        return identity;
    }

    public static Guid? ResolveStableId(BookIntelligenceState state, ProjectResources resources, string name)
    {
        CharacterProfile[] profiles = resources.Characters.Profiles.Where(p => p.ChineseName == name ||
            p.Aliases.Contains(name, StringComparer.Ordinal)).ToArray();
        if (profiles.Length == 1) return profiles[0].Id;
        CandidateIdentity[] candidates = state.CandidateIdentities.Where(c => c.ChineseName == name ||
            c.Aliases.Contains(name, StringComparer.Ordinal)).ToArray();
        return candidates.Length == 1 ? candidates[0].Id : null;
    }

    private static void AddBackwardEvidence(BookIntelligenceState state, CandidateIdentity identity)
    {
        string[] names = new[] { identity.ChineseName }.Concat(identity.Aliases).ToArray();
        foreach (CorpusEvidence evidence in state.Evidence.Where(e => e.IsCurrent &&
            names.Any(n => e.Excerpt.Contains(n, StringComparison.Ordinal))))
            if (!identity.EvidenceHistory.Contains(evidence.Id)) identity.EvidenceHistory.Add(evidence.Id);
    }
}

