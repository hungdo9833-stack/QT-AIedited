# AI Book Intelligence — Session 2 Progress

## Implemented

- [x] Migrated Session 1 content schema 1 to content schema 2 without changing Project/chapter schemas.
- [x] Replaced sparse-only scanning with exact 100% source windows and High/Medium/Low planning.
- [x] Added deterministic Low-priority audit, same-group expansion after discoveries, Fast Adaptive and Full Semantic modes.
- [x] Added relevant-context cache keys, bounded Ollama retry, malformed-JSON retry, checkpoints, and metrics.
- [x] Added stable candidate identities, evidence/alias aggregation, backward evidence search, exact-match merge proposals, safe merge undo.
- [x] Added structured direct relationship graph and dependency-tracked kinship inference to depth two.
- [x] Added stale/conflict handling and exclusions for adoptive/step/sworn/social/ambiguous relationships.
- [x] Added chapter-aware relationship/addressing timeline to prevent future knowledge leakage.
- [x] Added chapter-local scene snapshots that keep unnamed/multi-party dialogue ambiguous.
- [x] Added bounded immutable translation context behind a Project feature flag that defaults off.
- [x] Wired the provider into both editor translation and Project queue translation without replacing either pipeline.
- [x] Added type/status/search/origin/evidence filters, derived relationship review, and safe multi-select defer/reject to the modeless review UI.
- [x] Added cross-file resource commit markers and restart reconciliation.
- [x] Added focused quality, migration, cache, entity resolution, inference, timeline, scene, recovery, retry, and WPF integration tests.
- [x] Added a deterministic 500-chapter / 2,000,000-character CPU/planner benchmark.

## Verification

- Core regression: **652 passed, 0 failed, 0 skipped**.
- WPF regression: **93 passed, 0 failed, 0 skipped**.
- Combined: **745 passed, 0 failed, 0 skipped**.
- Release solution build: **0 errors, 0 warnings**.
- Focused AI Book Intelligence tests: **32 Core + 2 WPF passed**; structured Ollama request coverage is included in the Core suite.
- Live synthetic Ollama smoke test with `translategemma:12b`: JSON Schema response parsed with all three required collections.
- No personal Project was opened or modified. Tests use temporary synthetic Projects.

## Benchmark checkpoint

- Fixture: 500 synthetic chapters, 2,000,000 UTF-16 characters.
- CPU indexing/planning: 262 ms on this run.
- Full Semantic plan: 2,000 evidence windows, 250 requests at 8 windows/request, about 1,000,000 estimated input tokens.
- Fast Adaptive plan: 769 evidence windows, 97 requests, about 368,773 estimated input tokens.
- Planner reduction on this fixture: 61.2% requests and 63.1% estimated input tokens.
- Managed-memory sample: about 4.82 MB; serialized knowledge state: 26,204,050 bytes.
- Ollama duration/output tokens are zero because this benchmark deliberately does not invoke a real model.

## Remaining validation and limitations

- Run a human-reviewed Chinese novel corpus through a real selected Ollama model to measure true Character/Alias/Relationship/Glossary precision and recall.
- Validate visual density and long-running review interaction on Windows with a large synthetic Project.
- Analyzer requests currently run sequentially on one selected profile. Per-GPU analyzer scheduling is not part of this implementation.
- Bulk accept remains field-by-field; safe bulk defer/reject is available.
- Scene/coreference quality remains conservative and returns Ambiguous when explicit evidence is insufficient.
- The deterministic 15% Low audit target is hash sampling; small/structured fixtures may observe a different realized ratio.
