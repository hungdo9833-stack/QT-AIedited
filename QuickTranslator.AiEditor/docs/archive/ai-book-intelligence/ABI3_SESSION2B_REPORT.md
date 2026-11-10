# ABI 3.0 Session 2B handoff

Repository: `hungdo9833-stack/QT-AIedited`. Branch: `codex/abi3-semantic-survey`.
Required base: `15147c8c0c72ba8b53a85beb8dc711430bec75a3`.

Session 2B adds an ordered semantic survey and human review workflow to Entity Memory.
AI findings stay in a separate proposal journal until a person explicitly confirms
their source, identity references and timeline. Chapter context remains a preview;
the running translation pipeline and its existing behavior are unchanged.

## Architecture and safety

The design reuses `ProjectCorpusIndexer.ScanChapter` for complete source coverage,
including low-signal audit windows, and `CharacterMentionCensus`/`ChineseMentionDiscovery`
for local lexical hints. It does not seed these hints with future names from resources
or the full-corpus census. `AdaptiveBookIntelligenceAnalyzer` and its legacy storage
remain unchanged rather than importing its name-based identity aggregation into 2B.

`EntityMemoryQuery.AtChapter(N)` supplies only current, Confirmed, effective and
revealed facts. The request selects up to eight identities, sixteen relevant facts
and twelve prior events, not the full memory. Audit labels, hidden identity facets
and unapproved AI hypotheses are not background facts. CPU unresolved/repeated-name
questions and event hypotheses are explicitly labelled as unverified. New AI findings
do not automatically become a trusted memory for later chapters.

The strict provider-neutral schema covers the six requested tasks in one request.
All fields are required, unknown/duplicate properties and malformed enums are rejected,
and temporal dates may be null. Actuality distinguishes ActualCompleted,
HistoricalReference, PlannedRequested, HypotheticalConditional, Negated, Uncertain
and NotAnEvent. Historical mention dates are never filled as effective starts.

The independent auditor checks project/chapter/evidence scope, exact OriginalText
quotes, source SHA-256, absolute UTF-16 positions, surrogate boundaries, request-local
identity definitions, known-ID whitelists, direction and timelines. It detects selected
contradictions with Confirmed facts and conservatively flags clear request/negation/
conditional cues or explanatory `其实是` wrongly classified as completed events.
These are review guards, not a complete semantic verifier. NearbyMentions never
populate VerifiedParticipants. Unknown participants and speaker/listener stay unresolved.

`Settings/abi3-semantic-survey.v1.json` is optional and independent of older schemas.
It stores shared source references, batch/model/options/context fingerprints, attempts,
bounded proposals with quotes removed, and append-only decisions. Quotes are hydrated
from the current source for display/navigation. Invalid findings retain references,
diagnostics and an Analyzed/Failed state instead of disappearing into a success count.

Each reservation is saved before dispatch; response/proposals/checkpoint are saved
atomically afterwards. Revision guards and process/file leases prevent lost updates.
Resume preserves successful proposals, skips analyzed/validated/blocked batches and
does not automatically retry a refusal. Failed work needs another explicit run.
Cache keys include source, range, backend/model/options, prompt/schema and actual
chapter context. Changed sources/context only create new keys for affected requests;
unrelated requests keep their keys. Review also rechecks original context dependencies.

Human confirmation uses existing `EntityMemoryStore` and `EntityMemoryReviewService`,
not a second identity graph. New local identities are scoped to their batch. Explicit
bindings can reference an existing same-kind handle; matching names alone never merge
homonyms, aliases, body or consciousness. Old P0.1/P0.2 LinkExisting decisions retain
their audit-only semantics. Existing K01 inference remains proposed and separate from
AI direct claims; biological/adoptive/sworn/unknown sibling cases retain their tests.

The two journals are individually atomic, not a distributed transaction. Deterministic
memory IDs make retry after an interrupted second save idempotent. Undo also finds
human-owned memory via proposal provenance if its journal link was not committed,
demoting that decision with append-only history. Semantic memory depends on the
original confirmed context: source/review/timeline changes can stale it without
rewriting its prior approval.

