using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SemanticNamedQuotePromptTests
{
    private static int Count(string text, string value) { int n = 0; for (int i = 0; (i = text.IndexOf(value, i, StringComparison.Ordinal)) >= 0; i += value.Length) n++; return n; }

    [Fact]
    public void Single_and_multi_evidence_prompts_require_a_named_quote_for_each_identity_exactly_once()
    {
        var single = new SemanticFixture().Plan().Single();
        Assert.Equal(1, single.WireVersion); Assert.Equal(1, Count(single.Prompt, SemanticSurveyRequests.NamedQuoteRule));
        var multi = AdaptiveSemanticTests.Plan(new SemanticFixture(AdaptiveSemanticTests.Windows(2)), 2).Single();
        Assert.Equal(2, multi.WireVersion); Assert.Equal(1, Count(multi.Prompt, SemanticSurveyRequests.NamedQuoteRule));
        foreach (string required in new[] { "EventVerification", "RelationshipExtraction", "DirectionalAddressing", "exact name", "他/她/它/我/你", "Unresolved" })
            Assert.Contains(required, SemanticSurveyRequests.NamedQuoteRule, StringComparison.Ordinal);
        Assert.True(multi.Prompt.IndexOf(SemanticSurveyRequests.NamedQuoteRule, StringComparison.Ordinal) < multi.Prompt.IndexOf("CONTEXT (", StringComparison.Ordinal));
    }

    [Fact]
    public void Prompt_wording_does_not_change_cache_identity_so_surveyed_evidence_is_not_resent()
    {
        Assert.Equal("semantic-memory-1", SemanticSurveyRequests.PromptVersion);
        Assert.Equal("semantic-multi-evidence-2", SemanticAdaptiveBatching.PromptVersion);
        var s = new SemanticFixture(AdaptiveSemanticTests.Windows(2));
        var a = AdaptiveSemanticTests.Plan(s, 2).Single(); var b = AdaptiveSemanticTests.Plan(s, 2).Single();
        Assert.Equal(a.Id, b.Id); Assert.Equal(a.Units.Select(x => x.Id), b.Units.Select(x => x.Id));
    }

    [Fact]
    public async Task Gemini_request_carries_the_named_quote_rule_and_the_wire_addendum()
    {
        var r = AdaptiveSemanticTests.Plan(new SemanticFixture(AdaptiveSemanticTests.Windows(2)), 2).Single();
        string? body = null;
        var handler = new Capture(b => body = b);
        var options = new ModelOptions { Model = "gemini-3.5-flash-lite", ContextSize = 65536, MaximumOutputTokens = 8192 };
        using var client = SemanticSurveyBackend.Gemini("FAKE-ONLY", Guid.NewGuid(), options, new(1), handler);
        await Assert.ThrowsAsync<SemanticTransportFailureException>(() => client.TranslateStructuredAsync(r.Prompt, options, SemanticSurveyContract.SchemaFor(r.WireVersion)));
        using var json = JsonDocument.Parse(body!);
        string sent = json.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        Assert.Contains(SemanticSurveyRequests.NamedQuoteRule, sent, StringComparison.Ordinal);
        Assert.EndsWith(GeminiSemanticWire.PromptAddendum, sent, StringComparison.Ordinal);
    }

    private sealed class Capture(Action<string> onBody) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onBody(await request.Content!.ReadAsStringAsync(ct));
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }
}
