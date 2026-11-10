using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SemanticProviderDiagnosticAllowlistTests
{
    private const string Secret = "SENTINEL_PRIVATE_KEY_AND_ORIGINAL_NEVER_PERSIST";

    [Fact]
    public void Diagnostic_allowlist_hides_unknown_legacy_text()
    {
        Assert.Equal("UnexpectedProviderFailure", SemanticProviderDiagnostics.Describe(Secret).Code);
        Assert.DoesNotContain(Secret, SemanticProviderDiagnostics.Describe(Secret).Guidance);
        Assert.Contains("không thể", SemanticProviderDiagnostics.Describe("ProviderFailure").Guidance);
    }
}
