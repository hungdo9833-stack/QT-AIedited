namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public static class EntityMemoryReasoning
{
    /// <summary>Reuses the existing biological engine on time-compatible input pairs. Never approves output.</summary>
    public static void Propose(EntityMemoryState memory, IReadOnlyList<CensusSource> sources)
    {
        var query = new EntityMemoryQuery(memory, sources);
        var trusted = memory.Facts.Where(x => x.Kind == MemoryFactKind.Relationship && query.Confirmed(x.Id) &&
            query.Timeline(x.Id).EffectiveFromChapter is not null && query.Timeline(x.Id).RevealedAtChapter is not null).ToArray();
        var bySubject = trusted.ToLookup(x => x.SubjectId);
        var evidence = memory.Evidence.ToDictionary(x => x.Id); var builder = new EntityMemoryBuilder(memory);
        foreach (var parent in trusted.Where(x => x.RelationType is KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother))
            foreach (var sibling in bySubject[parent.TargetId!.Value].Where(x => x.Id != parent.Id && Compatible(parent.RelationType, x.RelationType)))
            {
                var a = query.Timeline(parent.Id); var b = query.Timeline(sibling.Id);
                if (!EntityMemoryConflicts.Overlap(a, b)) continue;
                var timeline = new MemoryTimeline(Math.Max(a.EffectiveFromChapter!.Value, b.EffectiveFromChapter!.Value),
                    a.EffectiveToChapter is null ? b.EffectiveToChapter : b.EffectiveToChapter is null ? a.EffectiveToChapter : Math.Min(a.EffectiveToChapter.Value, b.EffectiveToChapter.Value),
                    Math.Max(a.DiscoveredAtChapter ?? 1, b.DiscoveredAtChapter ?? 1), Math.Max(a.RevealedAtChapter!.Value, b.RevealedAtChapter!.Value));
                Guid[] proof = parent.EvidenceIds.Concat(sibling.EvidenceIds).Distinct().Order().ToArray();
                var legacy = new BookIntelligenceState { ProjectId = memory.ProjectId };
                foreach (var group in proof.Select(id => evidence[id]).GroupBy(x => x.ChapterId))
                    legacy.Chapters.Add(new() { ChapterId = group.Key, ChapterNumber = group.First().ChapterNumber,
                        SourceSha256 = group.First().SourceSha256, SourceLengthUtf16 = group.Max(x => x.StartUtf16 + x.LengthUtf16),
                        Evidence = group.Select(x => new CorpusEvidence { Id = x.Id, ChapterId = x.ChapterId, SourceSha256 = x.SourceSha256,
                            StartUtf16 = x.StartUtf16, LengthUtf16 = x.LengthUtf16 }).ToList() });
                foreach (var input in new[] { parent, sibling }) legacy.RelationshipGraph.Add(new() { Id = input.Id,
                    SubjectCharacterId = input.SubjectId, TargetCharacterId = input.TargetId!.Value, RelationType = input.RelationType,
                    Status = KnowledgeRelationshipStatus.Confirmed, EvidenceIds = input.EvidenceIds.ToList() });
                foreach (var derived in new KinshipInferenceEngine().Recalculate(legacy).Where(x => x.RuleId.StartsWith("K01-", StringComparison.Ordinal)))
                    builder.Fact(new() { SubjectId = derived.SubjectCharacterId, TargetId = derived.TargetCharacterId, Kind = MemoryFactKind.Relationship,
                        RelationType = derived.OutputRelation, Value = derived.OutputRelation.ToString(), Timeline = timeline, EvidenceIds = proof,
                        InputFactIds = derived.InputRelationIds.Order().ToArray(), Confidence = 0.6,
                        Provenance = new(MemoryOrigin.Rule, $"{derived.RuleId}:{parent.Id}:{sibling.Id}:{derived.SubjectCharacterId}:{derived.TargetCharacterId}",
                            derived.Explanation + " Requires human review; validity is the input-period intersection and reveal is the latest input reveal.", RuleId: derived.RuleId) });
            }
    }
    private static bool Compatible(KnowledgeRelationType parent, KnowledgeRelationType sibling) => sibling == KnowledgeRelationType.FullSibling ||
        parent == KnowledgeRelationType.BiologicalFather && sibling == KnowledgeRelationType.PaternalHalfSibling ||
        parent == KnowledgeRelationType.BiologicalMother && sibling == KnowledgeRelationType.MaternalHalfSibling;
}
