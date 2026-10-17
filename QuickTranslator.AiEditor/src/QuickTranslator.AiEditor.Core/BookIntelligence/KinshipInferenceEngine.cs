namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class KinshipInferenceEngine
{
    private static readonly HashSet<KnowledgeRelationshipStatus> Trusted =
        [KnowledgeRelationshipStatus.Confirmed, KnowledgeRelationshipStatus.Accepted];

    public IReadOnlyList<DerivedRelationship> Recalculate(BookIntelligenceState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        KnowledgeRelationship[] facts = state.RelationshipGraph.Where(x => Trusted.Contains(x.Status)).ToArray();
        var candidates = new List<DerivedRelationship>();
        foreach (KnowledgeRelationship parent in facts.Where(x => x.RelationType is
                     KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother))
        {
            foreach (KnowledgeRelationship sibling in facts.Where(x => x.SubjectCharacterId == parent.TargetCharacterId &&
                         IsCompatibleSibling(parent.RelationType, x.RelationType)))
            {
                Add(candidates, "K01-PARENT-SIBLING", [parent, sibling], parent.SubjectCharacterId,
                    sibling.TargetCharacterId, parent.RelationType,
                    $"{parent.RelationType} của một người cũng là {parent.RelationType} của người anh/chị/em có cùng nhánh huyết thống.");
                Add(candidates, "K01-CHILD-INVERSE", [parent, sibling], sibling.TargetCharacterId,
                    parent.SubjectCharacterId, KnowledgeRelationType.BiologicalChild,
                    "Quan hệ con ruột chiều ngược của kết luận cha/mẹ ruột; chưa suy ra cách xưng hô.");
            }
            foreach (KnowledgeRelationship child in facts.Where(x => x.SubjectCharacterId == parent.TargetCharacterId &&
                         x.RelationType is KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother))
            {
                Add(candidates, "K02-GRANDPARENT", [parent, child], parent.SubjectCharacterId,
                    child.TargetCharacterId,
                    parent.RelationType == KnowledgeRelationType.BiologicalFather ? KnowledgeRelationType.Grandfather : KnowledgeRelationType.Grandmother,
                    "Cha/mẹ ruột của cha/mẹ ruột là ông/bà sinh học; cách xưng hô vẫn chưa được suy ra.");
                Add(candidates, "K02-GRANDCHILD-INVERSE", [parent, child], child.TargetCharacterId,
                    parent.SubjectCharacterId, KnowledgeRelationType.Grandchild,
                    "Quan hệ cháu chiều ngược của kết luận ông/bà; chưa suy ra cách gọi cụ thể.");
            }
        }

        Guid[] validInputs = facts.Select(x => x.Id).ToArray();
        foreach (DerivedRelationship old in state.DerivedRelationships)
        {
            if (old.InputRelationIds.Any(id => !validInputs.Contains(id)))
            {
                old.ValidityStatus = InferenceValidityStatus.Stale;
                old.Status = KnowledgeRelationshipStatus.Stale;
            }
        }
        foreach (DerivedRelationship candidate in candidates)
        {
            DerivedRelationship? existing = state.DerivedRelationships.FirstOrDefault(x => x.RuleId == candidate.RuleId &&
                x.SubjectCharacterId == candidate.SubjectCharacterId && x.TargetCharacterId == candidate.TargetCharacterId &&
                x.OutputRelation == candidate.OutputRelation);
            if (existing is null) state.DerivedRelationships.Add(candidate);
            else
            {
                existing.InputRelationIds = candidate.InputRelationIds;
                existing.SupportingEvidenceIds = candidate.SupportingEvidenceIds;
                existing.Explanation = candidate.Explanation;
                existing.InferenceDepth = candidate.InferenceDepth;
                existing.ValidityStatus = candidate.ValidityStatus;
                if (existing.Status == KnowledgeRelationshipStatus.Stale)
                    existing.Status = KnowledgeRelationshipStatus.DerivedAwaitingReview;
            }
        }
        MarkConflicts(state, facts);
        state.Validate();
        return state.DerivedRelationships;
    }

    private static bool IsCompatibleSibling(KnowledgeRelationType parent, KnowledgeRelationType sibling) =>
        sibling == KnowledgeRelationType.FullSibling ||
        parent == KnowledgeRelationType.BiologicalFather && sibling == KnowledgeRelationType.PaternalHalfSibling ||
        parent == KnowledgeRelationType.BiologicalMother && sibling == KnowledgeRelationType.MaternalHalfSibling;

    private static void Add(List<DerivedRelationship> output, string ruleId, KnowledgeRelationship[] inputs,
        Guid subject, Guid target, KnowledgeRelationType relation, string explanation)
    {
        if (subject == target || output.Any(x => x.RuleId == ruleId && x.SubjectCharacterId == subject &&
            x.TargetCharacterId == target && x.OutputRelation == relation)) return;
        output.Add(new DerivedRelationship
        {
            RuleId = ruleId, InputRelationIds = inputs.Select(x => x.Id).ToList(),
            SupportingEvidenceIds = inputs.SelectMany(x => x.EvidenceIds).Distinct().ToList(),
            InferenceDepth = Math.Min(2, inputs.Max(x => x.DiscoveryMethod == RelationshipDiscoveryMethod.CpuInference ? 2 : 1)),
            SubjectCharacterId = subject, TargetCharacterId = target, OutputRelation = relation,
            ValidityStatus = InferenceValidityStatus.Valid, Explanation = explanation
        });
    }

    private static void MarkConflicts(BookIntelligenceState state, IReadOnlyCollection<KnowledgeRelationship> facts)
    {
        foreach (DerivedRelationship derived in state.DerivedRelationships.Where(x => x.ValidityStatus == InferenceValidityStatus.Valid))
        {
            bool conflict = facts.Any(x => x.SubjectCharacterId == derived.SubjectCharacterId &&
                x.TargetCharacterId == derived.TargetCharacterId && IsContradictory(x.RelationType, derived.OutputRelation));
            if (conflict)
            {
                derived.ValidityStatus = InferenceValidityStatus.Conflict;
                derived.Status = KnowledgeRelationshipStatus.Conflict;
            }
        }
    }

    private static bool IsContradictory(KnowledgeRelationType left, KnowledgeRelationType right) =>
        left != right && ((left is KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother or KnowledgeRelationType.AdoptiveParent) &&
                          (right is KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother or KnowledgeRelationType.AdoptiveParent));
}

public static class RelationshipTimelineService
{
    public static IReadOnlyList<KnowledgeRelationship> ValidRelationshipsAt(BookIntelligenceState state, int chapterNumber) =>
        state.RelationshipGraph.Where(x => x.Status is KnowledgeRelationshipStatus.Confirmed or KnowledgeRelationshipStatus.Accepted &&
            (x.ValidFromChapter is null || x.ValidFromChapter <= chapterNumber) &&
            (x.ValidToChapter is null || x.ValidToChapter >= chapterNumber) &&
            x.RevealedAtChapter is not null && x.RevealedAtChapter <= chapterNumber).ToArray();

    public static IReadOnlyList<AddressingRulePeriod> ApprovedAddressingAt(BookIntelligenceState state, int chapterNumber) =>
        state.AddressingTimeline.Where(x => x.Approved &&
            (x.ValidFromChapter is null || x.ValidFromChapter <= chapterNumber) &&
            (x.ValidToChapter is null || x.ValidToChapter >= chapterNumber) &&
            ((x.RevealedAtChapter is not null && x.RevealedAtChapter <= chapterNumber) ||
             (x.RevealedAtChapter is null && x.ValidFromChapter is not null && x.ValidFromChapter <= chapterNumber))).ToArray();
}
