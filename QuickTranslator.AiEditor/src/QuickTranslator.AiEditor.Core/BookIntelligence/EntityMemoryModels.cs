using System.Security.Cryptography;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public enum MemoryReviewStatus { Confirmed, Accepted, Proposed, Ambiguous, Conflict, Stale, Rejected }
public enum MemoryEntityKind { Character, Candidate, UnresolvedMention, Body, Consciousness, AddressingCatalogue }
public enum MemoryFactKind { PrimaryName, Alias, Nickname, Title, FamilyAddressing, IdentityAssociation, Relationship, Addressing, CharacterState }
public enum MemoryAssociationKind { Unspecified, PossibleIdentity, Body, Consciousness }
public enum MemoryOrigin { ProjectCharacter, CandidateIdentity, Census, AuditOnly, LegacyRelationship, LegacyAddressing, CpuTrigger, Rule, User }
public enum TranslationImpactEventType { RevealIdentity, NameOrAliasChange, TitleGrantedOrRevoked, RelationshipEstablished, RelationshipEnded, FamilyRoleChanged, AddressingChanged, CharacterStateChanged }
public enum MemoryEventEffect { None, Activate, End }

public sealed record MemoryTimeline(int? EffectiveFromChapter = null, int? EffectiveToChapter = null,
    int? DiscoveredAtChapter = null, int? RevealedAtChapter = null)
{
    public void Validate()
    {
        if (EffectiveFromChapter is <= 0 || EffectiveToChapter is <= 0 || DiscoveredAtChapter is <= 0 || RevealedAtChapter is <= 0 ||
            EffectiveFromChapter is int from && EffectiveToChapter is int to && from > to)
            throw new InvalidDataException("Invalid memory timeline.");
    }
    public bool VisibleAt(int chapter) => EffectiveFromChapter is int from && from <= chapter &&
        (EffectiveToChapter is null || EffectiveToChapter >= chapter) && RevealedAtChapter is int reveal && reveal <= chapter;
}

// Labels are for audit UI only, NEVER the source of a chapter-scoped display name.
public sealed record MemoryEntity(Guid Id, Guid ProjectId, MemoryEntityKind Kind, string Label, Guid? LegacyId = null);
public sealed record MemoryEvidence(Guid Id, Guid ProjectId, Guid ChapterId, int ChapterNumber, string SourceSha256,
    int StartUtf16, int LengthUtf16, Guid? CorpusEvidenceId = null);
public sealed record MemoryProvenance(MemoryOrigin Origin, string Key, string Explanation,
    Guid? LegacyId = null, string RuleId = "", string CensusName = "", string CensusFingerprint = "", string LineageKey = "");

public sealed record EntityMemoryFact
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public Guid SubjectId { get; init; }
    public Guid? TargetId { get; init; }
    public MemoryFactKind Kind { get; init; }
    public string Value { get; init; } = "";
    public KnowledgeRelationType RelationType { get; init; }
    public MemoryAssociationKind AssociationKind { get; init; }
    public string SelfReference { get; init; } = "";
    public string AddresseeReference { get; init; } = "";
    public MemoryTimeline Timeline { get; init; } = new();
    public double Confidence { get; init; }
    public MemoryReviewStatus InitialStatus { get; init; } = MemoryReviewStatus.Proposed;
    public IReadOnlyList<Guid> EvidenceIds { get; init; } = [];
    public IReadOnlyList<Guid> InputFactIds { get; init; } = [];
    public MemoryProvenance Provenance { get; init; } = new(MemoryOrigin.User, "", "");
}

public sealed record TranslationImpactEvent
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public TranslationImpactEventType Type { get; init; }
    public IReadOnlyList<Guid> EntityIds { get; init; } = [];
    public MemoryEventEffect Effect { get; init; }
    public MemoryTimeline Timeline { get; init; } = new();
    public IReadOnlyList<Guid> EvidenceIds { get; init; } = [];
    public IReadOnlyList<Guid> AffectedFactIds { get; init; } = [];
    public double Confidence { get; init; }
    public MemoryReviewStatus InitialStatus { get; init; } = MemoryReviewStatus.Proposed;
    public MemoryProvenance Provenance { get; init; } = new(MemoryOrigin.CpuTrigger, "", "");
}

