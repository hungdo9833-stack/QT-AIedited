namespace QuickTranslator.AiEditor.Core.BookIntelligence;

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
