using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public static class CharacterConsistencyProtection
{
    /// <summary>Preserves user-locked rules when a JSON import supplies matching character records.</summary>
    public static void PreserveLockedRules(CharacterProject current, CharacterProject incoming)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(incoming);
        if (current.ConsistencyLockEnabled) incoming.ConsistencyLockEnabled = true;

        foreach (CharacterProfile sourceOwner in current.Profiles)
        {
            CharacterProfile? targetOwner = FindMatching(incoming, sourceOwner);
            if (targetOwner is null) continue;
            if (sourceOwner.NarrativePronounLocked)
            {
                targetOwner.PrimaryNarrativePronoun = sourceOwner.PrimaryNarrativePronoun;
                targetOwner.NarrativePhases = [.. sourceOwner.NarrativePhases]; // Chapter phases are part of the locked rule.
                targetOwner.NarrativePronounLocked = true;
            }

            foreach (CharacterRelationship locked in sourceOwner.Relationships.Where(item => item.Locked))
            {
                CharacterProfile? sourceListener = current.Profiles.SingleOrDefault(profile =>
                    profile.Id == locked.EffectiveListenerCharacterId);
                CharacterProfile? targetListener = sourceListener is null ? null : FindMatching(incoming, sourceListener);
                if (targetListener is null) continue;
                int index = targetOwner.Relationships.FindIndex(item =>
                    item.EffectiveListenerCharacterId == targetListener.Id);
                var preserved = locked with
                {
                    TargetCharacterId = targetListener.Id,
                    SpeakerCharacterId = targetOwner.Id,
                    ListenerCharacterId = targetListener.Id
                };
                if (index >= 0) targetOwner.Relationships[index] = preserved;
                else targetOwner.Relationships.Add(preserved);
            }
        }
        incoming.Validate();
    }

    private static CharacterProfile? FindMatching(CharacterProject project, CharacterProfile source) =>
        project.Profiles.FirstOrDefault(profile => profile.Id == source.Id) ??
        (!string.IsNullOrWhiteSpace(source.ChineseName)
            ? project.Profiles.FirstOrDefault(profile => string.Equals(profile.ChineseName, source.ChineseName, StringComparison.Ordinal))
            : null) ??
        project.Profiles.FirstOrDefault(profile => string.Equals(profile.VietnameseName, source.VietnameseName,
            StringComparison.OrdinalIgnoreCase));
}
