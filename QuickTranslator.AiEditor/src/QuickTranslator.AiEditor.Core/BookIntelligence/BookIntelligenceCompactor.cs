namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public static class BookIntelligenceCompactor
{
    public static int RemoveUnreferencedRetiredEvidence(BookIntelligenceState state)
    {
        HashSet<Guid> referenced = state.Characters.SelectMany(x => x.EvidenceIds)
            .Concat(state.Relationships.SelectMany(x => x.EvidenceIds))
            .Concat(state.Glossary.SelectMany(x => x.EvidenceIds))
            .Concat(state.CandidateIdentities.SelectMany(x => x.EvidenceHistory))
            .Concat(state.RelationshipGraph.SelectMany(x => x.EvidenceIds))
            .Concat(state.DerivedRelationships.SelectMany(x => x.SupportingEvidenceIds))
            .Concat(state.SceneContexts.SelectMany(x => x.EvidenceIds)).ToHashSet();
        int removed = 0;
        foreach (CorpusChapterIndex chapter in state.Chapters)
            removed += chapter.Evidence.RemoveAll(x => !x.IsCurrent && !referenced.Contains(x.Id));
        return removed;
    }
}
