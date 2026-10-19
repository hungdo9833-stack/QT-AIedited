# Session 1 P0.1 — Missing Identity decisions and completion semantics

Base: `codex/abi3-integrity-census` at `a4251691270aa923ca1939ebbfc4297427cab18b`.
This small patch supersedes the Session 1 report's `Complete / Verified` terminology
and read-only-only Missing Identity workflow. It does not start Session 2.

## Workflow

Select a Missing identity row. The existing window now offers three explicit,
confirmation-gated actions:

- **Không phải nhân vật**: resolve the current CPU hypothesis as a false positive.
- **Xác nhận liên kết (audit)**: explicitly select an existing Character ID and
  either same identity or alias/title. No target or link type is preselected.
- **Chờ xác minh nhân vật mới**: persist a manual task in **Unverified**, not an
  accepted Character and not a request to an AI provider.

Resolved items appear in **Identity đã xử lý**. Selecting them permits a new
decision; earlier decisions remain in the audit history. Queued tasks may be
resolved by explicit linking after separately creating/checking the Character in
the existing editor, or by marking the hypothesis not a character. The patch does
not add a second Character editor or automatically accept/write resources.

## Persistence and freshness

`Settings/abi3-missing-identity-review.v1.json` is a new optional versioned sidecar.
Missing file means no decisions; existing Project/knowledge/resource schemas are
unchanged. Records retain decision ID, action, timestamp, note, superseded ID,
literal candidate, all ChapterIds/hash/UTF-16 mention ranges, target ID/link kind
and target-name snapshot/fingerprint. New decisions append; earlier decisions are
not rewritten. Atomic saves, revision checks and an exclusive `.lock` file prevent
concurrent decisions from silently overwriting history. The empty lock file is
intentionally retained and contains no Project content or secrets.

Before accepting a decision the store recounts current source and checks target
resource revision. Refresh/reopen compares the full current occurrence set with
the saved provenance. Changed mention-chapter hashes, positions/numbers, added or
removed occurrences, or changed/deleted target Chinese names/aliases require a fresh human
decision. Unrelated source chapters do not invalidate a decision. Previously
reviewed literal names are recounted even if their discovery cue disappears.
No stale decision suppresses a current Missing Identity item. If the name has no
active occurrences, its stale history remains visible but is not an active task;
it cannot be applied again if the name returns without renewed review.

Links are **audit-only associations**, never translation aliases or identity
merges. The tests compare Manifest, chapters, knowledge state and locked Project
Resources byte-for-byte before/after review and reopen.

## Completion semantics

- **CorpusProcessed**: active source/index integrity is complete.
- **AiSurveyComplete**: all active coverage was analyzed, with no blocked/skipped/
  pending excerpts. Empty output is still not a quality evaluation.
- **ManualReviewComplete**: the AI survey is processed and known AI/CPU review
  queues have no unresolved items. Newly queued identities remain pending.
- **AccuracyVerified**: always false in this patch. There is no frozen labelled
  corpus or independent quality assessment. Legacy `Verified` getter also returns
  false and the enum value is retained only for compatibility, never selected.

`Complete` remains an operational convenience for finished AI/manual processing,
not a claim that all real characters were correctly identified. UI summaries and
run completion explicitly say **Accuracy: chưa được kiểm chứng**. Heuristic names,
manual linking and absence of pending work cannot establish exhaustive recall.

## Changed files

- `src/QuickTranslator.AiEditor.Core/BookIntelligence/MissingIdentityReviewStore.cs` (new)
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/CharacterMentionCensus.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/CorpusIntegrityAuditor.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/CorpusReviewAudit.cs`
- `src/QuickTranslator.AiEditor.Wpf/BookIntelligenceReviewWindow.cs`
- `tests/QuickTranslator.AiEditor.Tests/MissingIdentityReviewTests.cs` (new)
- `tests/QuickTranslator.AiEditor.Wpf.Tests/CorpusAuditUiTests.cs`
- This report.

## Verification

Focused: 30 Core + 1 WPF tests passed. Coverage includes repeated false positives,
two explicit link kinds for a title with multiple possible Characters, durable
verification queue, reopening, source/target changes, new occurrences, superseded
history, stale submission, cancellation/revision conflict, protected file bytes and
independent completion/accuracy flags. WPF tests verify action visibility, no
implicit target selection, reopening, resolved/queued rows and new status labels.

Full suite command:
`dotnet test QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore --logger trx --results-directory QuickTranslator.AiEditor/artifacts/p01-final-tests`

Release command:
`dotnet build QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore`

Final full suite: **587 Core + 97 WPF passed (684 total), 0 failed, 0 skipped**.
Core duration 1m32s; WPF duration 19s. Release solution build succeeded with 0 errors
and 0 warnings on the final incremental build. TRX files are in the results folder
above (ignored build output). The commit SHA is in the handoff response. The existing
`GeminiSurveyClientTests.cs:59` CS8631 test nullable warning is not suppressed.

## Limits and safety

Decisions apply to the candidate's whole current occurrence set, not per-scene or
per-occurrence disambiguation. Ambiguous homonyms/titles should stay in manual
verification instead of being linked indiscriminately. Review history is append
only with no automatic compaction. Source freshness is conservative at chapter
hash level; even an unrelated sentence edit in a mention chapter requires review.

No personal Project, provider request, translation pipeline, Project Resources,
feature flag, Entity Memory, Event Ledger or incompatible migration was touched.
