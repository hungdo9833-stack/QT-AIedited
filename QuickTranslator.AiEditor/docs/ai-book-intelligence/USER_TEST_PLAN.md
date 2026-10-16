# AI Book Intelligence — User Test Plan

Use a copied or synthetic Project. Do not begin with the only copy of a personal Project.

## Prepare the Windows build

1. Use the delivered self-contained build or build the branch in Release.
2. Copy a Project to a new test folder and open only that copy.
3. Open AI & Performance and select a survey model/profile. Keep the translation model unchanged.

## Survey and resume

1. Open the copied Project with Chinese source chapters and initially select about 10 chapters.
2. Open **Khảo sát bằng AI** and confirm the window is modeless.
3. Select **Nhanh thích ứng**, keep translation context off, choose an analyzer profile, and start.
4. Stop the analyzer or disconnect Ollama after several requests. Reopen the Project and verify completed requests are not resent and pending review items remain.
5. Run again, then choose **Ngữ nghĩa đầy đủ** and verify Low-priority regions can also be analyzed.
6. Change one source chapter in the test copy, rescan, and verify unchanged chapters remain cached.

## Review and evidence

1. Filter Character, Relationship, Glossary, and Derived Relationship rows.
2. Search by Chinese/Vietnamese text and switch Pending/Later/Accepted/Rejected status.
3. Open evidence and verify QT Studio navigates to the exact source range.
4. Edit a Character suggestion in the existing Character form and save.
5. Merge a likely alias into an existing Character; verify evidence and alias appear. Make no other edit, then test Undo. Repeat after manually editing the Character and verify unsafe Undo is refused.
6. Accept a direct relationship without addressing and verify no addressing rule is invented.
7. Accept a context-dependent term and verify it does not become a global replacement rule.
8. For a two-way addressing proposal, enter valid-from, valid-to, and reveal chapters; verify an earlier chapter does not receive the rule.

## Inference and timeline

1. Confirm two compatible biological relations and inspect the derived relation plus its input chain.
2. Reject one premise and verify the derived item becomes Stale.
3. Check an adoptive/step/sworn or ambiguous sibling case and verify no biological propagation is created.
4. Set a reveal chapter, translate an earlier chapter, and verify the later relation/addressing does not enter the prompt.

## Optional translation context

1. With the feature flag off, translate a segment and verify the prompt has no `BOOK KNOWLEDGE` block.
2. Enable the flag and translate a segment containing an accepted Character/term; inspect the prompt and verify only relevant, chapter-valid knowledge appears.
3. Start a request, then edit knowledge before the response returns. Verify the active request used its original snapshot and the next request uses the update.
4. Run a small Project queue and confirm the same rules apply to queued translation.

## Restart and current-state checks

1. Close after a reviewed resource save completes, reopen, and verify the current resource appears exactly once.
2. Change a chapter source, rescan, and verify the old evidence is retired while new offsets match the new source.
3. Confirm translations, approvals, queue jobs, and Project Manifest are unchanged by survey/review operations.

## Full-book run

1. On a copied 500-chapter Project, run Fast Adaptive first and record CPU scan time, AI time, requests, input/output tokens, retries, cache hits, audit discoveries, and knowledge-file size.
2. Review a fixed human-labelled sample for Character, Alias, Relationship, Glossary, and evidence accuracy.
3. Resume after restart, then modify one chapter and measure incremental rescan time.
4. Run Full Semantic only if time/token budget permits, using the same model, temperature, prompt/schema version, and source copy.
5. Compare measured quality and runtime; do not use another model's output as ground truth.

## Evidence to capture when something fails

- Screenshot the selected row, detail pane, evidence chapter/range, filter state, and error banner.
- Save a diagnostic copy of the Project's current `Settings/ai-book-intelligence.json` when needed.
- Record model/profile, scan mode, chapter count, the last completed count, and whether restart/resume was involved.
- For prompt issues, use the existing prompt viewer and capture the relevant `BOOK KNOWLEDGE` block without sharing private source unnecessarily.
- For Ollama failures, preserve the visible error kind/status and endpoint/profile name; do not include access tokens.

Record the selected model/profile, Project size, elapsed time, request/token metrics, false positives, false negatives, and any evidence mismatch.


