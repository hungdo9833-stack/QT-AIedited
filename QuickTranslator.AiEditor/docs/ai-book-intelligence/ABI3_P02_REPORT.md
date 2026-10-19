# Session 1 P0.2 — Contextual Character Mention Census

Base: `codex/abi3-integrity-census` at
`fb393c824b6c9915a65af5f078a0bef818753c1f`. No Session 2 work.

## Root cause

P0.1 counted literal occurrences across all active source, but only **after** a
name was seeded by existing resources/reviews or three narrow discovery regexes.
Repeating a name 48 times did not help if no occurrence matched those discovery
contexts. The old surname lexicon also omitted the common surname `刘`; titles
did not include `老太爷`; family-role introductions, conversational calls and
possessive/object contexts were missing. Almost every discovered form entered
the same Missing Identity queue once it reached three occurrences, including
aliases, grammatical suffixes and ordinary words that happened to start with a
surname. Census/decision sidecars retained every occurrence, duplicating mention
arrays in the derived Missing Identity report.

## Code changes

- `ChineseMentionDiscovery.cs` (new): generic surname/compound-surname vocabulary,
  explicit introductions, family roles, vocatives, subject/action/modifier,
  object/contact, repeated boundary/possessive contexts, aliases and qualified
  titles. Introduction context can recognize a name without a known surname.
  Lexical noise and grammatical tails remain inspectable as uncertain, rather
  than automatically becoming identities. Explicit introductions can override
  ordinary-word homonyms. A regression protects legitimate names ending in `过`.
  No complete names from the reported novel are in production code.
- `CharacterMentionCensus.cs`: five explicit categories; only **PersonCandidate**
  enters Missing Identity. Strongly cued rare names do not need three mentions.
  Once discovered, each literal spelling is counted across all active source,
  including contexts not matched by the discovery rules.
- `MentionOccurrenceAccumulator.cs` (new): exact total/first/last chapter counts,
  affected chapter IDs and incremental SHA-256 over **every** source hash/range;
  at most 128 navigation positions per spelling. The derived v2 sidecar references
  Missing Identity names instead of duplicating their mention arrays.
- `CorpusReviewAudit.cs` and `BookIntelligenceReviewWindow.cs`: add existing-window
  groups for Alias candidates, Title candidates, Addressing terms and
  Noise / Uncertain. Unresolved aliases/titles can use the confirmation-gated
  P0.1 review actions, but are not labelled new identities. Noise/addressing do not
  automatically create identity-review tasks. `妈妈` stays available with source
  context; this patch does not insert it into translation prompts.
- `MissingIdentityReviewStore.cs`: optional whole-occurrence fingerprint,
  total frequency and affected chapter/hash scope. Capped navigation samples
  cannot hide a later changed/new occurrence. Existing P0.1 full-provenance records
  are compared using the same canonical fingerprint when new fields are absent.
- Tests: `CharacterCensusQualityTests.cs` (new),
  `CharacterMentionCensusTests.cs`, `MissingIdentityReviewTests.cs`,
  `CorpusAuditUiTests.cs`; this report is the eleventh changed file.

## Before/after — explicitly synthetic, not the original 19 chapters

The actual 19-chapter TXT/Project was not attached to this task. No personal
Project was read or modified. The 38-candidate/24-Missing-Identity result therefore
cannot be honestly recomputed here. The following fixture reproduces the supplied
seven frequencies in labelled grammatical contexts, not the unseen original
sentences. The frozen P0.1 discovery implementation is retained **only in tests**.

| Spelling | P0.1 fixture discovery | P0.2 category | Exact fixture count |
| --- | --- | --- | ---: |
| 毛亮 | missed | PersonCandidate | 48 |
| 王松 | missed | PersonCandidate | 17 |
| 刘刚 | missed | PersonCandidate | 3 |
| 秦梦芸 | missed | PersonCandidate | 3 |
| 秦老太爷 | missed | TitleCandidate | 24 |
| 小锋 | missed | AliasCandidate | 179 |
| 小影 | missed | AliasCandidate | 70 |

The quality fixture includes 17 person names, two aliases, one qualified title,
the 14 supplied noise spellings and four occurrences of `妈妈`. It covers 2–4 Han
characters, a compound surname, a name without a known surname, a one-occurrence
family introduction, a conversational call and ordinary words overlapping surnames.

| Labelled extraction metric | P0.1 | P0.2 |
| --- | ---: | ---: |
| True positive | 10 | 20 |
| False positive | 14 | 0 |
| False negative | 10 | 0 |
| Precision | 41.67% | 100% |
| Recall | 50% | 100% |

