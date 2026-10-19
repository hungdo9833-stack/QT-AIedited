using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record MemoryConflict(Guid LeftId, Guid RightId, string Reason);
public sealed record ChapterMemoryFact(Guid Id, Guid SubjectId, Guid? TargetId, MemoryFactKind Kind, string Value,
    KnowledgeRelationType RelationType, MemoryAssociationKind AssociationKind, string SelfReference, string AddresseeReference,
    MemoryTimeline Timeline, IReadOnlyList<Guid> EvidenceIds);
public sealed record ChapterMemoryEvent(Guid Id, TranslationImpactEventType Type, IReadOnlyList<Guid> EntityIds,
    MemoryEventEffect Effect, MemoryTimeline Timeline, IReadOnlyList<Guid> AffectedFactIds, IReadOnlyList<Guid> EvidenceIds);
public sealed record ChapterEntityMemory(Guid EntityId, IReadOnlyList<string> Names,
    IReadOnlyList<ChapterMemoryFact> Facts);
public sealed record ChapterMemorySnapshot(int ChapterNumber, IReadOnlyList<ChapterEntityMemory> Entities,
    IReadOnlyList<KnowledgeRelationship> Relationships, IReadOnlyList<AddressingRulePeriod> Addressing,
    IReadOnlyList<ChapterMemoryEvent> Events);

/// <summary>Immutable source-backed audit/query snapshot. Never connected to the translation provider in 2A.</summary>
public sealed class EntityMemoryQuery
{
    internal EntityMemoryState State { get; }
    private readonly Dictionary<Guid, CensusSource> _sources;
    private readonly Dictionary<Guid, string> _hashes;
    private readonly Dictionary<Guid, MemoryEvidence> _evidence;
    private readonly Dictionary<Guid, EntityMemoryFact> _facts;
    private readonly Dictionary<Guid, TranslationImpactEvent> _events;
    private readonly Dictionary<Guid, MemoryReviewDecision> _reviews;
    private readonly Dictionary<Guid, bool> _fresh = [];
    private readonly Dictionary<string, string> _census = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _superseded = [];
    private IReadOnlyList<MemoryConflict>? _conflicts;

    public EntityMemoryQuery(EntityMemoryState state, IReadOnlyList<CensusSource> sources)
    {
        State = EntityMemoryStore.Clone(state); State.Validate();
        if (sources.Any(x => x.ChapterId == Guid.Empty || x.ChapterNumber <= 0) ||
            sources.Select(x => x.ChapterId).Distinct().Count() != sources.Count || sources.Select(x => x.ChapterNumber).Distinct().Count() != sources.Count)
            throw new InvalidDataException("Duplicate/invalid active source identity or chapter number.");
        _sources = sources.ToDictionary(x => x.ChapterId);
        _hashes = sources.ToDictionary(x => x.ChapterId, x => CorpusIntegrityAuditor.SourceHash(x.OriginalText));
        _evidence = State.Evidence.ToDictionary(x => x.Id); _facts = State.Facts.ToDictionary(x => x.Id); _events = State.Events.ToDictionary(x => x.Id);
        _reviews = State.Reviews.GroupBy(x => x.TargetId).ToDictionary(x => x.Key, x => x.Last());
        foreach (var group in State.Facts.Select(x => (x.Id, x.Provenance.LineageKey)).Concat(State.Events.Select(x => (x.Id, x.Provenance.LineageKey)))
                     .Where(x => x.LineageKey.Length > 0).GroupBy(x => x.LineageKey, StringComparer.Ordinal))
            foreach (var prior in group.SkipLast(1)) _superseded.Add(prior.Id);
        string[] literals = State.Facts.Select(x => x.Provenance).Concat(State.Events.Select(x => x.Provenance))
            .Where(x => x.CensusName.Length > 0).Select(x => x.CensusName).Distinct(StringComparer.Ordinal).ToArray();
        if (literals.Length > 0)
        {
            var report = CharacterMentionCensus.Scan(State.ProjectId, 0, sources,
                new ProjectResources { ProjectId = State.ProjectId, Characters = new CharacterProject { Id = State.ProjectId } },
                new BookIntelligenceState { ProjectId = State.ProjectId }, reviewNames: literals);
            foreach (var candidate in report.Candidates) _census[candidate.ChineseName] = CharacterMentionCensus.Fingerprint(candidate);
        }
    }

