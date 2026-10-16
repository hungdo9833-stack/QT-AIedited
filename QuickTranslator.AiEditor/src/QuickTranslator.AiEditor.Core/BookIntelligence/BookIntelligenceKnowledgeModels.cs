using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public enum IntelligenceScanMode { FastAdaptive, FullSemantic }
public enum EvidencePriority { Low, Medium, High }

public sealed class IntelligenceAnalysisMetrics
{
    public long Requests { get; set; }
    public long CacheHits { get; set; }
    public long Retries { get; set; }
    public long Errors { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long AiDurationMilliseconds { get; set; }
    public long EvidenceAnalyzed { get; set; }
    public long EvidenceSkipped { get; set; }
    public long AuditDiscoveries { get; set; }
}

public enum CandidateIdentityStatus
{
    Candidate, ProposedAliasLink, ConfirmedIdentityMerge, IdentityConflict, Rejected
}

public sealed class CandidateIdentity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ChineseName { get; set; } = "";
    public string VietnameseName { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public CandidateIdentityStatus Status { get; set; }
    public Guid? ProposedExistingCharacterId { get; set; }
    public List<Guid> EvidenceHistory { get; set; } = [];
    public List<string> Conflicts { get; set; } = [];
    public List<IdentityMergeRecord> MergeHistory { get; set; } = [];
}

public sealed class IdentityMergeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CandidateIdentityId { get; set; }
    public Guid TargetCharacterId { get; set; }
    public List<string> AddedAliases { get; set; } = [];
    public long ResourceRevisionBefore { get; set; }
    public long ResourceRevisionAfter { get; set; }
    public DateTimeOffset CommittedAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
}

public enum KnowledgeRelationType
{
    Unknown, Parent, Child, BiologicalFather, BiologicalMother, BiologicalChild,
    AdoptiveParent, AdoptiveChild, FullSibling, PaternalHalfSibling, MaternalHalfSibling,
    UnknownSibling, SwornSibling, Grandfather, Grandmother, Grandchild, Spouse,
    RomanticPartner, Teacher, Disciple, Superior, Subordinate, Social
}

public enum RelationshipDiscoveryMethod { DirectAi, UserConfirmed, CpuInference }
public enum KnowledgeRelationshipStatus
{
    Proposed, Confirmed, DerivedAwaitingReview, Accepted, Rejected, Ambiguous, Stale, Conflict
}

public sealed class KnowledgeRelationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SubjectCharacterId { get; set; }
    public Guid TargetCharacterId { get; set; }
    public KnowledgeRelationType RelationType { get; set; }
    public string RelationQualifier { get; set; } = "";
    public string OriginalDescription { get; set; } = "";
    public List<Guid> EvidenceIds { get; set; } = [];
    public RelationshipDiscoveryMethod DiscoveryMethod { get; set; }
    public KnowledgeRelationshipStatus Status { get; set; }
    public int? FirstObservedChapter { get; set; }
    public int? ValidFromChapter { get; set; }
    public int? ValidToChapter { get; set; }
    public int? RevealedAtChapter { get; set; }
    public Dictionary<Guid, int?> KnownByCharacterAtChapter { get; set; } = [];
}

public enum InferenceValidityStatus { Valid, Ambiguous, Stale, Conflict }

public sealed class DerivedRelationship
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RuleId { get; set; } = "";
    public List<Guid> InputRelationIds { get; set; } = [];
    public List<Guid> SupportingEvidenceIds { get; set; } = [];
    public int InferenceDepth { get; set; }
    public Guid SubjectCharacterId { get; set; }
    public Guid TargetCharacterId { get; set; }
    public KnowledgeRelationType OutputRelation { get; set; }
    public InferenceValidityStatus ValidityStatus { get; set; }
    public KnowledgeRelationshipStatus Status { get; set; } = KnowledgeRelationshipStatus.DerivedAwaitingReview;
    public string Explanation { get; set; } = "";
}

public sealed class AddressingRulePeriod
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SpeakerCharacterId { get; set; }
    public Guid ListenerCharacterId { get; set; }
    public string SelfReference { get; set; } = "";
    public string AddresseeReference { get; set; } = "";
    public int? ValidFromChapter { get; set; }
    public int? ValidToChapter { get; set; }
    public int? RevealedAtChapter { get; set; }
    public bool Approved { get; set; }
    public bool Locked { get; set; }
    public List<Guid> EvidenceIds { get; set; } = [];
}

public enum SceneResolutionStatus { Verified, Ambiguous, Unresolved }

public sealed class SceneContextSnapshot
{
    public Guid ProjectId { get; set; }
    public Guid ChapterId { get; set; }
    public Guid SceneId { get; set; } = Guid.NewGuid();
    public int StartSegmentOrder { get; set; }
    public int EndSegmentOrder { get; set; }
    public List<Guid> ActiveCharacterIds { get; set; } = [];
    public Guid? SpeakerCharacterId { get; set; }
    public Guid? ListenerCharacterId { get; set; }
    public List<Guid> RelevantRelationshipIds { get; set; } = [];
    public List<Guid> ValidAddressingRuleIds { get; set; } = [];
    public List<Guid> EvidenceIds { get; set; } = [];
    public SceneResolutionStatus ResolutionStatus { get; set; }
    public string SourceSha256 { get; set; } = "";
}

public enum GlossaryKnowledgeCategory
{
    Unknown, Place, Organization, Clan, CultivationMethod, Item, Realm, Title, StoryTerm
}
public enum GlossaryKnowledgeScope { Global, ContextDependent, CharacterName, AddressingKinship }

public enum ResourceCommitKind { Character, CharacterMerge, Relationship, Glossary }
public enum ResourceCommitState { Prepared, ResourceCommitted, Reconciled }

public sealed class IntelligenceResourceCommit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public ResourceCommitKind Kind { get; set; }
    public ResourceCommitState State { get; set; }
    public Guid SuggestionId { get; set; }
    public string IntendedFingerprint { get; set; } = "";
    public long ExpectedResourceRevision { get; set; }
    public long? CommittedResourceRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record BookKnowledgeCharacter(Guid Id, string ChineseName, string VietnameseName,
    IReadOnlyList<string> Aliases, CharacterGender Gender, string NarrativePronoun, bool Locked);

public sealed record BookKnowledgeRelation(Guid Id, Guid SubjectId, Guid TargetId,
    KnowledgeRelationType Type, string Qualifier);

public sealed record BookKnowledgeAddressing(Guid SpeakerId, Guid ListenerId, string SelfReference,
    string AddresseeReference, bool Locked);

public sealed record BookKnowledgeTerm(string Chinese, string Vietnamese, GlossaryKnowledgeScope Scope);

public sealed record BookKnowledgeContextSnapshot(long KnowledgeRevision, Guid ProjectId, Guid ChapterId,
    Guid SegmentId, IReadOnlyList<BookKnowledgeCharacter> Characters,
    IReadOnlyList<BookKnowledgeRelation> Relationships, IReadOnlyList<BookKnowledgeAddressing> Addressing,
    IReadOnlyList<BookKnowledgeTerm> Terms, SceneResolutionStatus SceneStatus, string PromptBlock)
{
    public static BookKnowledgeContextSnapshot Empty(Guid projectId, Guid chapterId, Guid segmentId) =>
        new(0, projectId, chapterId, segmentId, [], [], [], [], SceneResolutionStatus.Unresolved, "");
}

