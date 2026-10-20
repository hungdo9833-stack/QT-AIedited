namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record SemanticAuditedFinding(SemanticFinding Finding, bool EvidenceValid, bool CanPromote, IReadOnlyList<string> Issues);
public sealed record SemanticAudit(IReadOnlyList<SemanticAuditedFinding> Findings, IReadOnlyList<string> Issues, IReadOnlyList<Guid> UnprocessedCpuIds,
    IReadOnlyList<Guid>? ReviewedEvidenceIds = null)
{
    public bool Validated => Issues.Count == 0 && UnprocessedCpuIds.Count == 0 && Findings.All(x => x.EvidenceValid && x.Issues.Count == 0);
}
