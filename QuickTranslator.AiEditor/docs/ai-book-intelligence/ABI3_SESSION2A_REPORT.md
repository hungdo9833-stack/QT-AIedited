# ABI 3.0 Session 2A — handoff

Date: 2026-10-19. Repository: `hungdo9833-stack/QT-AIedited`.
Branch: `codex/abi3-entity-event-memory`, from required base
`bea07fe1d8b2a09e7cf0dec4952143792be734b5` in an independent nested clone.
The original dirty checkout was not modified. No Session 2B work.

## Design and behavior

- Optional `Settings/abi3-entity-event-memory.v1.json`; no old-schema migration.
  Stable project/origin-scoped entity handles do not merge characters, candidates,
  homonyms, similar names, bodies or consciousnesses. They reference legacy IDs.
- Facts reuse typed relationships and addressing periods. Importing an existing
  approval stages it as Accepted, never silently confirms chronology. A new human
  Confirmed decision requires current evidence, effective start and explicit reveal.
  All seven requested statuses remain distinct; there is no claim of verified
  extraction completeness.
- P0.1/P0.2 audit associations never become aliases automatically. A separate,
  source-rechecked alias/title promotion records a human decision in memory only.
- Eight CPU trigger classes produce low-confidence event candidates with shared
  hash/UTF-16/evidence references. CPU co-occurrence does not establish participants,
  truth, effects, speaker/listener or reveal dates. Confirmation requires explicit
  event refinement. Definitions are versioned; prior decisions are append-only.
- The isolated chapter query requires fresh, Confirmed, effective, revealed facts;
  unknown reveal is excluded. Audit labels/provenance/facet metadata are not chapter
  DTOs. Chapter 30 cannot expose an identity revealed at chapter 50. End events stop
  facts and dependent derivations without rewriting approved definitions. Conflicting
  historical possibilities are withheld, not resolved using future knowledge.
- Existing KinshipInferenceEngine K01 logic is reused on compatible biological
  input pairs with intersecting periods/latest reveal. Output is proposed only.
  Unknown/adoptive/sworn sibling facts do not establish shared biological parents.
- Atomic definitions + checkpoints, revision guards, an exclusive file lease and
  per-path serialization protect saves. SimpleFileStorage is reused, not changed.
  Read-only source loading re-reads the current manifest. Source edits/deletion/
  renumbering or a changed whole-name occurrence fingerprint stale dependencies;
  old evidence and decisions are retained. Excerpts are hydrated only on navigation.
- Minimal review UI is opened from the existing survey screen: CPU scan/pause/
  resume, filters, source navigation, accepted/confirmed/rejected decisions, undo,
  separate alias promotion and an explicitly non-translation chapter preview.

## Changes (paths relative to QuickTranslator.AiEditor)

Core additions under `src/QuickTranslator.AiEditor.Core/BookIntelligence/`:

- `EntityMemoryModels.cs`: optional versioned data, validation, IDs and statuses.
- `EntityMemoryStore.cs`: guarded append-only/atomic transactions, bounded-buffer codec.
- `EntityMemoryProjection.cs`: read-only legacy/census/audit import, idempotent versions.
- `EntityMemoryQuery.cs`: source freshness, chronology, conflicts and isolated snapshots.
- `EntityMemoryReviewService.cs`: review/rollback/refinement/promotion and fresh sources.
- `EntityMemoryReasoning.cs`: explainable, compatible, temporal K01 proposals.
- `TranslationImpactScanner.cs`: CPU hints, shared evidence, atomic checkpoint/resume.

WPF: modified `src/QuickTranslator.AiEditor.Wpf/BookIntelligenceReviewWindow.cs`
(one separate entry); added `EntityMemoryReviewWindow.cs` and
`EntityMemoryReviewDialog.cs` in that directory.

Tests: added `EntityMemoryPersistenceTests.cs`, `EntityMemoryCoreTests.cs`,
`EntityMemoryIntegrationTests.cs`, `EntityMemoryScaleTests.cs` under
`tests/QuickTranslator.AiEditor.Tests/`, and `EntityMemoryUiTests.cs` under
`tests/QuickTranslator.AiEditor.Wpf.Tests/`.

Docs: `docs/ai-book-intelligence/ABI3_SESSION2A_DESIGN.md` and this report.
No translation/Queue/provider/prompt implementation, shared storage or resource
schema was changed.

