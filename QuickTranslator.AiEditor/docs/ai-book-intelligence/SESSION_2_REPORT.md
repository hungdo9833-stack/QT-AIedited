# AI Book Intelligence — Session 2 Report

## Outcome

Session 2 delivers the working path: full-source CPU index -> adaptive evidence plan -> evidence-bound AI proposals -> entity/relationship knowledge -> conservative kinship inference -> modeless user review -> revision-guarded safe save -> optional immutable translation context.

The analyzer remains user-triggered. It does not edit source, translations, approvals, manifests, queues, or locked resource rules.

## Changes by requirement

### Adaptive analysis

- Every active chapter is covered by exact, non-overlapping UTF-16 windows.
- Fast Adaptive analyzes High/Medium plus deterministic Low audit samples; Full Semantic analyzes all windows.
- Important Low-audit findings expand the same semantic group in the chapter.
- Stable cache keys depend on source/model/schema/relevant context rather than every unrelated resource revision.
- Requests are bounded to eight windows and a context-size-derived character budget.
- Ollama transport failures use bounded retry; malformed structured output is retried once.

### Entity and graph knowledge

- Candidate entities use stable IDs and accumulate alias/evidence history.
- Similar spellings never auto-merge. Exact alias/name matches become review proposals.
- Direct relationships use structured types and retain evidence, origin, source chapter, reveal chapter, and review status.
- Kinship inference is dependency tracked and limited to safe biological rules at depth two.
- Stale and conflicting derivations remain visible and cannot enter translation context.

### Timeline, scene, and prompt use

- Reveal/validity ranges block knowledge from future chapters.
- Scene snapshots are chapter-local and segment-ordered; ambiguous dialogue remains ambiguous.
- Accepted global Glossary, confirmed relevant Characters, revealed relationships, approved addressing, and scene status form a bounded immutable prompt supplement.
- The Project flag is default-off. Editor and translation queue use the existing pipelines and only receive the supplement when enabled.

### Review and persistence

- Review supports type/status/search filtering, evidence count/origin, exact evidence navigation, and derived inference chains.
- Character editing reuses the current Character form.
- Multi-selection supports safe defer/reject. Acceptance remains item-specific.
- Commit markers reconcile the knowledge file with Project Resources after interruption.
- Context-dependent/addressing terms stay in the knowledge base instead of becoming unsafe global replacements.

## Regression and build

- Core: 650 passed, 0 failed, 0 skipped.
- WPF: 93 passed, 0 failed, 0 skipped.
- Total: 743 passed.
- Release solution build: 0 errors, 0 warnings.
- Synthetic temporary Projects only; no personal Project was opened.

## Files changed

Core implementation:

- `src/QuickTranslator.AiEditor.Core/BookIntelligence/AdaptiveBookIntelligenceAnalyzer.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/BookIntelligenceModels.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/BookIntelligenceKnowledgeModels.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/BookIntelligenceMigration.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/BookIntelligenceStore.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/BookIntelligenceReviewService.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/BookIntelligenceCompactor.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/EntityResolutionService.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/IntelligenceQualityMetrics.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/KinshipInferenceEngine.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/ProjectCorpusIndexer.cs`
- `src/QuickTranslator.AiEditor.Core/BookIntelligence/SceneContextService.cs`
- `src/QuickTranslator.AiEditor.Core/Projects/ProjectModels.cs`
- `src/QuickTranslator.AiEditor.Core/Projects/ProjectTranslationCoordinator.cs`
- `src/QuickTranslator.AiEditor.Core/Projects/ProjectTranslationJob.cs`
- `src/QuickTranslator.AiEditor.Core/Translation/TranslationService.cs`
- `src/QuickTranslator.AiEditor.Core/Translation/TranslationService.Editing.cs`

WPF integration:

- `src/QuickTranslator.AiEditor.Wpf/BookIntelligenceReviewWindow.cs`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.BookIntelligence.cs`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.ProjectTranslation.cs`
- `src/QuickTranslator.AiEditor.Wpf/MainWindow.Projects.cs`

Tests, benchmark, solution, and documentation:

- `tests/QuickTranslator.AiEditor.Tests/BookIntelligenceTests.cs`
- `tests/QuickTranslator.AiEditor.Tests/BookIntelligenceSession2Tests.cs`
- `tests/QuickTranslator.AiEditor.Wpf.Tests/BookIntelligenceUiTests.cs`
- `tools/QuickTranslator.AiEditor.BookIntelligence.Benchmark/*`
- `QuickTranslator.AiEditor.slnx`
- `docs/ai-book-intelligence/*`

## Architecture decisions and migration

- Knowledge Graph remains auxiliary and separate from `CharacterRelationship`, which remains the translation-rule store.
- Relationship fact, inferred relation, and addressing period are distinct records.
- Derived facts require confirmed/accepted premises, maximum depth two, and explicit review.
- Translation integration uses a provider adapter and Project feature flag instead of rewriting the coordinator.
- Content schema 2 migrates schema 1 in memory and retains evidence and pending suggestions; existing recovery backup protects the pre-save file.
- The Project Manifest, chapter files, Characters, Glossary, queue, drafts, approvals, and translations receive no schema migration.

## Items not claimed complete

- No real Ollama model was invoked for acceptance or benchmark, so real model quality/cost is unverified.
- Analyzer execution is one sequential lane on the selected profile; it does not schedule analyzer calls across multiple GPU profiles.
- The review window has safe multi-defer/reject but no bulk accept.
- There is no dedicated rich relationship editor beyond the review fields and current Character relationship resources.
- Scene resolution intentionally leaves difficult coreference unresolved.
- Per-character "knows the truth at chapter" data is present in the graph model but has no dedicated review editor yet; translation context therefore uses the reviewed reveal/validity boundary.

These limits do not affect the default translation workflow because translation context remains off until explicitly enabled.
