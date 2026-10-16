# AI Book Intelligence — Architecture

## Data flow

1. `ProjectCorpusIndexer` loads active chapters through `ProjectRepository` and reads `OriginalText`.
2. CPU classification writes exact evidence windows to `BookIntelligenceState` through `BookIntelligenceStore`.
3. `AdaptiveBookIntelligenceAnalyzer` plans eligible windows, calls the selected `IOllamaClient`, validates JSON/evidence, and checkpoints proposals.
4. `EntityResolutionService` and the direct relationship graph aggregate identities, aliases, evidence, and facts.
5. `KinshipInferenceEngine` recalculates dependency-backed derived relations.
6. `BookIntelligenceReviewWindow` shows proposals/evidence and delegates accepted writes to `BookIntelligenceReviewService`.
7. `BookKnowledgeContextProvider` snapshots accepted, chapter-valid knowledge for `TranslationService` when the feature flag is enabled.

## Key files

- `BookIntelligenceModels.cs`: state schema, evidence and review proposals.
- `BookIntelligenceKnowledgeModels.cs`: adaptive metrics, identity, graph, timeline, scene, commit, and prompt snapshot models.
- `BookIntelligenceMigration.cs`: content schema 1 -> 2 migration.
- `BookIntelligenceStore.cs`: checksummed atomic persistence and optimistic revision guard.
- `ProjectCorpusIndexer.cs`: exact windows, CPU signals, incremental rescan, deterministic audit selection.
- `AdaptiveBookIntelligenceAnalyzer.cs`: planning, prompt, cache, structured validation, retry, checkpoint, audit expansion.
- `EntityResolutionService.cs`: stable candidate resolution and direct graph aggregation.
- `KinshipInferenceEngine.cs`: conservative derived relationship rules and timeline filtering.
- `SceneContextService.cs`: scene snapshots and immutable `IBookKnowledgeContextProvider`.
- `BookIntelligenceReviewService.cs`: explicit acceptance, merge/undo, commit markers, reconciliation.
- `BookIntelligenceReviewWindow.cs`: modeless review UI.
- `MainWindow.BookIntelligence.cs`: survey entry point and state refresh.
- `MainWindow.Projects.cs` / `MainWindow.ProjectTranslation.cs`: feature flag and editor/queue provider wiring.

## Safety boundaries

- Knowledge state is auxiliary; Project Manifest and chapter schemas are unchanged.
- `AutoApplyVerified=false`; no analysis result writes Project Resources by itself.
- Existing Project Resources revision checks prevent lost updates.
- Locked Character/Relationship rules are not overwritten.
- Context-dependent terms are excluded from global replacement rules.
- Translation snapshots are immutable per request.
- Source hashes and UTF-16 ranges bind evidence to exact source.
- Retired evidence is retained for audit unless explicit safe compaction finds it unreferenced.

## Schema migration and rollback

Loading schema 1 creates schema 2 defaults and stable identities in memory. Saving uses the existing recovery backup mechanism. Older application builds do not understand content schema 2; rollback therefore means restoring the pre-save backup or using the Session 1 Git build with a Session 1 knowledge file. Project/chapter/resource files remain compatible because their schemas were not changed.

## Translation prompt contract

The provider receives ChapterId, chapter number, SegmentId, and source. It returns a bounded text block plus immutable provenance. Only confirmed/reviewed and chapter-valid data is included. If the provider is absent or the Project flag is false, `TranslationService` builds the legacy prompt exactly as before.
