# AI Book Intelligence — Session 2 Handoff

## Built classes and interfaces

- `BookIntelligenceState`, `CorpusChapterIndex`, `CorpusEvidence`
- `IntelligenceCharacterSuggestion`, `IntelligenceRelationshipSuggestion`, `IntelligenceGlossarySuggestion`
- `BookIntelligenceStore`
- `ProjectCorpusIndexer`
- `AdaptiveBookIntelligenceAnalyzer`
- `BookIntelligenceReviewService`, `IntelligenceCommitResult`
- `BookIntelligenceReviewWindow`
- `MainWindow` integration in `MainWindow.BookIntelligence.cs`

No translation-pipeline interface was changed. AI calls use the existing `IOllamaClient` contract.

## How to use

1. Open a Project.
2. Open **Thiết lập → Nhân vật & xưng hô**.
3. Click **Khảo sát bằng AI**.
4. Select the analyzer backend/model. This selection is persisted in the intelligence state and does not edit Project translation settings.
5. Click **Bắt đầu khảo sát**. CPU indexing runs first; Ollama is called only afterward for current evidence without a completed cache key.
6. Select a queue item, inspect its source evidence, and choose edit/save, merge, review later, or reject.

## Persistence/checkpoint workflow

- Durable path: `Settings/ai-book-intelligence.json` plus `.bak`.
- Envelope: schema version 1, content element, SHA-256.
- Store saves use optimistic state revision.
- Index checkpoints after every changed chapter.
- Analyzer checkpoints after every valid response batch and records the completed input/model/version hash.
- On an Ollama error, `LastError` and the completed prefix remain durable; the failing batch is retried.
- Project Resource commits use the current `ProjectResourceStore` revision guard and recovery behavior.

## Test coverage added

- Full-source index and exact UTF-16 evidence.
- No implicit AI call.
- Incremental unchanged/changed source behavior and retired evidence.
- Structured response parsing, evidence validation, and durable queue reload.
- Invalid/uncited result rejection with manifest/chapter byte preservation.
- Connection failure checkpoint and resume.
- Explicit Character and Glossary save.
- Alias merge preserving a locked narrative pronoun.
- Locked Relationship overwrite rejection.
- WPF explicit survey entry point.

See `PROGRESS.md` for final suite counts after the Session 1 acceptance run.

Acceptance result: 628 Core + 92 WPF = 720 tests passed; Release build completed with zero errors and zero warnings.

## Known limitations

- Direct evidence only; no multi-hop relationship inference.
- No background schedule or automatic scan.
- No automatic resolution of conflicting user-edited resources.
- No corpus-wide semantic/vector index.
- No queue ranking/cluster UI beyond type, confidence, and deterministic sorting.
- Remote SSH tunnel creation is not duplicated inside the analyzer UI.

## Session 2 work

1. Improve candidate recall with corpus frequency/entity co-occurrence while retaining source-bound evidence.
2. Add direct entity clusters for aliases and a user-confirmed merge graph.
3. Add relationship evidence aggregation across chapters; then design, but do not silently apply, multi-hop inference.
4. Add review filters, bulk decisions, duplicate clusters, and an explicit stale-evidence reconciliation screen.
5. Add context-dependent glossary policy and term-category views.
6. Add metrics for scan coverage, cache hit rate, false-positive review outcomes, and model cost/time.
7. Add an SSH-runtime client handoff if the product requires analyzer traffic through an already-open GPU tunnel.
8. Consider a small transaction coordinator for cross-file Resource + review-state acknowledgements.

