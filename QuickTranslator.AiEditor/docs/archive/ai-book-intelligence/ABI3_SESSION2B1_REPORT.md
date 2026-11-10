# ABI 3.0 Session 2B.1 Adaptive Semantic Batching

Repository: `hungdo9833-stack/QT-AIedited`. Branch: `codex/abi3-adaptive-semantic-batching`.
Required base: `9a5112d5afba8a61e80760c0b2afe68c8a511154`.
Checkpoint A: `5c134ba`.

Adaptive batching reduces repeated prompt/schema overhead without changing source windows,
the six semantic tasks or the translation pipeline. It reuses Entity Memory, Event Ledger
and reasoning rather than implementing them again.
The default ceiling is four evidence windows per request. Two through six can be selected
in Dry-run; one is retained as a programmatic baseline mode. All verification uses synthetic
fixtures and mocks. No real Gemini/Ollama call or personal Project was used.

## Planning and context

The original complete coverage scan still creates atomic evidence IDs, chapter IDs,
SHA-256 values and absolute UTF-16 ranges. The planner packs adjacent windows only within
one chapter/source/backend and only when their confirmed context snapshots and dependency
fingerprints are identical. It sends that snapshot once, with lexical hints, CPU hypotheses
and questions separately attached to each evidence. It does not union unrelated identity
snapshots or promote adjacent names to participants.

Packing respects the requested evidence ceiling, an 85 percent context estimate threshold
including maximum output, and a per-request reserve ceiling. Identity/body/mind complexity
signals lower the ceiling to two. An oversized singleton is retained rather than truncated
or dropped, reported as over context, and rejected before provider dispatch. Different
snapshots, gaps and chapter boundaries can therefore leave one evidence in a request.

The provider output cap is unchanged. More findings than fit in the response/output limits
must remain for review or an explicit Resume; a larger input batch is not permission to
claim all evidence was reviewed.

## Contract and audit

Wire version 1 remains readable for singleton requests and stored 2B findings. Multi-evidence
requests use strict wire version 2 (`abi3-semantic-2`); finding fields are unchanged.
The auditor checks response scope and unique request-owned `reviewed_evidence_ids`, then
checks each finding's evidence set against its actual support IDs. Every support must match
its own range and exact current source quote, including surrogate boundaries. A quote at a
valid offset in another evidence is not transferable.

Coverage is independent of findings: absent evidence stays Pending, valid reviewed evidence
can finish even when a sibling is absent, and invalid/foreign/duplicate coverage cannot
finish anything. Findings referring to unreviewed evidence remain invalid, with diagnostics.
Unprocessed CPU classifications are still separate from technical evidence coverage.
CPU candidate IDs must also belong to a cited evidence member, not just exist somewhere
in the submitted batch. Invalid proof records an evidence-level Failed state rather than
a success cache hit; explicit Resume retries that evidence without resending good siblings.
The prior invalid proposal is retained as audit history, not counted as a valid finding.

Cross-evidence reuse of an unlinked local identity is flagged for review. Equal names with
different local IDs remain separate. Multi-evidence relationships need joint participant
proof and a conservative typed relationship cue; addressing needs joint participant proof
and quoted forms. These lexical guards are not a semantic truth engine. A cross-boundary
event may cite both evidence windows, but dropping one declared support fails audit.
Human confirmation rehydrates and reaudits the correct evidence, including later windows;
source navigation and details no longer assume every finding belongs to the first window.

## Checkpoints and compatibility

`Settings/abi3-semantic-survey.v2.json` adds append-only per-evidence results and batch
membership/cache keys. When only the original v1 journal exists, it is loaded and reused;
the first real update writes v2 without modifying v1. Old proposals, attempt IDs and human
reviews survive. No-op Resume does not force a migration or increment revision. Older 2B
binaries do not consume v2; after upgrading, do not alternate versions on the same Project.
Leases include the old and new run/write lock names, but this is not bidirectional syncing.

An atomic cache key retains the proven v1 source/range/model/options/context/prompt/schema
identity. This explicit v1 compatibility rule is valid because wire v2 extends coverage
scope, not the finding semantics. New batch fingerprints additionally include the entire
ordered atomic key set, model/options, multi-evidence prompt and schema version. Repacking
four evidence as two or six does not invalidate completed atomic results. Changing source,
relevant context or model does. A new semantic contract with different finding meaning
would require a new atomic cache namespace, not just reuse this compatibility rule.

