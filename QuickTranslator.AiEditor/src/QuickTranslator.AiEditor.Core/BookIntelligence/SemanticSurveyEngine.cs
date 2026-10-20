namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record SemanticSurveyBudget(int MaximumRequests = 5, int MaximumReservedTokens = 150_000)
{
    public void Validate() { if (MaximumRequests is < 1 or > 1000 || MaximumReservedTokens is < 1 or > 100_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumRequests)); }
}
