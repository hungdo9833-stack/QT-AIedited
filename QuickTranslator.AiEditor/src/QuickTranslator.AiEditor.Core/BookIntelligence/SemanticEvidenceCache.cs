using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

internal sealed class SemanticEvidenceCache
{
    private readonly HashSet<Guid> _done = [];
    private readonly Dictionary<Guid, List<(int Start, int Length)>> _blocks = [];
    internal SemanticEvidenceCache(SemanticSurveyState state, BookIntelligenceState legacy)
    {
        var statuses = state.Attempts.GroupBy(x => x.BatchId).ToDictionary(x => x.Key, x => x.Last());
        var proof = state.Evidence.ToDictionary(x => x.Id);
        foreach (var result in state.EvidenceResults.Where(x => x.Status is SemanticBatchStatus.Analyzed or SemanticBatchStatus.Validated)) _done.Add(result.CacheKey);
        foreach (var batch in state.Batches)
        {
            if (batch.EvidenceKeys is null && statuses.TryGetValue(batch.Id, out var attempt) && attempt.Status is SemanticBatchStatus.Analyzed or SemanticBatchStatus.Validated &&
                !new[] { "IncompleteEvidenceCoverage", "ResponseScopeMismatch", "StaleOrForeignEvidence" }.Any(x => attempt.ErrorCode.Contains(x, StringComparison.Ordinal))) _done.Add(batch.Id);
        }
        var refused = state.Attempts.Where(x => x.Status == SemanticBatchStatus.Blocked).Select(x => x.BatchId).ToHashSet();
        foreach (var e in state.Batches.Where(x => refused.Contains(x.Id)).SelectMany(x => x.Keys).Select(x => proof[x.EvidenceId])) Block(e.ChapterId, e.StartUtf16, e.LengthUtf16);
        foreach (var e in legacy.Evidence.Where(x => x.AiBlocked)) Block(e.ChapterId, e.StartUtf16, e.LengthUtf16);
    }
    private void Block(Guid chapter, int start, int length)
    {
        if (!_blocks.TryGetValue(chapter, out var list)) _blocks[chapter] = list = [];
        list.Add((start, length));
    }
    internal bool Blocked(SemanticSurveyRequest unit) => _blocks.TryGetValue(unit.Evidence.ChapterId, out var ranges) && ranges.Any(x =>
        x.Start < (long)unit.Evidence.StartUtf16 + unit.Evidence.LengthUtf16 && (long)x.Start + x.Length > unit.Evidence.StartUtf16);
    internal bool Done(SemanticSurveyRequest unit) => _done.Contains(unit.Id);
    internal IReadOnlyList<SemanticSurveyRequest> Ready(IReadOnlyList<SemanticSurveyRequest> plan)
    {
        var units = plan.SelectMany(x => x.Units).Where(x => !Done(x) && !Blocked(x)).ToArray();
        if (units.Length == 0) return [];
        // Settings/options are already fingerprinted by the approved plan. Never repack
        // under another model or increase the approved evidence/token ceiling.
        return units.GroupBy(x => new { x.PlanningOptions, x.MaximumEvidencePerBatch, x.MaximumBatchReserve })
            .SelectMany(g => SemanticAdaptiveBatching.Pack(g, g.Key.PlanningOptions ?? new ModelOptions(), new(g.Key.MaximumEvidencePerBatch, g.Key.MaximumBatchReserve)))
            .OrderBy(x => x.ChapterNumber).ThenBy(x => x.Evidence.StartUtf16).ToArray();
    }
}
