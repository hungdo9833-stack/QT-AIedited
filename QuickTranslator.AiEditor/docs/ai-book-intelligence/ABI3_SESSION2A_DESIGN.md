# ABI 3.0 Session 2A — design and checkpoint log

Required base: `bea07fe1d8b2a09e7cf0dec4952143792be734b5`, separate clean clone,
new branch `codex/abi3-entity-event-memory`. No Session 2B, provider calls, personal
Project writes, translation/Queue/prompt changes or automatic context enablement.

## Architecture surveyed before implementation

- CharacterMentionCensus/ChineseMentionDiscovery: typed, bounded, source-linked CPU
  hypotheses; reuse their literal counting and fingerprints, not a second NER.
- MissingIdentityReviewStore: audit-only associations and append-only human history;
  never promote LinkExisting automatically to a translation alias.
- BookIntelligenceState/CandidateIdentity/EntityResolutionService: reuse legacy IDs
  as origin references. Do not invoke mutating name-based entity aggregation or
  identity merging for new memory. Exact/homonym matches stay separate or ambiguous.
- RelationshipKnowledgeService/KnowledgeRelationship: reuse typed relationships.
- KinshipInferenceEngine: reuse biological K01 rules, add temporal intersection and
  explicit source/approval gates around them; do not derive from sworn/adoptive/
  unknown siblings as biological facts.
- RelationshipTimelineService/AddressingRulePeriod: reuse timeline selection on an
  isolated projection, with stricter mandatory reveal/expiry/source checks.
- SceneContextService/BookKnowledgeContextProvider: existing translation consumers
  remain unmodified. A new read-only query is groundwork for 2B, not prompt input.
- ProjectCorpusIndexer/CorpusIntegrityAuditor: reuse source hashes, evidence IDs,
  UTF-16 provenance and manifest-backed source reads, without reindexing legacy state.
- BookIntelligenceReviewService: keep resource commits out of 2A; independent
  memory review cannot call accept/merge methods that write Project Resources.

## Data and safety decisions

Optional `Settings/abi3-entity-event-memory.v1.json`; no migration of old schemas.
Entity handles reference legacy profiles/candidates or explicitly separate body/
consciousness/unresolved identities. Facts are temporal assertions, not a second
Character editor. Events only cover the eight translation-impact types requested.
Sources are shared hash/range references (no persisted excerpt copies). Stable IDs
and provenance keys make repeated imports/scans idempotent. Source changes create
new versions and make old evidence-dependent conclusions stale, not deleted.

Definitions and prior review records are immutable. Review/rollback append records;
freshness/conflicts are derived views and never overwrite old approvals. Accepted
means reviewed/staged; Confirmed requires a separate explicit human action, current
source and timing. Only Confirmed, fresh, revealed, effective facts are returned
by the new future query. Unknown reveal dates remain review-only. Audit associations
remain associations even when reviewed; a separate alias-promotion confirmation is
required, and still does not write translation resources.

One atomic sidecar transaction contains definitions, evidence and scan checkpoints.
Per-path semaphore + exclusive process lease + revision guard prevent lost updates.
SimpleFileStorage supplies validate/flush/replace and fault-injection support. No
destructive compaction of confirmed history. Old readers ignore the optional file.

## Checkpoints

A. Models/persistence and atomicity/concurrency/backward-compatibility tests.
B. Projection, CPU event candidates, resume, source audit, temporal queries and
   explainable proposals/conflict checks. No automatic event semantics/approval.
C. Minimal review window, source navigation/filtering, separate human confirmation,
   Core/WPF regressions, 500–1,000 chapter storage measurements and final Release build.

Each checkpoint is committed separately. Final test totals, SHA list and remaining
limits will be recorded in the Session 2A handoff report.

Checkpoint A: models/persistence implemented; focused tests cover optional-file
backward compatibility, immutable definitions/review history, atomic rollback at
flush fault, cancellation, process lease, revision races, invalid/partial files and
project/facet-scoped IDs. No new compiler warnings are accepted in these files.