Existing Gemini/Ollama adapters are wrapped, not replaced. A transport guard counts
all HTTP requests, including Ollama metadata and adapter retries, and reserves
request-body bytes plus maximum output tokens conservatively. Engine limits and
transport limits both apply. Only the explicitly selected model is constructed after
confirmation; there is no model pool/fallback in 2B. Existing Gemini refusal handling
is unchanged. Refused ranges are conservatively retained across model/context/source
changes, with no automated unblock or alternate-model resubmission.

## Review interface

The existing Entity Memory window has separate CPU/Memory and AI Semantic Survey tabs.
The AI tab provides dry-run, explicit send confirmation, HTTP/token/context limits,
pause/resume, source navigation, identity/chapter/type/status filters, uncertainty and
audit diagnostics, accepted/rejected/confirmed review and undo. Identity IDs/kinds are
visible for disambiguating equal labels. It reports batch states separately from review
states and explicitly states that Validated is not accuracy verified.

Chapter preview uses fresh `EntityMemoryQuery.AtChapter(N)` results. Neither
`BookKnowledgeContextProvider`, `SceneContextService`, translation prompts, Queue,
Project Resources nor the context feature flag was changed or enabled.

## Changed files

Under `src/QuickTranslator.AiEditor.Core/BookIntelligence/`, added:

- `SemanticSurveyContract.cs`: strict schema, typed response and parser.
- `SemanticSurveyRequest.cs`: local hints, selected chapter memory, keys and dependencies.
- `SemanticQualityAuditor.cs`: independent evidence/identity/actuality/conflict guards.
- `SemanticSurveyStore.cs`: optional append-only staging and progress sidecar.
- `SemanticSurveyEngine.cs`: ordered bounded dispatch, coverage, block and resume.
- `SemanticSurveyBackend.cs`: reuse selected adapters with HTTP/token reservation limits.
- `SemanticMemoryReviewService.cs`: explicit confirmation, bindings and recovery/undo.

Modified `EntityMemoryQuery.cs` only to honor dependencies of human-confirmed semantic
facts/events. No new entity model or old-record schema fields were introduced.

WPF: added `SemanticSurveyPanel.cs`; modified `EntityMemoryReviewWindow.cs` and
`BookIntelligenceReviewWindow.cs` for the new tab and existing backend selection.

Tests: added `SemanticContractTests.cs`, `SemanticEngineTests.cs`,
`SemanticTransportTests.cs`, `SemanticQualityAndScaleTests.cs` under the Core test
project, and `SemanticSurveyUiTests.cs` under WPF tests. This report is the only new doc.

## Verification

Final Release verification: **675 Core + 105 WPF = 780 passed**, zero failed/skipped.
This includes **36 new Core and 2 new WPF cases**. Core took about 198 seconds;
WPF about 18 seconds. Solution Release build succeeded with zero errors; no new
compiler warnings in Session 2B files. `git diff --check` passed.

Commands, from the independent clone root:

```powershell
dotnet test QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore --logger trx --results-directory QuickTranslator.AiEditor/artifacts/abi3-session2b/final-r2
dotnet build QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore
git diff --check
```

Logs remain under the already ignored `QuickTranslator.AiEditor/artifacts/abi3-session2b/`.
Existing NU1900 means this environment could not retrieve NuGet vulnerability metadata;
auditing was not disabled and no successful vulnerability audit is claimed. Existing
CS8631 remains in `GeminiSurveyClientTests.cs:59` on recompilation.

## Mock quality metrics

The neutral labelled integration fixture contains three entities and two direct
relationships. Its scripted response passes the strict schema and mechanical audit:
entity TP/FP/FN = 3/0/0, relationship TP/FP/FN = 2/0/0, fixture precision/recall = 1.0.
The mock recovers `星河`, which the CPU fixture did not classify as a person.

Eight separately labelled actuality cases preserve their expected classification/routing,
including historical, proposed, conditional, negated, uncertain and explanatory text.
Three deliberately wrong completed-event claims are flagged independently. Eight
malformed wire-contract variants are rejected. These measure the tested mock contract
and review pipeline, NOT Gemini/Ollama accuracy or real-story precision/recall.