public sealed record MemoryReviewDecision(Guid Id, Guid TargetId, MemoryReviewStatus Status, DateTimeOffset At,
    string Note, MemoryTimeline Timeline, string DefinitionFingerprint, Guid? RevertsDecisionId = null);
public sealed record MemoryChapterCheckpoint(Guid ChapterId, int ChapterNumber, string SourceSha256, string ProcessorVersion);

/// <summary>Optional v1 sidecar. Existing survey/resources and P0.1 journal remain untouched.</summary>
public sealed class EntityMemoryState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public List<MemoryEntity> Entities { get; set; } = [];
    public List<MemoryEvidence> Evidence { get; set; } = [];
    public List<EntityMemoryFact> Facts { get; set; } = [];
    public List<TranslationImpactEvent> Events { get; set; } = [];
    public List<MemoryReviewDecision> Reviews { get; set; } = [];
    public List<MemoryChapterCheckpoint> Checkpoints { get; set; } = [];

    public void Validate()
    {
        static void Require([DoesNotReturnIf(false)] bool condition) { if (!condition) throw new InvalidDataException("Invalid entity/event memory sidecar."); }
        bool Text(string? value, int limit, bool required = false) => value is not null && value.Length <= limit && (!required || !string.IsNullOrWhiteSpace(value));
        bool Hash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
        bool Provenance(MemoryProvenance? p) => p is not null && Enum.IsDefined(p.Origin) && Text(p.Key, 1024, true) &&
            Text(p.Explanation, 4000) && Text(p.RuleId, 200) && Text(p.LineageKey, 1024) && Text(p.CensusName, 200) && Text(p.CensusFingerprint, 64) &&
            (p.CensusName.Length == 0 == (p.CensusFingerprint.Length == 0)) && (p.CensusFingerprint.Length == 0 || Hash(p.CensusFingerprint));
        Require(SchemaVersion == 1 && ProjectId != Guid.Empty && Revision >= 0 && Entities is not null && Evidence is not null &&
            Facts is not null && Events is not null && Reviews is not null && Checkpoints is not null);
        Require(Entities.All(x => x is not null && x.Id != Guid.Empty && x.ProjectId == ProjectId && Enum.IsDefined(x.Kind) && Text(x.Label, 200, true)));
        var entities = Entities.Select(x => x.Id).ToHashSet();
        Require(Evidence.All(x => x is not null && x.Id != Guid.Empty && x.ProjectId == ProjectId && x.ChapterId != Guid.Empty &&
            x.ChapterNumber > 0 && Hash(x.SourceSha256) && x.StartUtf16 >= 0 && x.LengthUtf16 > 0 && (long)x.StartUtf16 + x.LengthUtf16 <= int.MaxValue));
        var evidence = Evidence.Select(x => x.Id).ToHashSet();
        Require(Facts.All(x => x is not null) && Events.All(x => x is not null));
        var facts = Facts.Select(x => x.Id).ToHashSet();
        bool Refs(IReadOnlyList<Guid>? refs, HashSet<Guid> ids) => refs is not null && refs.Distinct().Count() == refs.Count && refs.All(ids.Contains);
        Require(Facts.All(x => x is not null && x.Id != Guid.Empty && x.ProjectId == ProjectId && entities.Contains(x.SubjectId) &&
            (x.TargetId is null || entities.Contains(x.TargetId.Value) && x.TargetId != x.SubjectId) && Enum.IsDefined(x.Kind) &&
            Enum.IsDefined(x.RelationType) && Enum.IsDefined(x.AssociationKind) && Enum.IsDefined(x.InitialStatus) && x.InitialStatus != MemoryReviewStatus.Confirmed &&
            double.IsFinite(x.Confidence) && x.Confidence is >= 0 and <= 1 && Text(x.Value, 500, true) && Text(x.SelfReference, 200) && Text(x.AddresseeReference, 200) &&
            x.Timeline is not null && Provenance(x.Provenance) && Refs(x.EvidenceIds, evidence) && Refs(x.InputFactIds, facts) && !x.InputFactIds.Contains(x.Id) &&
            (x.Kind is not (MemoryFactKind.Relationship or MemoryFactKind.Addressing or MemoryFactKind.IdentityAssociation) || x.TargetId is not null) &&
            (x.Kind != MemoryFactKind.Relationship || x.RelationType != KnowledgeRelationType.Unknown)));
        foreach (var fact in Facts) fact.Timeline.Validate();
        Require(Events.All(x => x is not null && x.Id != Guid.Empty && x.ProjectId == ProjectId && Enum.IsDefined(x.Type) && Enum.IsDefined(x.Effect) &&
            Enum.IsDefined(x.InitialStatus) && x.InitialStatus != MemoryReviewStatus.Confirmed && x.Timeline is not null && Provenance(x.Provenance) &&
            double.IsFinite(x.Confidence) && x.Confidence is >= 0 and <= 1 && Refs(x.EntityIds, entities) && Refs(x.EvidenceIds, evidence) && Refs(x.AffectedFactIds, facts)));
        foreach (var item in Events) item.Timeline.Validate();
        var targets = facts.Concat(Events.Select(x => x.Id)).ToHashSet();
        var reviewIds = new HashSet<Guid>();
        foreach (var review in Reviews)
        {
            Require(review is not null && review.Id != Guid.Empty && reviewIds.Add(review.Id) && targets.Contains(review.TargetId) &&
                Enum.IsDefined(review.Status) && Text(review.Note, 2000, true) && review.At != default && review.Timeline is not null && Hash(review.DefinitionFingerprint) &&
                (review.RevertsDecisionId is null || Reviews.Take(reviewIds.Count - 1).Any(x => x.Id == review.RevertsDecisionId && x.TargetId == review.TargetId)));
            review.Timeline.Validate();
            Require(review.DefinitionFingerprint == EntityMemoryIdentity.DefinitionFingerprint(this, review.TargetId));
            if (review.Status == MemoryReviewStatus.Confirmed)
            {
                Require(review.Timeline.EffectiveFromChapter is not null && review.Timeline.RevealedAtChapter is not null);
                var fact = Facts.SingleOrDefault(x => x.Id == review.TargetId);
                if (fact is not null) Require(fact.EvidenceIds.Count > 0 &&
                    (fact.Kind != MemoryFactKind.Addressing || !string.IsNullOrWhiteSpace(fact.SelfReference) && !string.IsNullOrWhiteSpace(fact.AddresseeReference)));
                else { var item = Events.Single(x => x.Id == review.TargetId); Require(item.EvidenceIds.Count > 0 && item.EntityIds.Count > 0 &&
                    (item.Effect == MemoryEventEffect.None || item.AffectedFactIds.Count > 0)); }
            }
        }
        var ids = entities.Concat(evidence).Concat(facts).Concat(Events.Select(x => x.Id)).Concat(Reviews.Select(x => x.Id)).ToArray();
        Require(ids.Length == Entities.Count + Evidence.Count + Facts.Count + Events.Count + Reviews.Count && ids.Distinct().Count() == ids.Length);
        Require(Facts.Select(x => x.Provenance.Key).Distinct(StringComparer.Ordinal).Count() == Facts.Count &&
            Events.Select(x => x.Provenance.Key).Distinct(StringComparer.Ordinal).Count() == Events.Count);
        Require(Checkpoints.All(x => x is not null && x.ChapterId != Guid.Empty && x.ChapterNumber > 0 && Hash(x.SourceSha256) && Text(x.ProcessorVersion, 200, true)) &&
            Checkpoints.Select(x => x.ChapterId).Distinct().Count() == Checkpoints.Count);
        // Dependencies must be a DAG. Imported links are facts, never identity unions.
        var visited = new HashSet<Guid>(); var visiting = new HashSet<Guid>(); var byId = Facts.ToDictionary(x => x.Id);
        void Visit(Guid id, int depth)
        {
            if (visited.Contains(id)) return;
            Require(depth <= 16 && visiting.Add(id)); foreach (var input in byId[id].InputFactIds) Visit(input, depth + 1);
            visiting.Remove(id); visited.Add(id);
        }
        foreach (var fact in Facts) Visit(fact.Id, 0);
    }
}

public static class EntityMemoryIdentity
{
    public static Guid StableId(Guid project, string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(project.ToString("D") + "|" + key)).AsSpan(0, 16));
    public static string Fingerprint<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, DocumentJson.Options)));
    public static string DefinitionFingerprint(EntityMemoryState state, Guid id) => state.Facts.SingleOrDefault(x => x.Id == id) is { } fact
        ? Fingerprint(fact) : Fingerprint(state.Events.Single(x => x.Id == id));
}