    public MemoryTimeline Timeline(Guid id) => _reviews.TryGetValue(id, out var review) ? review.Timeline :
        _facts.TryGetValue(id, out var fact) ? fact.Timeline : _events[id].Timeline;
    public MemoryReviewStatus RecordedStatus(Guid id) => _reviews.TryGetValue(id, out var review) ? review.Status :
        _facts.TryGetValue(id, out var fact) ? fact.InitialStatus : _events[id].InitialStatus;
    public IReadOnlyList<Guid> EvidenceIds(Guid id) => _facts.TryGetValue(id, out var fact) ? fact.EvidenceIds : _events[id].EvidenceIds;
    public bool EvidenceCurrent(Guid id)
    {
        var e = _evidence[id];
        return _sources.TryGetValue(e.ChapterId, out var source) && source.ChapterNumber == e.ChapterNumber && _hashes[e.ChapterId] == e.SourceSha256 &&
            (long)e.StartUtf16 + e.LengthUtf16 <= source.OriginalText.Length && !Splits(source.OriginalText, e.StartUtf16) && !Splits(source.OriginalText, e.StartUtf16 + e.LengthUtf16);
    }
    private static bool Splits(string text, int index) => index > 0 && index < text.Length && char.IsHighSurrogate(text[index - 1]) && char.IsLowSurrogate(text[index]);
    public bool IsFresh(Guid id)
    {
        if (_fresh.TryGetValue(id, out bool result)) return result;
        var provenance = _facts.TryGetValue(id, out var fact) ? fact.Provenance : _events[id].Provenance;
        result = !_superseded.Contains(id) && EvidenceIds(id).Count > 0 && EvidenceIds(id).All(EvidenceCurrent) &&
            (provenance.CensusName.Length == 0 || _census.GetValueOrDefault(provenance.CensusName) == provenance.CensusFingerprint) &&
            (fact is null || fact.InputFactIds.All(input => IsFresh(input) && RecordedStatus(input) != MemoryReviewStatus.Rejected &&
                (fact.Provenance.Origin != MemoryOrigin.Rule || RecordedStatus(input) == MemoryReviewStatus.Confirmed)));
        if (result && fact is not null && fact.Provenance.Key.StartsWith("fact:semantic-confirmation:", StringComparison.Ordinal))
            result = fact.Provenance.RuleId == SemanticDependencies.Fingerprint(this, fact.InputFactIds);
        if (result && fact is null && provenance.Key.StartsWith("event:semantic-confirmation:", StringComparison.Ordinal))
        {
            var inputs = SemanticDependencies.EventInputs(provenance);
            result = inputs.All(_facts.ContainsKey) && provenance.RuleId == SemanticDependencies.Fingerprint(this, inputs);
        }
        _fresh[id] = result; return result;
    }
    internal MemoryReviewStatus SourceStatus(Guid id) => _superseded.Contains(id) || EvidenceIds(id).Count > 0 && !IsFresh(id) ? MemoryReviewStatus.Stale : RecordedStatus(id);
    public IReadOnlyList<MemoryConflict> Conflicts => _conflicts ??= EntityMemoryConflicts.Find(this);
    public MemoryReviewStatus Status(Guid id)
    {
        var status = SourceStatus(id);
        if (status is MemoryReviewStatus.Rejected or MemoryReviewStatus.Stale) return status;
        return Conflicts.Any(x => (x.LeftId == id || x.RightId == id) &&
            (status != MemoryReviewStatus.Confirmed || SourceStatus(x.LeftId == id ? x.RightId : x.LeftId) == MemoryReviewStatus.Confirmed))
            ? MemoryReviewStatus.Conflict : status;
    }
    public bool Confirmed(Guid id) => Status(id) == MemoryReviewStatus.Confirmed && IsFresh(id) &&
        (!_facts.TryGetValue(id, out var fact) || fact.InputFactIds.All(x => Status(x) is not (MemoryReviewStatus.Stale or MemoryReviewStatus.Rejected or MemoryReviewStatus.Conflict)));

    internal MemoryTimeline ConflictTimeline(Guid id)
    {
        var time = Timeline(id);
        var end = State.Events.Where(x => x.Effect == MemoryEventEffect.End && x.AffectedFactIds.Contains(id) &&
            SourceStatus(x.Id) == MemoryReviewStatus.Confirmed && IsFresh(x.Id) && Timeline(x.Id).EffectiveFromChapter is not null && Timeline(x.Id).RevealedAtChapter is not null &&
            !State.Events.Any(other => other.Effect == MemoryEventEffect.Activate && other.AffectedFactIds.Contains(id) &&
                SourceStatus(other.Id) == MemoryReviewStatus.Confirmed && IsFresh(other.Id) && Timeline(other.Id).EffectiveFromChapter == Timeline(x.Id).EffectiveFromChapter))
            .Select(x => Timeline(x.Id).EffectiveFromChapter).Min();
        return end is int stop ? time with { EffectiveToChapter = Math.Min(time.EffectiveToChapter ?? int.MaxValue, stop - 1) } : time;
    }

