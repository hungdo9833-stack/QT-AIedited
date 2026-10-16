# QT Studio AI Book Intelligence — Master Specification

## Session 1 boundary

AI Book Intelligence is a project-level, user-triggered assistant for surveying Chinese source text. It does not participate in translation, segmentation, approval, or the project translation queue. The first session implements corpus indexing, evidence-bound AI extraction, a durable review queue, and explicit commits to existing Character/Relationship/Glossary resources.

`AutoApplyVerified` is fixed to `false`. An AI response is a proposal even when its confidence is high. Only a review action can change Project Resources.

## File-level architecture

- `Core/BookIntelligence/BookIntelligenceModels.cs`: durable knowledge-base/index schema, evidence offsets, suggestions, review states.
- `Core/BookIntelligence/BookIntelligenceStore.cs`: checksummed, atomic `Settings/ai-book-intelligence.json` persistence and optimistic revision guard.
- `Core/BookIntelligence/ProjectCorpusIndexer.cs`: reads every active `ChapterDocument.OriginalText`, computes source hashes, records UTF-16 evidence, checkpoints changed chapters, and retains retired evidence for audit references.
- `Core/BookIntelligence/AdaptiveBookIntelligenceAnalyzer.cs`: bounded evidence batches, independent model options, structured JSON parsing, evidence validation, deduplication, request cache, and restart checkpoint.
- `Core/BookIntelligence/BookIntelligenceReviewService.cs`: explicit Character merge/create, Relationship create, Glossary create, Project Resource revision checks, and locked-rule protection.
- `Wpf/BookIntelligenceReviewWindow.cs`: modeless scan/review window; evidence navigation; accept, edit, merge, later, and reject actions.
- `Wpf/MainWindow.BookIntelligence.cs`: entry point, reuse of the current Character editor, project-resource refresh, and exact evidence navigation.

## Knowledge-base schema

The envelope has schema version 1 and SHA-256 over its JSON content. Its content is `BookIntelligenceState`:

- Project identity, state revision, analyzer version/model/profile, `AutoApplyVerified=false`.
- Per-chapter source SHA-256, UTF-16 source length, scan time, and evidence.
- Evidence identity, ChapterId, UTF-16 start/length, source hash, short exact excerpt, signal classification, and current/retired flag.
- Completed analysis keys derived from analyzer version, model, and exact evidence identity/hash/range.
- Character, Relationship, and Glossary review queues with confidence, evidence IDs, reason, and durable review status.

This is an auxiliary Project Settings file. It does not add fields to `.qtproj` or chapter JSON and cannot write back to either.

## Indexing and incremental behavior

The indexer loads every active chapter through `ProjectRepository.LoadChapterAsync` and reads `OriginalText` in full. Candidate extraction is deliberately a multi-signal CPU stage: known names and aliases, name-before-action forms, relationship/address terms, and domain-term suffixes. No single regex decides that a candidate is a verified entity.

Each exact source is hashed. An unchanged chapter is not rescanned. A changed chapter receives a new current evidence set; old evidence is retained as retired so already reviewed findings keep an auditable reference. Deleted chapters are retired. The state is checkpointed after each changed chapter.

Offsets are .NET string indices, therefore UTF-16 code-unit offsets. Evidence navigation verifies ranges against the current source before selection.

## Adaptive analyzer contract

The analyzer receives at most eight evidence excerpts per request. Prompts include known Characters and bounded Glossary context to reduce duplicates. Output must be one JSON object containing `characters`, `relationships`, and `glossary` arrays. Every result cites request-local evidence IDs.

Validation rules:

- cited evidence must belong to the request;
- Chinese names, aliases, relationship endpoints, and terms must occur literally in cited source;
- unknown gender/confidence remains Unknown;
- existing Character names/aliases and Glossary entries are not proposed as new global entries;
- context-dependent terms remain marked for review;
- malformed JSON fails the batch and preserves the last checkpoint;
- hallucinated or uncited individual findings are discarded;
- completed input/model/version hashes are not sent again.

The analyzer model and backend profile are stored separately from translation settings. The default backend is the local Ollama profile. Selecting a remote profile still requires the existing remote disclosure approval.

## Review and safe sync

The WPF review queue is modeless, so scans do not open repeated dialogs. It offers:

- Character: open the existing Character form with proposed values, then create; or merge names/aliases into an existing Character.
- Relationship: review/edit forward and optional reverse address rules, then save only if both endpoint Characters exist.
- Glossary: review/edit the Vietnamese value, then create.
- All types: open exact source evidence, review later, or reject.

Every commit reloads `ProjectResources`, compares the expected revision, validates existing limits, and uses `ProjectResourceStore.SaveAsync`. Character merges only add confirmed Chinese names/aliases and never replace pronouns or user fields. Existing relationship rules are never overwritten; locked rules return a specific error. Glossary conflicts and Character-name collisions require manual resolution.

## Recovery and invariants

- Checkpoints and the review queue survive restart through `RecoveryFiles` primary/backup handling.
- Network failure saves `LastError` and leaves the current batch unfinished for retry.
- Cancel stops after the current awaited operation and retains committed checkpoints.
- Indexing and analysis do not change source, translation drafts, approvals, history, manifest, queue, or chapter files.
- Resource changes occur only after an explicit review action.

## Known Session 1 limits

- CPU signals intentionally favor precision and can miss names written without dialogue/action/relationship/term cues.
- Identity resolution is direct evidence plus current aliases; multi-hop kinship/identity inference is deferred to Session 2.
- No automatic conflict resolution is attempted for an existing unlocked relationship or a Glossary term with another translation.
- An SSH-only remote profile must already expose a reachable Ollama endpoint; the survey window does not create a second SSH tunnel manager.
- Large-project throughput depends on chapter file I/O and the selected local model. Requests are sequential and checkpointed.