Blocked/resume tests keep one blocked batch separate while later batches finish; changing
the selected model does not send the refused range again. Missing CPU classification
leaves unprocessed coverage, even if the provider request returned successfully. Valid
negative classifications do not create completed events or Confirmed facts.

The gold response uses one mock call, estimated input 2,839 tokens and conservative
reserve 12,739. Fake usage values are synthetic, not actual billed tokens. All real
provider requests/tokens/costs during implementation are zero.

## Synthetic performance

The focused benchmark uses 1,000 neutral synthetic chapters, 532,000 UTF-16 units,
one window/proposal per chapter and concurrency one. All responses are fake.

| Measure | 500 chapters | 1,000 chapters |
|---|---:|---:|
| Proposals and logical calls | 500 | 1,000 |
| Sidecar bytes | 1,317,210 | 2,634,599 |
| Checkpoint time in focused run | 31.50 s | 94.59 s cumulative |

Planning: 0.19 s; no-op resume: 0.09 s, unchanged revision, no additional fake calls.
Estimated input for all 1,000 requests: 3,298,991 tokens; conservative reserve:
14,119,893. No complete source excerpt marker is persisted. Storage grows 2.00x.
This measures planning and atomic sidecar snapshots, not provider latency, actual HTTP,
runtime Project-file dependency rereads or peak process memory. The complete snapshot
per request makes later checkpoints more expensive; time does not grow linearly.

## Trial workflow and remaining limits

Use a disposable Project copy. Open the existing AI survey screen, choose the intended
Gemini or Ollama profile/model, open Entity Memory and select AI Semantic Survey.
Dry-run first; inspect model, batch/input estimates, request/token reservation limits
and context size. A small context may not fit the semantic schema; increase it explicitly
within the selected backend's capability. No runtime/profile setting is changed silently.

Only when ready to send source data, choose Survey/Resume and confirm the displayed
backend and budget. Review exact evidence, actual participants/identity IDs and dates;
Accepted remains staging. Confirmed needs explicit effective/reveal chapters. Unknown
historical timing stays unresolved instead of using the mention chapter automatically.
New same-name handles stay separate unless explicitly bound. Preview the target chapter;
undo and reopen to check history. No real API test is part of this handoff.

Remaining limits:

- Real provider compatibility, model quality, token accounting and the user's 19-chapter
  story/28 CPU candidates were not evaluated. A small real preflight needs separate
  permission and its own request budget; no personal corpus was accessed here.
- The actuality guard is conservative lexical checking, not independent model reasoning;
  it can flag a genuinely completed event with a negated/planned clause nearby. There
  is no automatic semantic repair or auto-confirmation.
- Unreviewed AI proposals are not a trusted rolling context. Human review/binding is
  required for new cross-batch identities. Unresolved questions currently come from
  local repeated-name gaps and CPU event hypotheses, not a separate long-term AI agenda.
- Requests use the existing indexer's bounded windows, without an overlap/recontextualization
  repair pass. Meaning spanning windows can remain unresolved. Stored history is not
  destructively compacted, and source/whole-context snapshots stay in memory during a run.
- New relevant confirmed context can cause an earlier window to be surveyed again under
  a new key. Invalid/Analyzed findings remain visible for review rather than being silently
  retried; a changed model/version/context creates new work. Refused ranges stay blocked
  conservatively even after a source rewrite; no unblock workflow is added.
- Context integration stops at preview. The translation provider, resources and active
  translations retain their old behavior. No GraphRAG, Full Event Graph or agents.

## Checkpoints

- A: `a113ad14f6ccaf62c4d51d835480ab7d7bcbe368`.
- B: `94fb4ae62133e5791b0631c8a0fd1b535e666fff`.
- C: the commit containing this report; its SHA is supplied in the handoff and Git history.

The original dirty checkout was left untouched. Only this independent clone's requested
branch is eligible for the user-authorized push after successful verification.