    public CorpusEvidence Navigation(Guid evidenceId)
    {
        if (!EvidenceCurrent(evidenceId)) throw new InvalidOperationException("Evidence is stale; refresh source before navigation.");
        var e = _evidence[evidenceId]; var source = _sources[e.ChapterId].OriginalText;
        return new() { Id = e.CorpusEvidenceId ?? e.Id, ChapterId = e.ChapterId, SourceSha256 = e.SourceSha256,
            StartUtf16 = e.StartUtf16, LengthUtf16 = e.LengthUtf16, Excerpt = source.Substring(e.StartUtf16, e.LengthUtf16), IsCurrent = true };
    }

    public ChapterMemorySnapshot AtChapter(int chapterNumber)
    {
        if (chapterNumber <= 0) throw new ArgumentOutOfRangeException(nameof(chapterNumber));
        var events = State.Events.Where(x => Confirmed(x.Id) && Timeline(x.Id).VisibleAt(chapterNumber)).ToArray();
        bool Ended(Guid factId)
        {
            // End is a point change, not a one-chapter pause. A later explicit activation
            // can change the ledger effect, but cannot override a fact's own expiry.
            var latest = State.Events.Where(x => x.AffectedFactIds.Contains(factId) && x.Effect != MemoryEventEffect.None && Confirmed(x.Id) &&
                Timeline(x.Id).EffectiveFromChapter <= chapterNumber && Timeline(x.Id).RevealedAtChapter <= chapterNumber)
                .OrderBy(x => Timeline(x.Id).EffectiveFromChapter).ThenBy(x => x.Id).LastOrDefault();
            return latest?.Effect == MemoryEventEffect.End;
        }
        bool InputsVisible(EntityMemoryFact fact) => fact.Provenance.Origin != MemoryOrigin.Rule && !fact.Provenance.Key.StartsWith("fact:semantic-confirmation:", StringComparison.Ordinal) || fact.InputFactIds.All(id =>
            Timeline(id).VisibleAt(chapterNumber) && !Ended(id) && InputsVisible(_facts[id]));
        var visible = State.Facts.Where(x => x.Provenance.Origin != MemoryOrigin.AuditOnly && Confirmed(x.Id) &&
            Timeline(x.Id).VisibleAt(chapterNumber) && !Ended(x.Id) && InputsVisible(x)).ToArray();
        // Historical knowledge may overlap until a later revelation explains an ending.
        // Do not guess a winner or leak that future ending to resolve today's conflict.
        var ambiguous = EntityMemoryConflicts.FindFacts(visible, _ => new MemoryTimeline(1)).SelectMany(x => new[] { x.LeftId, x.RightId }).ToHashSet();
        visible = visible.Where(x => !ambiguous.Contains(x.Id)).ToArray();
        var projection = new BookIntelligenceState { ProjectId = State.ProjectId };
        foreach (var fact in visible)
        {
            var time = Timeline(fact.Id);
            if (fact.Kind == MemoryFactKind.Relationship)
                projection.RelationshipGraph.Add(new() { Id = fact.Id, SubjectCharacterId = fact.SubjectId, TargetCharacterId = fact.TargetId!.Value,
                    RelationType = fact.RelationType, RelationQualifier = fact.Value, Status = KnowledgeRelationshipStatus.Confirmed,
                    ValidFromChapter = time.EffectiveFromChapter, ValidToChapter = time.EffectiveToChapter, RevealedAtChapter = time.RevealedAtChapter,
                    FirstObservedChapter = time.DiscoveredAtChapter, EvidenceIds = fact.EvidenceIds.ToList() });
            if (fact.Kind == MemoryFactKind.Addressing)
                projection.AddressingTimeline.Add(new() { Id = fact.Id, SpeakerCharacterId = fact.SubjectId, ListenerCharacterId = fact.TargetId!.Value,
                    SelfReference = fact.SelfReference, AddresseeReference = fact.AddresseeReference, Approved = true,
                    ValidFromChapter = time.EffectiveFromChapter, ValidToChapter = time.EffectiveToChapter, RevealedAtChapter = time.RevealedAtChapter,
                    EvidenceIds = fact.EvidenceIds.ToList() });
        }
        // Audit labels and hidden canonical identities are deliberately not returned.
        var entities = visible.GroupBy(x => x.SubjectId).Select(g => new ChapterEntityMemory(g.Key,
            g.Where(x => x.Kind is MemoryFactKind.PrimaryName or MemoryFactKind.Alias or MemoryFactKind.Nickname).Select(x => x.Value).Distinct(StringComparer.Ordinal).ToArray(),
            g.Select(x => new ChapterMemoryFact(x.Id, x.SubjectId, x.TargetId, x.Kind, x.Value, x.RelationType, x.AssociationKind,
                x.SelfReference, x.AddresseeReference, Timeline(x.Id), x.EvidenceIds)).ToArray())).ToArray();
        return new(chapterNumber, entities, RelationshipTimelineService.ValidRelationshipsAt(projection, chapterNumber),
            RelationshipTimelineService.ApprovedAddressingAt(projection, chapterNumber), events.Select(x => new ChapterMemoryEvent(x.Id, x.Type,
                x.EntityIds, x.Effect, Timeline(x.Id), x.AffectedFactIds, x.EvidenceIds)).ToArray());
    }
}

