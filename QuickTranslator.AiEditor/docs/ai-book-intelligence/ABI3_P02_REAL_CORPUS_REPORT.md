# P0.2 — Read-only real-corpus follow-up

Follow-up to `2ca59d38ca9eadce08412495970a40efa84c5cc2` on
`codex/abi3-integrity-census`. The user supplied a local TXT folder after the initial
fixture-only result. No raw text, source excerpts or personal Project files are
included in Git. No Gemini/provider request, translation change or Session 2 implementation.

## Input and reproduction

The original Chinese TXT has raw SHA-256
`CE2EA8CB33C4C6AA9E5F63B46052B33ECEF65E5F36669CD755F518047AC2684D`.
It has 172,647 UTF-16 units. The existing ChapterDetector produces **19 chapter
bodies / 172,119 UTF-16**, excluding 528 units of headings/prefix material.
Bodies are not trimmed or rewritten. The Vietnamese TXT is not used.

With empty resources/review state, the frozen P0.1 discovery/counting rules reproduce
**38 candidates / 24 Missing Identity** exactly. The diagnostic harness uses the
old discovery/filtering/threshold logic and equivalent overlapping literal counts;
its counting loop is not a timing benchmark of the original trie implementation.
The seven supplied missing spellings are all absent from that old candidate list.

A local ignored harness under `artifacts/p02-real-corpus-check` reads TXT directly
and invokes the production scanner in memory. No Project open/import/save is needed.
It hashes all **19 files** recursively before/after the measurement; every hash and
the file count are unchanged. The full local diagnostic JSON is
`artifacts/p02-real-corpus-check/final-measurement.json` (ignored, not committed).

## New regression discovered and fixed

The initial P0.2 commit found all seven target spellings, but its weak surname/
boundary rule also promoted ordinary prose to identities: **1,134 diagnostic
spellings / 287 PersonCandidate**, including words such as 任务/任何人 and chopped
name-plus-predicate forms. Its small synthetic gold set did not expose that failure.

The follow-up fixes:

- Require a left lexical boundary for free subject/possessive/alias/title discovery;
  remove the broad "any surname before any predicate/function word" rule.
- Treat a newline between Han characters as a soft wrap, not proof that a shorter
  name ends there. Do not normalize/concatenate source or change UTF-16 offsets.
- Add generic 小姨/闺蜜/extended family roles and role-following action contexts;
  these recover the rare reported name without embedding it in production rules.
- Add contextual contact/hand-off/waiting patterns and conversational pronouns.
  Generic surname titles such as 老/总/董/女士/秘书 remain titles, not new identities.
- Use grammatical fragments, ordinary-word vocabulary and independently evidenced
  shorter spellings to mark name-plus-predicate expansions uncertain. This is
  lexical uncertainty, **not an identity association or automatic merge**. Existing
  primary names/aliases in resources stay authoritative and are not rewritten.
- Preserve family addressing and modal particles as addressing context; do not turn
  pronouns/self-reference into names. Explicit introductions can still recognize
  ordinary-word homonyms and names containing grammatical characters.
- Keep at most **8 navigation samples for Noise / Uncertain**, 128 for other
  categories. All-occurrence counts/fingerprints/chapter scope remain exact.
  Prefix checking uses bounded spelling-prefix lookups, not all-pairs comparison.

Changed production files: `CharacterMentionCensus.cs`, `ChineseMentionDiscovery.cs`,
`MentionOccurrenceAccumulator.cs`. Regression changes:
`CharacterCensusQualityTests.cs`, `MissingIdentityReviewTests.cs`.
Documentation: this addendum and `ABI3_P02_REPORT.md`.
No UI, resource, knowledge-state or decision-journal schema changes in this follow-up.

## Actual source result

| Group | Count |
| --- | ---: |
| PersonCandidate / Missing Identity (empty resources) | 12 |
| AliasCandidate | 9 |
| TitleCandidate | 17 |
| AddressingTerm | 26 |
| Noise / Uncertain | 183 |
| All diagnostic spellings | 247 |

247 is **not** a count of characters: it includes 183 weak/noisy forms plus addressing
terms. The comparison with the old mixed Missing Identity count is **24 → 12 person
hypotheses**; 26 alias/title hypotheses have separate queues and still require
human review. No hypothesis has been accepted into Project Resources.

Person spellings/counts: 刘刚3, 柳恒127, 柳月影375, 柳轻锋1380, 毛亮48, 沈夕颜476,
王松17, 秦子卿6, 秦梦岚368, 秦梦芸3, 秦长荣12, 陈馨玉16.

Aliases/counts: 小亮3, 小岚2, 小峰6, 小影70, 小影姐55, 小沈2, 小锋179, 柳少13, 阿岚5.
These are separate hypotheses: in particular **小峰 is not automatically linked
to 小锋**, nor are other spellings linked to any full name.