P0.1 false positives: 房门, 温柔, 温柔的, 张的, 严肃的, 安慰, 解释, 郑重,
马路, 安静的, 金项链, 高兴的, 小锋了, 方的.
P0.1 false negatives: the seven reported spellings above plus 欧阳青川, 星河, 赵宁.
P0.2 has no false positives/negatives **on this fixture**: person TP17, alias TP2,
title TP1, each with precision/recall 100%. `妈妈` is AddressingTerm, not Missing
Identity. These figures are a regression guard, not evidence of exhaustive recall
or verified accuracy on real novels. All exact spellings are test data only.

## Persistence, safety and P0.1 compatibility

- No automatic merging of characters, aliases, bodies or consciousness; no
  resource writes, AI/provider calls, prompt integration or translation changes.
- Original v1 census diagnostics are left untouched. New derived statistics go
  to `Settings/abi3-character-census.v2.json`; they are rebuilt from source, not
  trusted as survey/checkpoint authority. Project, knowledge and resource schemas
  are unchanged; the optional decision journal remains schema 1.
- Existing history records/IDs/notes/actions and superseded decisions are retained.
  Old records missing the new optional fields reopen without a destructive
  migration. A 200-occurrence legacy record remains current with only 128 census
  navigation samples. Changing/adding a mention outside those samples invalidates
  the decision and rejects a stale submission. Unrelated chapters remain unaffected.
- Existing explicit target/link selection, confirmation, queue-new-character,
  revision/lock/cancellation safeguards remain. Corpus processed, AI survey,
  manual review and accuracy stay separate. AccuracyVerified/legacy Verified are
  still false; no lack of pending work asserts recognition correctness.
- Regression tests compare manifest, source chapter, survey/checkpoint/evidence
  and resource bytes before/after census, human decisions and reopen. WPF tests
  exercise all new groups, source navigation, action restrictions and P0.1 reopen.

## Performance (CPU-only synthetic source)

500 and 1,000 chapters, 100 repeated short passages per chapter, four distinct
spellings. Input strings are created **before** memory measurement. Each spelling
has 50,000/100,000 exact occurrences; only 128 positions are retained per spelling
(512 total). Timings exclude Project loading and JSON persistence I/O. Serialized
sidecar size is measured separately. This is a mention-dense bounded-sampling test,
not a diverse thousand-chapter natural-language corpus or end-to-end app benchmark.

| Chapters | UTF-16 units | Scan time | Allocated bytes | Retained delta bytes | Sidecar bytes |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 500 | 1,450,000 | 216 ms | 93,090,024 | 176,728 | 228,355 |
| 1,000 | 2,900,000 | 604 ms | 186,119,376 | 320,880 | 359,372 |

Allocated bytes are cumulative allocations on the scan thread, **not peak working
set**. Retained delta uses forced GC before/after scanning, excluding the already
allocated input; it is not an end-to-end process-memory measurement. Source loading
is still an existing all-chapter in-memory operation. JSON measurement does not
include old diagnostic files or append-only human history.

## Verification

Commands from the repository root:

```text
dotnet test QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore --logger trx --results-directory QuickTranslator.AiEditor/artifacts/p02-final-tests-r2
dotnet build QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore
git diff --check
```

Final full suite: **599 Core + 97 WPF = 696 passed, 0 failed, 0 skipped**.
Core duration 1m41s; WPF duration 18s. TRX output, including labelled metrics and
the performance measurements above, is under `artifacts/p02-final-tests-r2`.
The existing nullable warning CS8631 at `GeminiSurveyClientTests.cs:59` appeared
during test compilation and is not suppressed or changed by this patch.
Release solution build succeeded: **0 errors, 0 warnings** on the final incremental
build (2.16s). `git diff --check` passed. The commit SHA is in the handoff response.

## Remaining limits

This is a rule-based CPU census, not a Chinese segmenter, statistical NER or
identity resolver. Names without any supported context (especially unusual
surnames/non-BMP Han), ambiguous ordinary-word names and unseen predicates can
still be missed or misclassified. Repetition plus a surname is not proof of a
person; remaining weak homonyms require human review. Counts are exact literal
substrings, not disambiguated per-scene entities. Family terms inside longer
titles can also appear in the addressing catalogue; no speaker/listener identity
is inferred from them. The real TXT must be tested separately before claiming a
production recall/precision improvement or a new candidate/Missing Identity count.

Navigation exposes the first 128 positions, not every occurrence; all-source
fingerprints/counts/chapter scope remain exact. Memory still scales with loaded
source, number of distinct candidate spellings and candidate/chapter scope. Legacy
full history is not compacted and append-only human history can continue growing.
Freshness conservatively invalidates a decision on any edit in a chapter containing
that spelling. Entity Memory and Translation-Impact Event Ledger remain unstarted.
