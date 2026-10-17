namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>
/// Prepares a deliberate fresh survey pass. This is not Resume: a dated snapshot of
/// the previous state must succeed before changing any persisted review data.
/// Confirmed knowledge and previously committed Project Resources are untouched.
/// </summary>
public static class BookIntelligenceResurveyService
{
    public static bool HasUnanalyzedEvidence(BookIntelligenceState state, IntelligenceScanMode mode) =>
        state.Evidence.Any(x => x.IsCurrent && !x.AiAnalyzed && !x.AiBlocked &&
            (mode == IntelligenceScanMode.FullSemantic ||
             x.Priority is EvidencePriority.High or EvidencePriority.Medium ||
             x.IsAuditSample || x.AuditExpanded));

    public static async Task<(BookIntelligenceState State, string BackupPath)> BeginFreshAsync(
        BookIntelligenceState state, BookIntelligenceStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(store);
        if (state.PendingResourceCommits.Count != 0)
            throw new InvalidOperationException(
                "Có giao dịch lưu nhân vật/quan hệ chưa đối soát. Đóng và mở lại cửa sổ khảo sát trước khi khảo sát lại.");

        // Refuse to reset if the on-disk state is newer than the state shown in UI.
        BookIntelligenceState current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision != state.Revision)
            throw new InvalidOperationException(
                "Dữ liệu khảo sát đã thay đổi. Đóng và mở lại cửa sổ trước khi khảo sát lại.");

        // Snapshot lives alongside the state (not in a cloud prompt or repository).
        string folder = Path.Combine(Path.GetDirectoryName(store.PathName)!,
            "ai-book-intelligence-snapshots");
        Directory.CreateDirectory(folder);
        string backup = Path.Combine(folder,
            "survey-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") +
            "-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        if (File.Exists(store.PathName))
            File.Copy(store.PathName, backup, overwrite: false);
        else
        {
            // First run: save a recoverable snapshot even when no state file exists yet.
            await File.WriteAllTextAsync(backup,
                System.Text.Json.JsonSerializer.Serialize(current,
                    new System.Text.Json.JsonSerializerOptions(
                        QuickTranslator.AiEditor.Core.Persistence.DocumentJson.Options) { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);
        }

        // Work on a clone. Failed SaveAsync leaves the current review state intact.
        BookIntelligenceState fresh = BookIntelligenceStore.Clone(state);
        fresh.CompletedAnalysisKeys.Clear();
        fresh.NextEvidenceIndex = 0;
        fresh.LastError = "";
        fresh.Metrics = new IntelligenceAnalysisMetrics();
        foreach (CorpusEvidence evidence in fresh.Evidence.Where(x => x.IsCurrent))
        {
            evidence.AiAnalyzed = false;
            evidence.AiBlocked = false;
            evidence.AiBlockReason = "";
            evidence.ImportantFinding = false;
            evidence.AuditExpanded = false;
        }

        // Keep user-approved items, including character merges. Reviewable, rejected,
        // and auto-merged AI-only suggestions are in the snapshot and leave the live queue.
        fresh.Characters.RemoveAll(x => x.Status is not (
            IntelligenceReviewStatus.Accepted or IntelligenceReviewStatus.Merged));
        fresh.Relationships.RemoveAll(x => x.Status != IntelligenceReviewStatus.Accepted);
        fresh.Glossary.RemoveAll(x => x.Status != IntelligenceReviewStatus.Accepted);

        // Remove only unapproved AI graph facts. Accepted/confirmed relationships,
        // addressing history, and derived facts must survive.
        HashSet<Guid> trustedRelationIds = fresh.RelationshipGraph
            .Where(x => x.Status is KnowledgeRelationshipStatus.Confirmed or KnowledgeRelationshipStatus.Accepted)
            .Select(x => x.Id).ToHashSet();
        foreach (DerivedRelationship derived in fresh.DerivedRelationships
            .Where(x => x.Status == KnowledgeRelationshipStatus.Accepted))
            foreach (Guid id in derived.InputRelationIds) trustedRelationIds.Add(id);
        fresh.RelationshipGraph.RemoveAll(x =>
            !trustedRelationIds.Contains(x.Id) && x.DiscoveryMethod == RelationshipDiscoveryMethod.DirectAi);
        fresh.DerivedRelationships.RemoveAll(x => x.Status != KnowledgeRelationshipStatus.Accepted);

        HashSet<Guid> protectedCandidateIds = fresh.RelationshipGraph
            .SelectMany(x => new[] { x.SubjectCharacterId, x.TargetCharacterId })
            .Concat(fresh.Characters.Select(x => x.CandidateIdentityId))
            .ToHashSet();
        fresh.CandidateIdentities.RemoveAll(x =>
            x.MergeHistory.Count == 0 &&
            x.Status != CandidateIdentityStatus.ConfirmedIdentityMerge &&
            !protectedCandidateIds.Contains(x.Id));

        fresh = await store.SaveAsync(fresh, cancellationToken).ConfigureAwait(false);
        return (fresh, backup);
    }
}
