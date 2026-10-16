# Full legacy storage removal

## Baseline and dependency audit

Baseline: branch `codex/import-no-silent-loss-hotfix`, commit
`2914b6cb966697e6907cc1ef203e9fc07198235f`, 666 Core tests and 93 WPF tests.

The former runtime graph was:

```text
WPF recovery dialogs / project switching guards
    -> ProjectSession.RecoveryReport / RecoveryInfo
    -> ProjectRepository transaction partial
    -> ProjectTransactionJournal
    -> journal.json + before/after copies + rollback/replay

ProjectRepository / ChapterFileStore / resource and settings stores
Import / title translation / queue / AI Book Intelligence
Reader preferences / presets / library / Ollama profiles
    -> RecoveryFiles
    -> checksum validation + backup selection + rescue ZIP
    -> AtomicLocalFile
    -> primary + .bak
```

The direct dependencies were classified as follows:

| Area | Former dependency |
| --- | --- |
| Project lifecycle | manifest envelope/checksum, recovery report and transaction inspection |
| Chapter management | journal-backed add/delete/restore/rename/resegmentation and revision-guarded checkpoints |
| TXT import | journal chunks, recovery blocking, rescue source archive |
| AI translation | chapter revision hash and recovery-aware checkpoint write |
| Multi-GPU queue | checksummed job envelope and journal preflight |
| Characters/pronouns | checksummed character envelope and backup restoration |
| Glossary/context/resources | recovery-aware bundle/settings stores and revision blocking |
| Presets/settings | RecoveryFiles-backed optional stores |
| Reader/editor | recovery state surfaced by WPF and preferences stores |
| Export | publication backup creation and recovery UI dependencies |
| AI Book Intelligence | checksummed recovery store and optimistic storage revision |

## Target graph

```text
Feature services / repositories
    -> model validation and feature-level conflict checks
    -> SimpleFileStorage
    -> validate proposed bytes
    -> sibling temporary file + flush + direct atomic replacement
    -> one current primary file
```

Legacy envelopes are read only by the owning model codec. Their checksum fields are
ignored when the payload is valid. The next successful save writes direct JSON. There
is no second storage engine, backup fallback, journal, replay, rescue archive, or
recovery state machine.

## Milestones

1. Introduce and test the direct primary-file primitive.
2. Move every production store and dependent module to it.
3. Remove journal/recovery infrastructure, UI, and obsolete tests/APIs.
4. Run full integration, dependency search, and disk-I/O acceptance checks.
