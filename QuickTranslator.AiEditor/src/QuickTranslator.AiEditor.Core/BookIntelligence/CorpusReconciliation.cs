using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public static class CorpusReconciliation
{
    public static async Task<string> BackupAsync(BookIntelligenceStore store, long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision) throw new InvalidOperationException("Survey changed; reload before reconciliation.");
        if (current.PendingResourceCommits.Count > 0) throw new InvalidOperationException("Reconcile pending resource commits before reindexing.");
        string path = Path.Combine(Path.GetDirectoryName(store.PathName)!, "ai-book-intelligence-snapshots",
            $"index-reconcile-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.json");
        await SimpleFileStorage.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(current, DocumentJson.Options),
            Decode, cancellationToken).ConfigureAwait(false);
        return path;
    }

    // Explicit rollback only. Revision guard prevents reverting intervening approvals.
    public static async Task<BookIntelligenceState> RollbackAsync(BookIntelligenceStore store, string backupPath,
        long expectedRevision, CancellationToken cancellationToken = default)
    {
        string folder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(store.PathName)!, "ai-book-intelligence-snapshots")) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(backupPath);
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("index-reconcile-", StringComparison.Ordinal))
            throw new InvalidOperationException("Not an index reconciliation snapshot.");
        var current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.Revision != expectedRevision || current.PendingResourceCommits.Count > 0)
            throw new InvalidOperationException("Survey changed; rollback refused.");
        var saved = await SimpleFileStorage.ReadRequiredAsync(path, Decode, cancellationToken).ConfigureAwait(false);
        if (saved.ProjectId != current.ProjectId) throw new InvalidDataException("Snapshot belongs to another project.");
        if (ProtectedDecisions(saved) != ProtectedDecisions(current))
            throw new InvalidOperationException("Review decisions changed after snapshot; rollback would discard approvals or rejections.");
        saved.Revision = expectedRevision;
        return await store.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
    }

    internal static void InvalidateRetiredDependencies(BookIntelligenceState state)
    {
        var current = state.Evidence.Where(x => x.IsCurrent).Select(x => x.Id).ToHashSet();
        bool Retired(IEnumerable<Guid> ids) => ids.Any() && !ids.Any(current.Contains);
        foreach (var fact in state.RelationshipGraph.Where(x => x.Status is not
            (KnowledgeRelationshipStatus.Accepted or KnowledgeRelationshipStatus.Confirmed or KnowledgeRelationshipStatus.Rejected)))
            if (Retired(fact.EvidenceIds)) fact.Status = KnowledgeRelationshipStatus.Stale;
        var staleFacts = state.RelationshipGraph.Where(x => x.Status == KnowledgeRelationshipStatus.Stale).Select(x => x.Id).ToHashSet();
        foreach (var derived in state.DerivedRelationships.Where(x => x.Status is not
            (KnowledgeRelationshipStatus.Accepted or KnowledgeRelationshipStatus.Rejected)))
            if (Retired(derived.SupportingEvidenceIds) || derived.InputRelationIds.Any(staleFacts.Contains))
            { derived.ValidityStatus = InferenceValidityStatus.Stale; derived.Status = KnowledgeRelationshipStatus.Stale; }
        state.SceneContexts.RemoveAll(x => x.EvidenceIds.Any(id => !current.Contains(id)));
        // Accepted/rejected proposals, confirmed facts and addressing rules are audit
        // decisions, never rewritten. Cache keys include evidence identity (v3), so
        // replacement evidence cannot hit old source-range-only cache entries.
    }

    private static BookIntelligenceState Decode(byte[] bytes)
    {
        var state = JsonSerializer.Deserialize<BookIntelligenceState>(bytes, DocumentJson.Options)
            ?? throw new InvalidDataException("Empty reconciliation snapshot.");
        state.Validate();
        return state;
    }

    private static string ProtectedDecisions(BookIntelligenceState state) => JsonSerializer.Serialize(new
    {
        Characters = state.Characters.Where(x => x.Status is IntelligenceReviewStatus.Accepted or IntelligenceReviewStatus.Merged or IntelligenceReviewStatus.Rejected),
        Relationships = state.Relationships.Where(x => x.Status is IntelligenceReviewStatus.Accepted or IntelligenceReviewStatus.Rejected),
        Glossary = state.Glossary.Where(x => x.Status is IntelligenceReviewStatus.Accepted or IntelligenceReviewStatus.Rejected),
        Facts = state.RelationshipGraph.Where(x => x.Status is KnowledgeRelationshipStatus.Accepted or KnowledgeRelationshipStatus.Confirmed or KnowledgeRelationshipStatus.Rejected),
        Derived = state.DerivedRelationships.Where(x => x.Status is KnowledgeRelationshipStatus.Accepted or KnowledgeRelationshipStatus.Rejected),
        Addressing = state.AddressingTimeline.Where(x => x.Approved || x.Locked)
    }, DocumentJson.Options);
}