public static class EntityMemoryConflicts
{
    public static bool Overlap(MemoryTimeline a, MemoryTimeline b) =>
        (a.EffectiveFromChapter ?? 1) <= (a.EffectiveToChapter ?? int.MaxValue) && (b.EffectiveFromChapter ?? 1) <= (b.EffectiveToChapter ?? int.MaxValue) &&
        (a.EffectiveFromChapter ?? 1) <= (b.EffectiveToChapter ?? int.MaxValue) && (b.EffectiveFromChapter ?? 1) <= (a.EffectiveToChapter ?? int.MaxValue);
    public static IReadOnlyList<MemoryConflict> Find(EntityMemoryQuery query)
    {
        var facts = query.State.Facts.Where(x => query.SourceStatus(x.Id) is not (MemoryReviewStatus.Rejected or MemoryReviewStatus.Stale)).ToArray();
        var result = FindFacts(facts, query.ConflictTimeline).ToList();
        foreach (var group in query.State.Events.Where(x => x.Effect != MemoryEventEffect.None && query.SourceStatus(x.Id) is not (MemoryReviewStatus.Rejected or MemoryReviewStatus.Stale))
                     .SelectMany(x => x.AffectedFactIds.Select(id => (Fact: id, Event: x))).GroupBy(x => x.Fact))
        {
            var array = group.Select(x => x.Event).ToArray();
            for (int i = 0; i < array.Length; i++) for (int j = i + 1; j < array.Length; j++)
                if (array[i].Effect != array[j].Effect && query.Timeline(array[i].Id).EffectiveFromChapter == query.Timeline(array[j].Id).EffectiveFromChapter)
                    result.Add(new(array[i].Id, array[j].Id, "Opposite event effects at the same effective chapter."));
        }
        return result;
    }
    internal static IReadOnlyList<MemoryConflict> FindFacts(IReadOnlyList<EntityMemoryFact> facts, Func<Guid, MemoryTimeline> timeline)
    {
        var result = new List<MemoryConflict>();
        // Compare only relevant pairs, not every fact in a large corpus.
        foreach (var group in facts.GroupBy(x => (x.SubjectId, x.TargetId, x.Kind)))
        {
            var array = group.ToArray();
            for (int i = 0; i < array.Length; i++) for (int j = i + 1; j < array.Length; j++)
            {
                var a = array[i]; var b = array[j];
                if (!Overlap(timeline(a.Id), timeline(b.Id))) continue;
                bool conflict = a.Kind == MemoryFactKind.PrimaryName && a.Value != b.Value ||
                    a.Kind == MemoryFactKind.Addressing && (a.SelfReference != b.SelfReference || a.AddresseeReference != b.AddresseeReference) ||
                    a.Kind == MemoryFactKind.Relationship && DifferentParentRole(a.RelationType, b.RelationType);
                if (conflict) result.Add(new(a.Id, b.Id, "Overlapping incompatible primary name, parent role or directional addressing. Human review required."));
            }
        }
        foreach (var group in facts.Where(x => x.Kind == MemoryFactKind.Relationship && x.RelationType is KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother)
                     .GroupBy(x => (x.TargetId, x.RelationType)))
        {
            var array = group.ToArray();
            for (int i = 0; i < array.Length; i++) for (int j = i + 1; j < array.Length; j++)
                if (array[i].SubjectId != array[j].SubjectId && Overlap(timeline(array[i].Id), timeline(array[j].Id)))
                    result.Add(new(array[i].Id, array[j].Id, "Different biological parents of the same role in overlapping periods; do not auto-resolve."));
        }
        return result;
    }
    private static bool DifferentParentRole(KnowledgeRelationType a, KnowledgeRelationType b) => a != b &&
        a is KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother or KnowledgeRelationType.AdoptiveParent &&
        b is KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother or KnowledgeRelationType.AdoptiveParent;
}
