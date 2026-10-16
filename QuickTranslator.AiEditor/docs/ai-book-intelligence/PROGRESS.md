# AI Book Intelligence — Session 1 Progress

## Implemented

- [x] Audited existing Characters, directional Relationships, Glossary, Project Resources, Ollama profiles, and WPF Character form.
- [x] Confirmed there was no existing Corpus Indexer.
- [x] Added a full-source CPU index with ChapterId, UTF-16 offsets, source hash, exact evidence, per-chapter checkpoint, resume, and incremental rescan.
- [x] Added bounded adaptive Ollama analysis with an independent model/profile selection, JSON schema, evidence checks, deduplication, and completed-request cache.
- [x] Added durable Character, Relationship, and Glossary suggestions with Unknown/Ambiguous states.
- [x] Added a modeless review queue and exact source-evidence navigation.
- [x] Reused the current Character form for edit-before-save.
- [x] Added explicit Character create/merge, Relationship forward/reverse save, Glossary save, later, and reject actions.
- [x] Added revision conflict checks and locked relationship protection through existing Project Resources persistence.
- [x] Kept `AutoApplyVerified=false`; the analyzer is never called before the user presses **Bắt đầu khảo sát**.
- [x] Added focused Core and WPF regression tests.

## Verified invariants

- Index/analyze operations do not write `.qtproj` or chapter files.
- Source excerpts must match stored UTF-16 ranges.
- Restart reloads the review queue and request cache.
- Changed source is rescanned while unchanged source is skipped.
- Invalid/uncited findings cannot enter the queue.
- Confirmed Character/Glossary entries use Project Resource revision guards.
- Character merge preserves locked narrative pronouns.
- Existing locked Relationships are not overwritten.

## Verification completed

- Core regression: **628 passed, 0 failed, 0 skipped**.
- WPF regression: **92 passed, 0 failed, 0 skipped**.
- Combined: **720 passed, 0 failed, 0 skipped**.
- Release solution build: **0 errors, 0 warnings**.
- Automated WPF integration verifies the explicit survey entry point. A live Ollama model was not invoked during acceptance, so model quality and the visual result of a long real scan still need a synthetic/manual Windows smoke test. No personal Project was opened.

## Risks recorded during implementation

- Candidate recall is bounded by CPU signal quality. This is safer than sending the entire novel to AI but means the review queue is not exhaustive.
- Durable review state and Project Resources are separate atomic files. Resources commit first; if the subsequent review-state write fails, retry detects the already-existing resource instead of overwriting it, but the queue item may still appear pending until reconciled manually.
- Project Resources currently cap Characters and Glossary at 1,000 each. The review service respects those limits.

