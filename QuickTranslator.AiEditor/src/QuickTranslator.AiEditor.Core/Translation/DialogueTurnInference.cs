using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>A survey line plus optional, tentative attribution. The survey evidence itself is never changed.</summary>
public sealed record DialogueTurn(DialogueLine Line, CharacterProfile? Speaker, CharacterProfile? Listener, bool IsInferred);

/// <summary>CPU-only two-party turn inference over complete chapter scenes; performs no I/O or profile mutations.</summary>
public static class DialogueTurnInference
{
    private const int MaximumNarrationCharacters = 60, MaximumInferredRun = 4;
    private static readonly HashSet<string> GenericNames = new(StringComparer.Ordinal)
        { "男", "女", "男人", "女人", "男生", "女生", "男孩", "女孩", "他", "她", "它", "他们", "她们", "它们" };
    private static readonly Regex PronounSubject = new(
        @"^(?:(?:说着|于是|然后|这时候|这时|只见|此时|随即|接着|便|又|就|而|却|但是|但|只是)[，,]?\s*)*(?<pronoun>[他她它])(?<plural>们)?",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // SPC excludes generic gender nouns and numbered placeholders from the alias table.
    internal static bool IsCharacterName(string name) => !GenericNames.Contains(name) && !name.Any(c => c is >= '0' and <= '9');

    public static IReadOnlyList<DialogueTurn> Infer(IReadOnlyList<DialogueScene> scenes,
        IReadOnlyDictionary<string, CharacterProfile> owners)
    {
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(owners);
        var names = new AddressingDialogueSurvey.NameMatcher(owners.Keys.Where(IsCharacterName));
        var result = new List<DialogueTurn>();
        foreach (var scene in scenes)
        {
            CharacterProfile? previous = null, last = null, partyA = null, partyB = null;
            int inferredRun = 0;
            void Reset() { previous = null; last = null; partyA = null; partyB = null; inferredRun = 0; }
            foreach (var quote in scene.Quotes)
            {
                var line = quote.Line;
                CharacterProfile? speaker = line.Speaker is not null &&
                    line.SpeakerEvidence is SpeakerEvidence.Explicit or SpeakerEvidence.PreviousNarration &&
                    IsCharacterName(line.Speaker) && owners.TryGetValue(line.Speaker, out var attributed) ? attributed : null;
                bool inferred = false;
                CharacterProfile? listener = null;
                if (last is not null)
                {
                    // Include a new second anchor when checking its lead, but a third identity breaks the chain.
                    var first = partyA ?? last;
                    var second = partyB ?? speaker;
                    if (quote.Before.Trim().Length > MaximumNarrationCharacters ||
                        quote.Before.Split('\n').Count(p => !string.IsNullOrWhiteSpace(p)) > 1 ||
                        names.Find(quote.Before).Any(h => owners[h.Name].Id != first.Id && owners[h.Name].Id != second?.Id))
                        Reset();
                }
                // Attribution can be in the quote's trailer rather than its lead narration.
                if (speaker is not null && partyA is not null && partyB is not null &&
                    speaker.Id != partyA.Id && speaker.Id != partyB.Id) Reset();

                if (speaker is null && partyA is not null && partyB is not null && last is not null &&
                    inferredRun < MaximumInferredRun && !AddressingDialogueSurvey.IsSoundOnly(line.Quote))
                {
                    if (line.SpeakerEvidence == SpeakerEvidence.Unattributed && previous is not null && previous.Id != last.Id)
                        speaker = last.Id == partyA.Id ? partyB : partyA;
                    else if (line.SpeakerEvidence == SpeakerEvidence.Pronoun)
                    {
                        var gender = SubjectGender(line.LeadClause);
                        // An unknown profile gender could also match; do not turn missing data into certainty.
                        if (gender != CharacterGender.Unknown && partyA.Gender != CharacterGender.Unknown && partyB.Gender != CharacterGender.Unknown)
                        {
                            var matches = new[] { partyA, partyB }.Where(p => p.Gender == gender).ToArray();
                            if (matches.Length == 1) speaker = matches[0];
                        }
                    }
                    if (speaker is not null)
                    {
                        listener = speaker.Id == partyA.Id ? partyB : partyA;
                        // A named addressee can contradict alternation; leave that turn unknown instead of self-addressing.
                        if (line.Listener is not null && line.ListenerEvidence is ListenerEvidence.Vocative or ListenerEvidence.Clause &&
                            owners.TryGetValue(line.Listener, out var addressed) && addressed.Id != listener.Id)
                        { speaker = null; listener = null; }
                        else { inferred = true; inferredRun++; }
                    }
                }

                result.Add(new(line, speaker, listener, inferred));
                if (speaker is null) Reset();
                else
                {
                    previous = last; last = speaker;
                    if (partyA is null && previous is not null && previous.Id != last.Id)
                    { partyA = previous; partyB = last; }
                    if (!inferred) inferredRun = 0;
                }
            }
        }
        return result;
    }

    private static CharacterGender SubjectGender(string lead)
    {
        // Mirror the survey's nearest subject clause; object pronouns ("望着她") do not determine gender.
        foreach (string clause in lead.Split('，', ',').Reverse())
        {
            var match = PronounSubject.Match(clause.Trim());
            if (!match.Success) continue;
            if (match.Groups["plural"].Success) return CharacterGender.Unknown;
            return match.Groups["pronoun"].Value switch
            {
                "他" => CharacterGender.Male,
                "她" => CharacterGender.Female,
                _ => CharacterGender.Unknown
            };
        }
        return CharacterGender.Unknown;
    }
}