Titles/counts: 吴老3, 李总4, 柳同学1, 柳大小姐1, 柳少爷2, 柳总23, 毛老2,
沈秘书15, 王阿姨10, 王院长1, 秦女士7, 秦总1, 秦老太爷24, 秦董9, 秦阿姨6,
钱老师2, 陈老师10. None implies a unique identity.

| Reported omission | Actual count | Follow-up routing |
| --- | ---: | --- |
| 毛亮 | 48 | PersonCandidate |
| 王松 | 17 | PersonCandidate |
| 刘刚 | 3 | PersonCandidate |
| 秦梦芸 | 3 | PersonCandidate |
| 秦老太爷 | 24 | TitleCandidate |
| 小锋 | 179 | AliasCandidate |
| 小影 | 70 | AliasCandidate |

All **7/7** reported omissions are found and counted correctly; all **14/14**
confirmed noise spellings are either absent as hypotheses or Noise / Uncertain,
never Person/Alias/Title. `妈妈` retains **527 occurrences** as AddressingTerm,
not Missing Identity and not an activated translation prompt entry.

This validates the user's partial labels only. There is no exhaustive human gold
set for every real-source spelling; global real-corpus FP/FN/precision/recall are
**not established**. The 183 uncertain forms are not all claimed false positives.
The separate 20-positive labelled synthetic regression still measures TP20, FP0,
FN0 (precision/recall 100% on that fixture, not on arbitrary real novels).

## Performance

Actual 19-chapter CPU scan: **243.50 ms**, serialized census sidecar **619,175 bytes**.
This includes first-use scanner initialization, excludes TXT loading/heading
detection and JSON serialization. No inference/network/model cost.

Additional scale check cycles the **19 distinct original chapter bodies** to 500/
1,000 synthetic chapter identities. This tests recurring real prose and candidate
diversity, not 1,000 independently authored chapters or newly loaded Project data.
Input strings are reused; memory figures exclude pre-created input and are not
peak process working set.

| Chapters | Scanned UTF-16 | CPU scan | Thread allocations | Retained GC delta | Sidecar bytes | Stored samples |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 500 | 4,526,857 | 799.38 ms | 31,326,720 | 1,362,144 | 2,832,381 | 8,686 |
| 1,000 | 9,055,824 | 1,232.93 ms | 61,536,896 | 2,171,592 | 4,389,551 | 11,152 |

Both retain 247 spellings, exact counts and whole-source fingerprints. Navigation
samples are bounded per spelling, but affected-chapter IDs/hashes still grow with
corpus scope. Legacy diagnostic files/history are not included in these sizes or
automatically compacted.

## Verification and limits

New neutral fixtures cover soft-wrapped name prefixes, subject/name suffixes,
family roles, common nouns, self-reference, contextual nicknames, rare names and
explicit introductions overriding grammatical characters. P0.1 tests also check
source changes beyond the **8-position uncertain sample cap**, rejection of stale
submissions, exact counts and preservation of old history/resources/evidence.
No original paragraphs are copied into test fixtures.

Focused suite: **33 Core + 1 WPF passed** before the final full run.
Full-suite command: `dotnet test QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore --logger trx --results-directory QuickTranslator.AiEditor/artifacts/p02-real-final-tests`.
Final full suite: **604 Core + 97 WPF = 701 passed, 0 failed, 0 skipped**.
Core duration 1m43s, WPF duration 19s. The labelled 20-positive regression still
reports TP20/FP0/FN0. The automated 500/1,000-chapter four-spelling fixture also
passes (193/591 ms; 228,355/359,372 sidecar bytes).

Release solution build (`dotnet build QuickTranslator.AiEditor/QuickTranslator.AiEditor.slnx -c Release --no-restore`)
succeeded in 2.76s with **0 errors and 1 NU1900 warning**: NuGet vulnerability
metadata could not be retrieved from api.nuget.org in the restricted environment.
This warning appeared after restoring the local diagnostic harness, is not an
application compile error and has not been suppressed in repository settings.
The existing CS8631 nullable warning at `GeminiSurveyClientTests.cs:59` also appeared
during test compilation. `git diff --check` passed. Commit SHA is in the handoff.

Remaining limitations: still heuristic, not a segmenter/identity resolver. Some
unanchored rare names and text errors may remain uncertain or undiscovered. Soft
wraps are prevented from creating false boundaries, not joined into cross-line
name occurrences. Literal counts therefore do not include a spelling split across
lines. Ambiguous homonyms, titles and variant spellings still need human review.
Corpus/AI/manual completion remains separate from AccuracyVerified (always false).
Entity Memory and Translation-Impact Event Ledger remain unstarted.
