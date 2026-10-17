namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record IntelligenceQualitySet(
    IReadOnlyCollection<string> Characters,
    IReadOnlyCollection<string> Aliases,
    IReadOnlyCollection<string> Relationships,
    IReadOnlyCollection<string> Glossary,
    int ValidEvidenceLinks,
    int TotalEvidenceLinks);

public sealed record PrecisionRecall(double Precision, double Recall, int TruePositive, int FalsePositive, int FalseNegative)
{
    public double FalsePositiveRate => TruePositive + FalsePositive == 0 ? 0 : FalsePositive / (double)(TruePositive + FalsePositive);
    public double FalseNegativeRate => TruePositive + FalseNegative == 0 ? 0 : FalseNegative / (double)(TruePositive + FalseNegative);
}

public sealed record IntelligenceQualityReport(PrecisionRecall Characters, PrecisionRecall Aliases,
    PrecisionRecall Relationships, PrecisionRecall Glossary, double EvidenceValidationAccuracy);

public static class IntelligenceQualityEvaluator
{
    public static IntelligenceQualityReport Evaluate(IntelligenceQualitySet expected, IntelligenceQualitySet actual) => new(
        Compare(expected.Characters, actual.Characters), Compare(expected.Aliases, actual.Aliases),
        Compare(expected.Relationships, actual.Relationships), Compare(expected.Glossary, actual.Glossary),
        actual.TotalEvidenceLinks == 0 ? 1 : actual.ValidEvidenceLinks / (double)actual.TotalEvidenceLinks);

    private static PrecisionRecall Compare(IReadOnlyCollection<string> expected, IReadOnlyCollection<string> actual)
    {
        HashSet<string> truth = expected.ToHashSet(StringComparer.Ordinal), found = actual.ToHashSet(StringComparer.Ordinal);
        int tp = found.Count(truth.Contains), fp = found.Count - tp, fn = truth.Count - tp;
        double precision = found.Count == 0 ? 1 : tp / (double)found.Count;
        double recall = truth.Count == 0 ? 1 : tp / (double)truth.Count;
        return new(precision, recall, tp, fp, fn);
    }
}
