# QT Studio AI Book Intelligence — Master Specification

## Product boundary

AI Book Intelligence is a project-level, user-triggered assistant for surveying Chinese source text. It builds an evidence-backed knowledge base for Characters, aliases, direct relationships, derived blood relationships, addressing periods, scenes, and glossary terms. It never changes `ChapterDocument.OriginalText`, completed translations, approvals, the Project Manifest, or the translation queue.

`AutoApplyVerified` is always `false`. AI output remains a proposal until the user accepts it. Derived relationships are also proposals and never become an addressing rule automatically.

## Durable data and compatibility

The auxiliary state is stored in `Settings/ai-book-intelligence.json` through the existing checksummed, atomic recovery store. The envelope remains schema 1; content schema 2 adds:

- adaptive scan mode, evidence priority/reason/group, deterministic audit state, and metrics;
- stable candidate identities, alias evidence history, merge proposals, and reversible merge history;
- direct relationship graph facts and derived relationship dependencies/status;
- chapter-aware addressing periods and scene snapshots;
- glossary category and global/context-dependent scope;
- cross-file resource commit markers for crash reconciliation.

Content schema 1 is migrated in memory to schema 2 on load. The first subsequent save uses the existing backup path. The Project Manifest, chapter JSON schema, Characters schema, and Glossary schema are unchanged.

## Corpus indexing and adaptive planning

`ProjectCorpusIndexer` reads 100% of every active `ChapterDocument.OriginalText`. It divides source into exact, non-overlapping windows of about 900 UTF-16 code units, preserving ChapterId, start, length, source hash, excerpt, and signals. Concatenating the current windows reconstructs the original source exactly; long lines and multiline source are covered.

Each window is classified High, Medium, or Low from several CPU signals: known names/aliases, repeated name-action forms, identity introductions, direct relationship words, dialogue structure, and domain-term suffixes. No single regex verifies an entity. Fast Adaptive sends High/Medium windows plus a deterministic audit sample of Low windows. Full Semantic sends every current window. A finding in a Low audit sample expands the remaining same-group scope in that chapter.

Unchanged chapter hashes are not rescanned. Changed chapters receive new current windows; old windows stay retired while referenced by review/audit history. `BookIntelligenceCompactor` is an explicit maintenance helper that can remove only retired, unreferenced evidence; it is not called automatically.

## AI analysis and cache

`AdaptiveBookIntelligenceAnalyzer` sends at most eight excerpts per request and caps prompt characters against the selected context size. It validates schema JSON, evidence IDs, literal Chinese forms, and evidence ranges. Malformed JSON is retried once; transient Ollama connection/timeout/HTTP/invalid-response failures use bounded retry. Each completed request is checkpointed.

The cache key contains model, analyzer schema version, exact source hashes/ranges, and only relevant Character/Glossary context present in the excerpts. Unrelated Project Resource edits do not invalidate prior analysis. Model, schema, relevant context, or source changes do.

The analysis concurrency policy is persisted, but the current Session 2 runner deliberately uses one sequential request lane for the selected analyzer backend. Multi-profile/GPU analyzer scheduling is not yet implemented.

## Entity resolution and relationship knowledge

`EntityResolutionService` gives each candidate a stable ID, aggregates evidence/aliases, searches backwards across indexed evidence, and proposes exact alias matches against existing Characters. It never auto-merges by name similarity. A user can merge a candidate into an existing Character; the operation records added aliases and the resource revision. Undo is allowed only when the current resource revision still matches, preventing removal of later manual edits.

Direct graph facts record subject, target, structured relationship type, status, evidence IDs, source/reveal chapters, and origin. Only confirmed or explicitly accepted facts can participate in inference.

`KinshipInferenceEngine` is intentionally narrow and dependency based. It supports compatible biological father/mother propagation through full or matching half-sibling facts, inverse biological child, and grandparent/grandchild at maximum depth two. It excludes maternal/paternal mismatches, unknown sibling type, adoptive, step, sworn, social, and ambiguous facts. Each derived relation retains its input relation IDs and evidence. Rejecting a premise marks dependents stale. Conflicting inputs produce a conflict state rather than a forced answer.

## Timeline, scene, and translation context

Relationships and approved addressing periods have reveal/validity boundaries. `RelationshipTimelineService` returns only data valid at the requested chapter, so later discoveries cannot leak into earlier chapters.

`SceneContextService` builds immutable, chapter-local segment snapshots in source order. Explicit speaker/listener syntax can be resolved; unnamed or multi-party dialogue is marked ambiguous. Ambiguity is preserved and is never converted into a guessed pronoun.

`BookKnowledgeContextProvider` creates a bounded immutable snapshot before each translation request. It includes relevant confirmed Characters, relationships already revealed, approved addressing periods, accepted global glossary, and the current scene resolution. It excludes rejected/stale/conflicting knowledge and warns the prompt when the scene is ambiguous.

The Project setting `BookKnowledgeContextEnabled` defaults to `false`. With it off, the existing translation prompt path is unchanged. With it on, editor and queue translation services use the same provider. An in-flight request keeps the snapshot captured at request start; later knowledge changes apply only to later requests.

## Review and safe synchronization

The modeless review window supports type/status/search filtering, origin and evidence count, multi-selection for safe bulk defer/reject, exact evidence navigation, Character edit/create/merge, direct relationship review, glossary review, and derived inference-chain review. The existing Character form remains the editor of record.

Context-dependent/addressing terms can be accepted into the knowledge base but are not written as global Glossary replacements. Direct relationship knowledge may be accepted without inventing addressing. Locked Character/Relationship rules are never overwritten.

Cross-file commits write a pending marker to the knowledge state, save Project Resources through the existing revision-guarded store, then finalize the marker. On restart, reconciliation recognizes resources that already committed and completes the review state idempotently.

## Explicit limitations

- Real-model precision/recall and token cost require a human-reviewed novel corpus; the included benchmark is CPU/planner-only.
- Analyzer execution is currently sequential for one chosen backend despite the persisted concurrency policy.
- Bulk accept is intentionally absent; bulk defer/reject is supported because acceptance may need field-level review.
- Relationship review is integrated in the review grid; there is no separate rich relationship editor.
- Scene resolution handles explicit signals and preserves ambiguity; it is not a general coreference model.
