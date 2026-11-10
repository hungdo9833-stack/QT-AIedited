# ABI3 Session 1 — P0 corpus integrity and mention census

Repository: `hungdo9833-stack/QT-AIedited`.
Branch: `codex/abi3-integrity-census`.
Exact base: `2f8746847140205a7360f67a64cfea323cf4d3ef` on
`codex/gemini-multimodel-adaptive-survey`. This is intentionally NOT the newer
QuickTranslator mirror checkout. No mirror commits were imported.

## Diagnosis: what is and is not proved

The supplied brief describes 199 evidence records: 87 current in chapters 1–8
(75 analyzed / 12 blocked) and 112 stale in chapters 9–19. Neither the actual
Manifest, ChapterDocuments nor that JSON was supplied to this session. There is
no basis to claim those eleven chapters are active or to diagnose that user's
root cause. A name in retired evidence is not proof of a current-model omission.

The following code paths and defects were reproduced with synthetic Projects:

- The base indexer retires every record whose ChapterId is absent from the active
  Manifest set (absent or soft-deleted), and every old window when source hash changes.
- It skips scanning if the stored chapter hash equals the current source hash,
  without checking current coverage. Consequently, a retired chapter reactivated
  with the same source, or a partial/interrupted index, may remain without current
  evidence indefinitely. Reconciliation now validates coverage even for equal hashes.
- Inactive retirement previously did not necessarily reach disk: saves occurred
  inside the rescan branch. Retirement-only runs now checkpoint as well.
- Source-range cache keys lacked ChapterId/EvidenceId. Identical source in two
  chapters could share a completion key although the response cites different
  evidence IDs. Keys now include both identities. Existing `AiAnalyzed` flags remain
  Resume checkpoints; old opaque keys are retained but cannot certify replacement evidence.
- Window boundaries could split a surrogate pair. New windows keep non-BMP
  characters intact while preserving exact UTF-16 coverage.

These are demonstrated implementation defects, not a proven explanation for the
specific 19-chapter survey in the brief.

## A. Integrity and reconciliation

`CorpusIntegrityAuditor` reads active Manifest entries and ChapterDocument source,
checks IDs, duplicate chapter IDs/numbers, source hash/length, exact excerpt text,
UTF-16 bounds, gaps, overlaps, surrogate boundaries and blocked/analyzed conflicts.
Inactive chapters are audit history, never inferred to be active from state alone.
Diagnostics distinguish missing index, source/hash mismatch, incomplete coverage,
unreadable documents and inactive history with precise ranges/reason codes. A hash
mismatch cannot alone distinguish source editing from a corrupted stored hash.

`ProjectCorpusIndexer` clones its input, snapshots the persisted survey before its
first write, and atomically checkpoints each repaired chapter. Exact matching old
windows are reused (including evidence IDs, analyzed flags and provider blocks).
Other old windows remain retired and auditable. A same-source refusal propagates
to overlapping replacement windows; indexing never provides a route around a block.
Only unapproved stale-dependent facts and scene caches are invalidated. Confirmed
relationships, accepted/rejected proposals, addressing periods and Project Resources
are not rewritten.

Snapshots: `Settings/ai-book-intelligence-snapshots/index-reconcile-*.json`.
Explicit developer rollback: `CorpusReconciliation.RollbackAsync(store, backupPath,
expectedCurrentRevision)`. Rollback is revision guarded, Project scoped, and refused
if approvals/rejections changed after the snapshot. A failed write/cancel leaves the
last durable checkpoint available for a fresh load and idempotent restart. There is
no destructive automatic rollback after successful chapter checkpoints.

Analyzer entry refuses invalid/unindexed active corpora before calling any client.
Acceptance also checks active chapter membership and exact source/range/excerpt,
not only a matching hash and `IsCurrent=true`.

## B. Deterministic character census

`CharacterMentionCensus` reads 100% of active OriginalText, independently of the
index or its stale flags. Known exact names come from existing profiles/review.
Unknown hypotheses require a surname/action cue, explicit introduction, or a
surname/title cue; arbitrary 2–4-Han substrings are not treated as names. A noise
dictionary rejects common generic phrases. A trie then counts exact mentions over
all source positions, including appearances outside the discovery context.

Every mention carries ChapterId, source hash and UTF-16 range. Frequency and first/
last chapter are deterministic. At least three mentions, with no profile/review,
produce a `MissingIdentityAuditItem`. Alias/title hypotheses remain separate; no
consciousness/body identity is merged or accepted. This is conservative heuristics,
not a guarantee of discovering every possible Chinese name or coreference.

Versioned derived sidecar: `Settings/abi3-character-census.v1.json`. The UI rebuilds
from actual source instead of trusting cached sidecar statistics. No Project or
knowledge-state schema migration was introduced.

## C. Completion and minimal review UI

The existing window now offers a CPU-only audit button and four audit groups:
`Missing identity`, `Stale index`, `Blocked excerpts`, `Unverified`. Existing review
forms remain in place. CPU audit rows cannot be accepted, merged, rejected or
turned into resources; they navigate to current source for manual investigation.

The audit summary reports active/indexed chapters and current, analyzed, blocked,
missing, stale, skipped and unverified excerpts. Indexed, Analyzed, Blocked, Skipped,
Stale, Verified and Complete are separate concepts. `LastError` and
`NextEvidenceIndex` are never completion evidence. Blocked is not analyzed. Fast
Adaptive skipped coverage, pending review, missing identities or invalid coverage
prevent the combined review from claiming Complete/Verified. Empty AI findings do
not prove absence of missing identities.