## Verification

Commands run from the clone root, using the installed .NET 10 SDK and cached restore:

```powershell
dotnet test QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore --logger trx --results-directory artifacts/session2a-final-tests
dotnet build QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore
git diff --check
```

- Full Core: **639 passed / 0 failed / 0 skipped**, about 122 seconds.
- Full WPF: **103 passed / 0 failed / 0 skipped**, about 24 seconds.
- Total: **742 passed**, including **35 new Core + 6 new WPF cases**.
- Release solution build: **success, 0 errors**.
- Existing NU1900 remains: this environment cannot fetch NuGet vulnerability
  metadata. No vulnerability audit is claimed; auditing was not disabled. Existing
  CS8631 in `GeminiSurveyClientTests.cs:59` appears on test recompilation. No new
  compiler warnings in Session 2A files.

Coverage includes A-B-C compatible/incompatible reasoning, same-name body/mind and
similar-alias separation, title/address/relationship endings, chapter-50 revelation,
unknown reveal, stale/source deletion/reorder/unsampled occurrences, invalid UTF-16,
repeated import/scan, partial/malformed sidecars, competing revisions/exclusive lease,
injected atomic-write failure, cancellation/resume, append-only rollback/reopen,
current audit-alias promotion, legacy byte preservation and safe WPF review actions.

TRX output was moved (not deleted) under the already ignored directory
`QuickTranslator.AiEditor/artifacts/abi3-session2a/`; full results are in
`session2a-final-tests/` and focused results in `session2a-focus/`.

## Synthetic storage/performance

1,000 chapters, 1,577,893 UTF-16 units, approximately 1,578 units/chapter, eight
trigger types in each chapter's first window. No API/model/token usage.

| Measure | 500 chapters | 1,000 chapters |
|---|---:|---:|
| Event candidates | 4,000 | 8,000 |
| Sidecar bytes | 4,379,800 | 8,753,639 |
| Final-run scan/checkpoint time | 2.90 s | 10.69 s cumulative |

1,020 shared evidence references at 1,000 chapters; no excerpt marker persisted.
No-op resume: 0.34 s, same revision and IDs. Storage ratio: 2.00x.
The isolated cold focused run retained about 55.6 MB managed delta for the final
state/codec caches after comparison snapshots were released (limit: 64 MiB).
The full-suite delta was negative because pre-existing pools were reclaimed;
that is not evidence of zero memory use. These are not peak working-set figures.

The first benchmark exposed about 173 MB retained delta. Avoiding document-sized
pooled serialization and DOM validation in the new sidecar reduced the cold result
to about 56 MB. This does not alter SimpleFileStorage or other Project formats.

## Commits

- A: `eace3ba91ff52b22a4b6fcf1fdd421621d5985b8` — models/persistence.
- B: `19530d8d07cea148f138d1df2a04d5efea8e4bfb` — source/timeline/event/reasoning core.
- C: commit containing this report — minimal review UI, extra regressions, scale
  tests and the measured sidecar-buffer fix. Its SHA is in the handoff/Git history.

## Remaining limits / safety

- CPU patterns are review hints, not a semantic extractor or measured real-corpus
  event recall/precision. Repeated triggers coalesce to one type/window (900 UTF-16
  units); phrases crossing a window boundary may be missed. Registry changes do not
  automatically reinterpret completed event participants; a human can refine them.
- Only K01 compatible biological parent/sibling inference is implemented, not a
  Full Event Graph, exhaustive kinship reasoning or GraphRAG.
- The source loader and audit snapshot currently hold the corpus/state in memory;
  the synthetic benchmark is not a guarantee for 1,000 very long chapters. Each
  checkpoint batch saves a validated complete sidecar. Historical versions are
  retained deliberately; no destructive pruning/GC of approved history is provided.
- Freshness is evaluated on reload/query/navigation/review, not a background file
  watcher. Existing audit labels are historical snapshots, not context names.
- The new query is NOT connected to BookKnowledgeContextProvider, SceneContextService,
  translation prompts or Queue. Existing translation behavior is unchanged; wiring
  safe chapter context belongs to 2B. No context feature flag was enabled.
- No Gemini/Ollama/API calls, no personal Project/corpus access, no sub-agents,
  no UI redesign, no Windows package publish requested/performed for this session.
