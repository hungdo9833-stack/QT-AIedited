using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class SceneContextService
{
    private static readonly Regex SaidTo = new(
        @"(?<speaker>[\p{IsCJKUnifiedIdeographs}]{2,8})(?:对|向)(?<listener>[\p{IsCJKUnifiedIdeographs}]{2,8})(?:说|问|喊)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public IReadOnlyList<SceneContextSnapshot> RebuildChapter(BookIntelligenceState state,
        ChapterDocument chapter, ProjectResources resources)
    {
        int chapterNumber = state.Chapters.SingleOrDefault(x => x.ChapterId == chapter.Id)?.ChapterNumber ?? 0;
        string hash = Hash(chapter.OriginalText ?? "");
        var result = new List<SceneContextSnapshot>();
        foreach (TranslationSegment segment in chapter.Segments.OrderBy(x => x.Order))
        {
            CharacterProfile[] active = resources.Characters.Profiles.Where(p =>
                segment.SourceText.Contains(p.ChineseName, StringComparison.Ordinal) ||
                p.Aliases.Any(segment.SourceText.Contains)).ToArray();
            Match explicitDialogue = SaidTo.Match(segment.SourceText);
            Guid? speaker = explicitDialogue.Success ? Find(resources, explicitDialogue.Groups["speaker"].Value)?.Id : null;
            Guid? listener = explicitDialogue.Success ? Find(resources, explicitDialogue.Groups["listener"].Value)?.Id : null;
            SceneResolutionStatus status = speaker is not null && listener is not null
                ? SceneResolutionStatus.Verified
                : active.Length > 2 || ContainsDialogue(segment.SourceText)
                    ? SceneResolutionStatus.Ambiguous : SceneResolutionStatus.Unresolved;
            Guid[] activeIds = active.Select(x => x.Id).Concat(new Guid?[] { speaker, listener }.OfType<Guid>())
                .Distinct().ToArray();
            KnowledgeRelationship[] relations = RelationshipTimelineService.ValidRelationshipsAt(state, chapterNumber)
                .Where(x => activeIds.Contains(x.SubjectCharacterId) && activeIds.Contains(x.TargetCharacterId)).ToArray();
            AddressingRulePeriod[] addressing = RelationshipTimelineService.ApprovedAddressingAt(state, chapterNumber)
                .Where(x => activeIds.Contains(x.SpeakerCharacterId) && activeIds.Contains(x.ListenerCharacterId)).ToArray();
            result.Add(new SceneContextSnapshot
            {
                ProjectId = chapter.ProjectId ?? Guid.Empty, ChapterId = chapter.Id, StartSegmentOrder = segment.Order,
                EndSegmentOrder = segment.Order, ActiveCharacterIds = activeIds.ToList(), SpeakerCharacterId = speaker,
                ListenerCharacterId = listener, RelevantRelationshipIds = relations.Select(x => x.Id).ToList(),
                ValidAddressingRuleIds = addressing.Select(x => x.Id).ToList(), ResolutionStatus = status,
                SourceSha256 = hash
            });
        }
        state.SceneContexts.RemoveAll(x => x.ChapterId == chapter.Id);
        state.SceneContexts.AddRange(result);
        return result;
    }

    private static CharacterProfile? Find(ProjectResources resources, string name) => resources.Characters.Profiles
        .SingleOrDefault(x => x.ChineseName == name || x.Aliases.Contains(name, StringComparer.Ordinal));
    private static bool ContainsDialogue(string text) => text.IndexOfAny(['“', '”', '「', '」', '"']) >= 0;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

public interface IBookKnowledgeContextProvider
{
    BookKnowledgeContextSnapshot GetSnapshot(ChapterDocument chapter, TranslationSegment segment);
}

public sealed class BookKnowledgeContextProvider : IBookKnowledgeContextProvider
{
    private readonly BookIntelligenceState _state;
    private readonly ProjectResources _resources;

    public BookKnowledgeContextProvider(BookIntelligenceState state, ProjectResources resources)
    {
        _state = BookIntelligenceStore.Clone(state); _resources = resources.Clone();
        _state.Validate(); _resources.Validate();
        if (_state.ProjectId != _resources.ProjectId) throw new InvalidOperationException("Knowledge Base và Project Resources không cùng Project.");
    }

    public BookKnowledgeContextSnapshot GetSnapshot(ChapterDocument chapter, TranslationSegment segment)
    {
        if (chapter.ProjectId != _state.ProjectId) throw new InvalidOperationException("Chapter không thuộc Knowledge Base.");
        int number = _state.Chapters.SingleOrDefault(x => x.ChapterId == chapter.Id)?.ChapterNumber ?? int.MaxValue;
        SceneContextSnapshot? scene = _state.SceneContexts.SingleOrDefault(x => x.ChapterId == chapter.Id &&
            x.StartSegmentOrder <= segment.Order && x.EndSegmentOrder >= segment.Order);
        CharacterProfile[] relevant = _resources.Characters.Profiles.Where(p =>
            segment.SourceText.Contains(p.ChineseName, StringComparison.Ordinal) || p.Aliases.Any(segment.SourceText.Contains) ||
            scene?.ActiveCharacterIds.Contains(p.Id) == true).Take(12).ToArray();
        Guid[] ids = relevant.Select(x => x.Id).ToArray();
        KnowledgeRelationship[] relations = RelationshipTimelineService.ValidRelationshipsAt(_state, number)
            .Where(x => ids.Contains(x.SubjectCharacterId) && ids.Contains(x.TargetCharacterId)).Take(16).ToArray();
        AddressingRulePeriod[] addressing = RelationshipTimelineService.ApprovedAddressingAt(_state, number)
            .Where(x => ids.Contains(x.SpeakerCharacterId) && ids.Contains(x.ListenerCharacterId)).Take(16).ToArray();
        IntelligenceGlossarySuggestion[] terms = _state.Glossary.Where(x => x.Status == IntelligenceReviewStatus.Accepted &&
            x.Scope == GlossaryKnowledgeScope.Global && segment.SourceText.Contains(x.Chinese, StringComparison.Ordinal)).Take(24).ToArray();

        var characters = relevant.Select(x => new BookKnowledgeCharacter(x.Id, x.ChineseName, x.VietnameseName,
            x.Aliases.ToArray(), x.Gender, x.PrimaryNarrativePronoun, x.NarrativePronounLocked)).ToArray();
        var knowledgeRelations = relations.Select(x => new BookKnowledgeRelation(x.Id, x.SubjectCharacterId,
            x.TargetCharacterId, x.RelationType, x.RelationQualifier)).ToArray();
        // The addressing ledger writes the Character Project: its rules are the confirmed source and win over
        // older knowledge periods for the same direction. With the consistency lock on, the CHARACTER CONSISTENCY
        // block already carries them, so they are not repeated here.
        BookKnowledgeAddressing[] ledger = _resources.Characters.ConsistencyLockEnabled ? [] : LedgerAddressing(relevant, ids,
            number == int.MaxValue ? chapter.ChapterNumber : number);
        var knowledgeAddressing = ledger.Concat(addressing
                .Where(x => !ledger.Any(l => l.SpeakerId == x.SpeakerCharacterId && l.ListenerId == x.ListenerCharacterId))
                .Select(x => new BookKnowledgeAddressing(x.SpeakerCharacterId, x.ListenerCharacterId, x.SelfReference, x.AddresseeReference, x.Locked)))
            .Take(MaximumAddressingRules).ToArray();
        var knowledgeTerms = terms.Select(x => new BookKnowledgeTerm(x.Chinese, x.Vietnamese, x.Scope)).ToArray();
        var names = _resources.Characters.Profiles.ToDictionary(x => x.Id, x => x.VietnameseName);
        string block = BuildPromptBlock(characters, knowledgeRelations, knowledgeAddressing, knowledgeTerms,
            scene?.ResolutionStatus ?? SceneResolutionStatus.Unresolved, id => names.GetValueOrDefault(id, "?"));
        return new BookKnowledgeContextSnapshot(_state.Revision, chapter.ProjectId ?? Guid.Empty, chapter.Id, segment.Id,
            characters, knowledgeRelations, knowledgeAddressing, knowledgeTerms,
            scene?.ResolutionStatus ?? SceneResolutionStatus.Unresolved, block);
    }

    private const int MaximumAddressingRules = 24;

    /// <summary>Enabled, complete Character Project rules between relevant characters; a chapter-ranged rule beats a whole-book one.</summary>
    private static BookKnowledgeAddressing[] LedgerAddressing(IReadOnlyList<CharacterProfile> relevant, Guid[] ids, int? chapter) => relevant
        .SelectMany(owner => owner.Relationships.Select(rule => (Owner: owner, Rule: rule)))
        .Where(x => x.Rule.Enabled && x.Rule.EffectiveSpeakerCharacterId(x.Owner.Id) == x.Owner.Id &&
            ids.Contains(x.Rule.EffectiveListenerCharacterId) && x.Rule.EffectiveListenerCharacterId != x.Owner.Id &&
            x.Rule.AppliesTo(chapter) && !string.IsNullOrWhiteSpace(x.Rule.SelfReference) && !string.IsNullOrWhiteSpace(x.Rule.AddresseeReference))
        .GroupBy(x => (x.Owner.Id, x.Rule.EffectiveListenerCharacterId))
        .Select(g => g.OrderBy(x => x.Rule.EffectiveFromChapter is null && x.Rule.EffectiveToChapter is null).First())
        .Select(x => new BookKnowledgeAddressing(x.Owner.Id, x.Rule.EffectiveListenerCharacterId,
            x.Rule.SelfReference.Trim(), x.Rule.AddresseeReference.Trim(), x.Rule.Locked))
        .ToArray();

    // Names, never ids: the model cannot resolve a GUID to a character.
    private static string BuildPromptBlock(IReadOnlyCollection<BookKnowledgeCharacter> characters,
        IReadOnlyCollection<BookKnowledgeRelation> relations, IReadOnlyCollection<BookKnowledgeAddressing> addressing,
        IReadOnlyCollection<BookKnowledgeTerm> terms, SceneResolutionStatus scene, Func<Guid, string> name)
    {
        if (characters.Count == 0 && relations.Count == 0 && addressing.Count == 0 && terms.Count == 0) return "";
        var lines = new List<string> { "BOOK KNOWLEDGE (approved, chapter-scoped):" };
        lines.AddRange(characters.Select(x => $"- Character {x.ChineseName}={x.VietnameseName}; aliases=[{string.Join(",", x.Aliases)}]; narrative={x.NarrativePronoun}"));
        lines.AddRange(relations.Select(x => $"- Relation {name(x.SubjectId)} -> {name(x.TargetId)}: {x.Type} {x.Qualifier}".TrimEnd()));
        lines.AddRange(addressing.Select(x => $"- Dialogue {name(x.SpeakerId)} -> {name(x.ListenerId)}: self={x.SelfReference}; address={x.AddresseeReference}{(x.Locked ? " [LOCKED]" : "")}"));
        lines.AddRange(terms.Select(x => $"- Term {x.Chinese}={x.Vietnamese}"));
        if (addressing.Count > 0 && scene != SceneResolutionStatus.Verified)
            lines.Add("- Apply a dialogue rule to a quote when the passage shows who is speaking to whom.");
        return string.Join('\n', lines);
    }
}
