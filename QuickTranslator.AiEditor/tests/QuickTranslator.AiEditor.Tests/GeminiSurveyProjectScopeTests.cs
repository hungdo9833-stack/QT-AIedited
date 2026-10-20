using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GeminiSurveyProjectScopeTests
{
    [Fact]
    public void Quota_identity_is_shared_by_the_actual_google_project()
    {
        Guid same = GeminiSurveyProjectScope.Resolve("secret-a", "my-google-project");
        Assert.Equal(same, GeminiSurveyProjectScope.Resolve("secret-b", "my-google-project"));
        Assert.NotEqual(same, GeminiSurveyProjectScope.Resolve("secret-a", "another-project"));
        Assert.Equal(GeminiSurveyProjectScope.Resolve("secret-a", null),
                     GeminiSurveyProjectScope.Resolve("secret-a", ""));
    }
}