Resume packs only incomplete, nonblocked atomic evidence. Completed proposals are not
duplicated. Missing coverage, Failed and interrupted attempts require an explicit next run;
there is no loop retrying a partial response. A provider refusal blocks every member of
that submitted batch, since the provider did not identify a safe subset. Those ranges stay
blocked across repacking, model/context/source changes, and cannot be sent to another model
automatically. Nonblocked evidence outside those ranges can continue.

Reservations precede dispatch; proposals, attempt outcome and individual evidence states
share an atomic checkpoint. Existing revision guards and append-only history remain.
Engine request budgets and the existing transport budget guard both apply; the latter counts
every actual HTTP request, including metadata and retries, and gates it before sending.

## Quality comparison with labelled mocks

The same neutral passage from Session 2B supplies three labelled characters and two direct
relationships, alongside its eight actuality cases. Two extra independent homonym passages
exercise identity separation. The identical scripted findings go through the singleton and
multi-evidence contracts/auditor; no test substitutes model output quality for model accuracy.

| Metric | Singleton baseline | Adaptive |
| --- | ---: | ---: |
| Character findings including two homonyms | 5 | 5 |
| Direct relationships | 2 | 2 |
| Actuality labels retained | 8 of 8 | 8 of 8 |
| Evidence attribution errors | 0 | 0 |
| Missing evidence | 0 | 0 |
| Identity confusion in this labelled fixture | 0 | 0 |
| Requests | 11 | 3 |
| Input estimate | 40,578 | 19,842 |
| Conservative reserve | 168,179 | 72,193 |

Separate adversarial tests retain partial coverage, flag false identity joins/co-occurrence,
reject foreign/misattributed support, and cover refusal, timeout, cancellation, source/context
change, partial-success reopen/Resume, v1 migration, human confirmation and idempotency.
The conservative guards intentionally make some ambiguous multi-evidence findings less
promotable than v1 rather than automatically merging them to match a success count.

## Request and cost comparison

The user-reported real-project baseline is 199 requests, 768,794 estimated input tokens and
3,961,867 conservative reserve at context 65,536, selected model `gemini-3.8-flash`.
It cannot establish the new real-project count without its chapter sizes and confirmed
context layout. The Project was not accessed, as requested.

A separate synthetic 19-chapter corpus has 199 contiguous 900-UTF-16 evidence windows
(179,100 UTF-16 units), nine chapters with eleven windows and ten with ten. At the same
context and model label, default packing measures:

| Metric on the same synthetic corpus | Singleton baseline | Adaptive |
| --- | ---: | ---: |
| Evidence retained | 199 | 199 |
| Requests | 199 | 57 |
| Input estimate | 747,714 | 380,395 |
| Conservative reserve | 3,083,529 | 1,381,915 |

Request reduction is 71.36 percent and input estimate reduction is 49.13 percent. The
distribution is 38 four-evidence, 9 three-evidence and 10 two-evidence batches. This reaches
the 40–70 target on this fixture, not a promise that the private Project will produce 57.
More identity complexity, incompatible context or a smaller budget can exceed that target;
packing does not discard evidence to force it.

Input estimates retain the baseline UTF-8-byte-count divided by three heuristic. Conservative
reserve uses a byte ceiling plus maximum output (transport uses actual serialized body bytes).
Neither number is an actual tokenizer count or provider usage. Mock completion usage counters
do not measure model tokens. Actual paid calls are zero and actual provider tokens are unknown.

## Interface

The existing semantic panel adds only the evidence ceiling and Dry-run metrics: model/context,
total evidence, planned batches, calls still needed, completed/pending/blocked evidence,
input estimate, conservative reserve, distribution and over-context count. Changing the
ceiling or budget invalidates the preview. Explicit send confirmation, Pause/Resume and
human review remain. No Resources, translation context flag or Queue behavior is changed.
Pending calls count logical model generations; actual HTTP can include backend metadata.
The explicit HTTP ceiling still applies to all of them.

## Context and transport regressions

The final verification also covers prior confirmed events used by a batch context, not
only confirmed facts. New batch metadata records these event inputs; demotion/source changes
invalidate the batch review and confirmed semantic dependents through the existing provenance
fingerprint mechanism. `EntityMemoryQuery` has only a small freshness-guard extension for
these event IDs; no new memory graph, inference or storage schema was implemented. V1
journals retain their historical dependency metadata without fabricating missing provenance.