Diagnostic sidecar: `Settings/abi3-corpus-audit.v1.json`, version 1, with source-bound
chapter reason codes and positions. Neither sidecar contains API keys or full-source
uploads. After review changes, the UI invalidates the audit and requires CPU refresh.

## Reproducible audit examples

`CorpusIntegrityTests.Nineteen_chapters_distinguish_missing_current_active_from_legitimately_inactive`:

| Synthetic Manifest | Before | After reconciliation |
|---|---|---|
| 19 active, 11 chapters with no current windows | 8/19 indexed, 11 missing ranges, NeedsReindex | 19/19 indexed, no gaps; analysis/review still separate |
| 8 active, 11 inactive with stale history | 8/8 indexed; inactive history excluded | unchanged, no unnecessary reindex |

An additional synthetic fixture matches the brief's **counts**, without claiming
to reconstruct its real Manifest/history:

| Audit | Current | Analyzed | Blocked | Missing ranges | Stale | Unverified |
|---|---:|---:|---:|---:|---:|---:|
| Before (19 active, 8 indexed) | 87 | 75 | 12 | 11 | 112 | 0 |
| After (19 active, 19 indexed) | 199 | 75 | 12 | 0 | 0 | 112 |

The 199 original evidence IDs survive, all twelve blocks remain un-analyzed, and
the survey still reports not Complete. This is an integrity repair, not fabricated
AI analysis of the missing chapters.

`Retire_then_reactivate_same_hash_repairs_current_coverage_and_keeps_provider_block`
reproduces the actual inactive → stale → active/same-hash lifecycle. A blocked
window keeps its ID/refusal and remains un-analyzed after repair.

Other tests exercise changed source, malformed excerpts, inactive acceptance,
rollback after review changes, cancellation/restart, reordered/duplicate chapters,
non-BMP ranges, cache/dependency invalidation, no-candidate findings and skipped
coverage. Deterministic smoke tests cover 500/1000 chapter census and 1000 chapter
window integrity. Tests use temporary synthetic Projects and mocked clients only.

## Verification

Restore: `dotnet restore QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx --ignore-failed-sources -p:NuGetAudit=false`
(local package cache, no model calls).

Full suite: `dotnet test QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore --logger trx --results-directory QuickTranslator.AiEditor/artifacts/abi3-session1-tests`.

Build: `dotnet build QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore`.

Final full-suite results: **579 Core passed, 97 WPF passed; 0 failed, 0 skipped**
(676 total). Core duration 2m20s, WPF duration 24s. TRX evidence is under
`artifacts/abi3-session1-final-tests/` (ignored build output). Release solution build
completed with 0 errors / 0 warnings on the final incremental build. A clean test
compile still emits the base `GeminiSurveyClientTests.cs:59` nullable warning CS8631;
this is not suppressed or treated as a test failure. Checkpoint hashes are in the
handoff response.
An initial Debug full run found a new test fixture missing its ChapterNumber; the
fixture was corrected and the complete suite rerun. No result from that failed
run is reported as a passing full suite.

Additional mock-only engine smoke:
`dotnet run --project QuickTranslator.AiEditor/tools/QuickTranslator.AiEditor.BookIntelligence.Benchmark -c Release --no-restore -- 2 1000 QuickTranslator.AiEditor/artifacts/abi3-mock-engine-smoke.json 0`.
Both lanes completed all four evidence windows with one mocked request each. These
small synthetic numbers are a regression smoke, not real-model performance/quality.
The benchmark fixture now registers real synthetic chapters with stable segment IDs
instead of supplying an index whose chapters are absent from the Manifest.

## Changed files

- Core `BookIntelligence/`: `CorpusIntegrityAuditor.cs`, `CorpusReconciliation.cs`,
  `ProjectCorpusIndexer.cs`, `AdaptiveBookIntelligenceAnalyzer.cs`,
  `BookIntelligenceReviewService.cs`, `CharacterMentionCensus.cs`, `CorpusReviewAudit.cs`.
- WPF: `BookIntelligenceReviewWindow.cs`.
- Core tests: `CorpusIntegrityTests.cs`, `CharacterMentionCensusTests.cs`,
  `BookSurveyConcurrencyTests.cs` (fixture now has actual active ChapterDocuments).
- WPF tests: `CorpusAuditUiTests.cs`.
- Mock benchmark: `tools/QuickTranslator.AiEditor.BookIntelligence.Benchmark/Program.cs`.
- Documentation: this report.

Checkpoints: A `f18d95d`, B `e60bcc5`, C is the final handoff HEAD.

## Boundaries and remaining limitations

- No real Gemini/Ollama calls, API key usage for requests, source uploads, personal
  Project edits, Queue changes, translation prompt changes or new Entity/Event Graph.
- Existing Book Knowledge flag remains default OFF. Approved resource semantics
  and translation behavior are unchanged.
- A structurally unreadable knowledge JSON still fails closed in the existing reader.
  The auditor can diagnose in-memory invalid windows but does not silently rewrite
  malformed JSON or bypass existing validation. Restore a known-good snapshot or
  inspect a copy before attempting recovery.
- Census hypotheses require manual interpretation and are not model quality metrics.
- A live concurrent source edit after a CPU audit requires another audit. Audit
  compares source hashes/scope between its passes, and later analysis/approval
  validates source again; it does not lock a user's editor or modify their source.
- Schema-1/2 compatibility remains the base reader's contract; statistics use new
  versioned sidecars rather than altering Project, resource or knowledge schemas.

Session 2 is intentionally not started. Validate this P0 branch first.
