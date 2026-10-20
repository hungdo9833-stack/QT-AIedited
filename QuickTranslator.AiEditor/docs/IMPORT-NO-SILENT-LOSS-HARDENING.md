# Import without silent text loss — audit & completion plan

Status: VCR + SCA implemented on `codex/import-no-silent-loss-hotfix`. Review the limitations and rerun the full Windows test suite before merge.

Implementation details and verification evidence: [VERIFIED-CHAPTER-RECONSTRUCTION.md](VERIFIED-CHAPTER-RECONSTRUCTION.md).

## Confirmed sources of loss

1. ImportPreviewEditor.AutomaticallyResolveDuplicates used HashSet<int> and silently excluded every subsequent occurrence of the same chapter number, without comparing its body.
2. SafeBulkImportService imports only ImportableChapters; ValidateExactCoverage() proves the preview partitions the source, not that imported chapters cover all intended source characters.
3. External QT Text Toolkit 1.1 also chooses the longest chapter for equal (part, chapter) numbers, without semantic comparison. It is a separate tool; this branch cannot repair its binaries.
4. Detection can mistake chapter-like lines inside ordinary prose as headings, creating spurious duplicates and truncated chapters.
5. Existing tests asserted that silently ignoring later duplicate numbers was successful.

## Immediate fix in this branch

- Preserve both same-number chapters and block import until the user resolves the duplicate.
- Allow manual exclusion, but mark excluded-content divergence as an explicit warning and preserve its original source in the source rescue archive.
- Update tests to reject the old auto-skip behavior.

## Mandatory follow-up before production completion

### Phase A: header reliability

- Only recognize heading candidates from standalone heading lines and valid structural layouts. Add negative test cases where prose contains '第2章', time, quotes, repeated numbering and bracketed book titles.
- Classify same-number candidates as: exact duplicate, overlapped/copy, chapter continuation, false heading, ambiguous. Never resolve by chapter number alone or by length alone.
- Keep source order and explicit volume identity. Do not globally conflate chapter '2' from different parts/volumes.

### Phase B: full coverage preflight (must be blocking)

- In preview, show counts for selected body UTF-16 units, heading units, excluded/duplicate body units, unclassified source units, volume metadata, and source rescue units. These totals must reconcile exactly with the decoded original, preserving offsets.
- For body content of any excluded candidate, require a per-item user resolution: merge into target chapter, mark as exact duplicate with byte-equal normalization proof, or explicit discard with source rescue and reason. No implicit exclusion.
- If the result cannot represent intended source coverage unambiguously, Import button must remain disabled.
- Preserve all source spans with provenance through selection, split, merge, ordering, and save. All decisions must survive reopening the preview.

### Phase C: post-commit verification

- Before import record source hash and plan fingerprint.
- After import, reopen all chapters, verify each chapter.OriginalText against the exact content span chosen in plan (SHA256 and full equality), and concatenate actual segment.SourceText for exact equality to OriginalText.
- For excluded bytes require an explicit disposition ledger and verified rescue. Report imported, user-excluded, undecided and total source text. Block completion status on any mismatch.
- Perform same verification for existing chapters marked AlreadyImported; do not silently accept matching chapter count.
- Tests covering interrupted chunk commit, rollback, resume, multibyte Chinese source, mixed newline, bracket headings, repeats, deleted/history collision, large (500+ chapters).

### UI acceptance

- If chapter 2 appears 4 times with different bodies, show 4 candidates, no default exclusion, import disabled with clear Chinese-text preview and source offsets.
- If candidate heading is false and its span is inside chapter 1, user can merge back safely and see a before/after delta.
- 0 warnings is allowed only when no unresolved excluded body text remains.
- Source rescue is disaster recovery, not evidence that text was imported successfully.

### Test commands

dotnet restore QuickTranslator.AiEditor.slnx --locked-mode
dotnet test tests/QuickTranslator.AiEditor.Tests/QuickTranslator.AiEditor.Tests.csproj
dotnet test tests/QuickTranslator.AiEditor.Wpf.Tests/QuickTranslator.AiEditor.Wpf.Tests.csproj

Run on Windows/.NET 10 with test fixtures that contain neutral, non-sensitive dummy Chinese text.

## 2026-10 revision: default decisions instead of blocking

The blocking policy above made whole books unimportable for text the user could not act on (e.g. `------------ 正文卷 ------------`
before chapter 1 of 隐婚100分, or one low-confidence heading: chapter 878). The no-silent-loss guarantee is kept, the block is not:

- **Every character still has a ledger status**, and `Unresolved` is no longer produced. Included chapters are imported (whatever the
  detector's confidence); excluded chapters stay in the raw snapshot (`ExplicitlyExcluded` now also covers default decisions);
  text outside chapters is `SourceMetadata`.
- **Default decisions** (`ImportPreviewEditor.ApplyDefaultDecisions`): a low-confidence numbered heading is imported; an unnumbered
  heading right after a chapter (番外, 后记, a false heading) is imported inside that chapter and named in a warning; the same number
  again right after itself with no title of its own (`第24章1`, `（续）`, `（二）`, uploader notes such as `(PC修改)`) continues that
  chapter; the same number elsewhere or with a different title keeps the first and lists the others (`ChapterNotImported`,
  `ExcludedDifferentContent`); a missing title becomes `Chương N`; an included chapter without text is left out.
- **Outside-chapter text** is classified by shape only (`OutsideChapterText`: decoration, volume markers, book-info keys, links,
  end markers); prose of 40+ characters is a warning with two actions in the preview: *Gộp vào chương trước* and *Tạo chương từ vùng này*.
- **Still blocking** (all caused by the user's own edits, each named in the red banner with a jump): an included chapter without a
  number, title or text; the same number chosen twice; collisions with chapters already in the project.
- Numbering gaps were always warnings; the message now says the chapter is absent from the source and the import can proceed.
- Also fixed while testing on real books: zero-width characters (a BOM mid-file) hid headings and leaked into titles; ordering by
  volume threw when a volume identity differed from its title.
- `tools/QuickTranslator.AiEditor.Import.Probe` reports this on real files (`QT_PROBE_WARNINGS=1`, `QT_PROBE_IMPORT_ROOT=<dir>` for a
  real import into a throw-away project). Verified: 隐婚100分 2,163/2,163 chapters, 反派：我的母亲是大帝 147/147, two more books unchanged.
