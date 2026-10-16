# AI Book Intelligence — Session 2 Handoff

Session 2 is implemented on `codex/ai-book-intelligence-session2`. The authoritative outcome and limits are in `SESSION_2_REPORT.md`; architecture and manual validation are in `ARCHITECTURE.md` and `USER_TEST_PLAN.md`.

## Operational entry points

1. Open a Project and choose **Khảo sát bằng AI** from Characters/relationships.
2. Select Fast Adaptive or Full Semantic and an analyzer model/profile.
3. Start the user-triggered survey. CPU indexing checkpoints first; Ollama only receives planned evidence.
4. Filter the modeless queue, inspect exact source evidence, then accept/edit/merge/defer/reject.
5. Review direct and derived relationships separately. Enter validity/reveal chapters and two-way addressing only when confirmed.
6. Enable **Book Knowledge Context** in the survey window only for a copied/test Project first. The default remains off.

## Durable state

- Path: `Settings/ai-book-intelligence.json` plus existing recovery backup.
- Envelope schema: 1; content schema: 2.
- Schema 1 evidence and pending suggestions migrate in memory and remain auditable.
- Index checkpoints by changed chapter; analyzer checkpoints by valid request batch.
- Project Resource commits use revision guards plus a pending marker reconciled after restart.
- Translation requests receive immutable chapter/segment snapshots; no mutable cross-chapter scene memory is shared.

## Verification at handoff

- Core tests: 650 passed.
- WPF tests: 93 passed.
- Failed/skipped: 0/0.
- Release solution build: 0 errors, 0 warnings.
- Synthetic 500-chapter / 2,000,000-character planner benchmark completed; no real model was called.

## Important boundaries for subsequent work

- Do not treat derived relationships as confirmed facts or addressing rules.
- Preserve the feature flag default-off behavior.
- Keep Project Manifest/chapter/resource schemas unchanged unless a separately approved migration is designed.
- Real-model quality, multi-profile analyzer scheduling, rich per-character knowledge-time editing, and conservative scene/coreference expansion remain future work.
