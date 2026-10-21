using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SemanticTransportTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<Uri> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls.Add(request.RequestUri!); return Task.FromResult(response(request)); }
    }
    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    [Fact]
    public async Task Gemini_refusal_and_429_are_single_model_no_fallback_and_no_secret_persistence()
    {
        foreach (bool blocked in new[] { true, false })
        {
            var handler = new Handler(_ => blocked ? Json("{\"promptFeedback\":{\"blockReason\":\"SAFETY\"}}") : new(HttpStatusCode.TooManyRequests));
            var options = new ModelOptions { Model = "gemini-3.8-flash", ContextSize = 32768 };
            using var client = SemanticSurveyBackend.Gemini("TEST-FAKE-NOT-A-KEY", Guid.NewGuid(), options, new(5), handler);
            if (blocked) await Assert.ThrowsAsync<GeminiSurveyBlockedException>(() => client.TranslateStructuredAsync("neutral mock", options, SemanticSurveyContract.ResponseSchema));
            else await Assert.ThrowsAsync<GeminiSurveyHttpException>(() => client.TranslateStructuredAsync("neutral mock", options, SemanticSurveyContract.ResponseSchema));
            Assert.Single(handler.Calls); Assert.Equal(1, client.Transport.RequestsUsed); Assert.All(handler.Calls, uri => Assert.DoesNotContain("TEST-FAKE", uri.ToString()));
        }
    }
    [Fact]
    public async Task Llama_server_request_counts_against_http_limit_and_no_request_is_sent_past_it()
    {
        var handler = new Handler(_ => Json("{\"choices\":[{\"message\":{\"content\":\"{}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}"));
        var options = new ModelOptions { Model = GpuBackendProfile.DefaultLocalModel, ContextSize = 32768 };
        using var client = SemanticSurveyBackend.LlamaServer(GpuBackendProfile.CreateDefaultLocal(), options, new(1), handler);
        await client.TranslateStructuredAsync("first", options, SemanticSurveyContract.ResponseSchema);
        await Assert.ThrowsAsync<SemanticTransportBudgetException>(() => client.TranslateStructuredAsync("source not sent", options, SemanticSurveyContract.ResponseSchema));
        Assert.Equal("/v1/chat/completions", Assert.Single(handler.Calls).AbsolutePath); Assert.Equal(1, client.Transport.RequestsUsed);
    }
    [Fact]
    public async Task Token_reservation_prevents_http_before_network_and_model_cannot_change()
    {
        var handler = new Handler(_ => throw new InvalidOperationException("Must not send")); var options = new ModelOptions { Model = "gemini-3.6-flash", ContextSize = 32768 };
        using var client = SemanticSurveyBackend.Gemini("MOCK", Guid.NewGuid(), options, new(5, 1), handler);
        await Assert.ThrowsAsync<SemanticTransportBudgetException>(() => client.TranslateStructuredAsync("neutral", options, SemanticSurveyContract.ResponseSchema));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.TranslateStructuredAsync("neutral", options with { Model = "other" }, SemanticSurveyContract.ResponseSchema));
        Assert.Empty(handler.Calls); Assert.Equal(0, client.Transport.RequestsUsed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Multi_evidence_schema_is_sent_once_and_transient_or_network_errors_do_not_trigger_hidden_retries(bool network)
    {
        string? body = null;
        var handler = new Handler(r =>
        {
            body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            if (network) throw new HttpRequestException("Synthetic connection reset");
            return new(HttpStatusCode.ServiceUnavailable);
        });
        var options = new ModelOptions { Model = "gemini-3.8-flash", ContextSize = 65536 };
        using var client = SemanticSurveyBackend.Gemini("FAKE-ONLY", Guid.NewGuid(), options, new(5), handler);
        await Assert.ThrowsAsync<SemanticTransportFailureException>(() => client.TranslateStructuredAsync("Mock two-evidence prompt", options, SemanticSurveyContract.MultiEvidenceSchema));
        Assert.Single(handler.Calls); Assert.Equal(1, client.Transport.RequestsUsed);
        using var json = JsonDocument.Parse(body!);
        var sent = json.RootElement.GetProperty("generationConfig").GetProperty("responseJsonSchema");
        Assert.Equal(GeminiSemanticWire.Schema.GetRawText(), sent.GetRawText()); Assert.DoesNotContain("anyOf", sent.GetRawText());
        Assert.EndsWith(GeminiSemanticWire.PromptAddendum, json.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());
    }
}