The semantic-only transport converts connection failures, HTTP 408 and 5xx into a failed
attempt before the shared adapter can retry. HTTP 429 already stops the selected Gemini
model through its existing typed exception. Tests inspect the serialized v2 response schema
and assert one mock HTTP request for network/503 failures. The shared Gemini/Ollama clients
and translation Queue were not modified.

## Remaining limits

Real Gemini schema compliance, token usage, model latency and semantic recall/precision are
not measured by these mocks. Cross-window semantics can still be ambiguous; there is no
automatic repair/overlap pass, identity merge or model fallback. Complex relationships
without the conservative lexical proof remain for review. Provider refusal has no automatic
override. Evidence keys use the whole chapter source hash, so a chapter edit invalidates its
windows even when a specific quoted substring did not change. Confirmed-context changes
affect only requests whose snapshot/dependencies change.

The sidecar retains full append-only history and still rewrites a snapshot per checkpoint;
size grows with evidence/proposals/attempts, while total checkpoint time is superlinear as
history grows. Repeated failed/manual runs add audit records. No storage-engine redesign,
Entity Memory reimplementation or translation pipeline integration is part of this patch.

## Scale benchmark

The final full-suite run uses 1,000 synthetic chapters with four evidence windows each,
3,600,000 UTF-16 units in total. All 4,000 proposals and evidence checkpoints survive.
Concurrency is one, preserving the existing ordered semantic engine.

| Metric | Result |
| --- | ---: |
| Singleton requests for the same corpus | 4,000 |
| Adaptive requests | 1,000 |
| Input estimate before and after | 15,030,874 / 7,231,794 |
| Planning both plans | 1.133 s |
| First 500 chapters | 68.50 s |
| Additional 500 chapters | 177.14 s |
| Total mock execution and checkpoints | 245.63 s |
| Sidecar at 500 and 1,000 chapters | 4,047,274 / 8,095,675 bytes |
| Resume after changing to singleton grouping | 0.199 s, zero extra calls, unchanged revision |
| Managed heap sampled after benchmark | 322,852,352 bytes |
| Testhost peak working set | 649,105,408 bytes |

Memory measurements include both plans, the mock/testhost and the rest of the suite; they
are not an isolated production-app retention measurement. The old 2B 1,000-chapter singleton
benchmark also passes (532,000 UTF-16 units, 99.46 s execution, 0.082 s no-op Resume,
2,895,648-byte sidecar). The two timing corpora differ in size and proposal count, so they
do not establish a model speedup. Model/HTTP latency and actual token usage remain unmeasured.

## Verification and changed areas

Final full suite: **696 Core and 107 WPF tests passed, 0 failed, 0 skipped** (803 total,
23 more than the required base). Release solution build passed with zero errors. The build
retains 12 NU1900 audit-metadata warnings because NuGet's vulnerability endpoint is unreachable;
test compilation also retains the pre-existing CS8631 warning in `GeminiSurveyClientTests.cs`.
No new warning is introduced by this patch. `git diff --check` passed.

```powershell
dotnet test QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore --logger trx --results-directory QuickTranslator.AiEditor/artifacts/abi3-session2b1/final-r2
dotnet build QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore
```

Local TRX evidence is under `QuickTranslator.AiEditor/artifacts/abi3-session2b1/final-r2/`;
the fixtures, assertions and benchmark output statements are committed source.

Core batching changes are `SemanticAdaptiveBatching.cs`, `SemanticEvidenceCache.cs`,
`SemanticSurveyRequest.cs`, `SemanticSurveyContract.cs`, `SemanticQualityAuditor.cs`,
`SemanticSurveyStore.cs` and `SemanticSurveyEngine.cs`. Review/transport freshness fixes
are in `SemanticMemoryReviewService.cs`, `SemanticSurveyBackend.cs` and the small semantic
dependency guard in `EntityMemoryQuery.cs`. The existing UI change is confined to
`SemanticSurveyPanel.cs`. Regression source is `AdaptiveSemanticTests.cs`,
`AdaptiveSemanticBenchmarkTests.cs`, `SemanticTransportTests.cs` and `SemanticSurveyUiTests.cs`.
The sixteenth changed file is this report. No translation Queue or Resources implementation
file is changed.
