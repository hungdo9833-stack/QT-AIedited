using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class SemanticTransportBudgetException : Exception;

/// <summary>Bounds ALL HTTP requests, including metadata/retries, without changing existing adapters.</summary>
public sealed class SemanticBudgetHandler(SemanticSurveyBudget budget, int maximumOutputTokens, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly object _sync = new();
    public int RequestsUsed { get; private set; }
    public long ReservedTokens { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        long cost = request.Content is null ? 0 : (await request.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false)).LongLength + maximumOutputTokens;
        lock (_sync)
        {
            if (RequestsUsed >= budget.MaximumRequests || ReservedTokens + cost > budget.MaximumReservedTokens) throw new SemanticTransportBudgetException();
            RequestsUsed++; ReservedTokens += cost;
        }
        return await base.SendAsync(request, ct).ConfigureAwait(false);
    }
}

public sealed class SemanticSurveyBackend : IJsonSchemaOllamaClient, IDisposable
{
    private readonly IJsonSchemaOllamaClient _client;
    private readonly HttpClient _http;
    public SemanticBudgetHandler Transport { get; }
    public string Model { get; }
    private SemanticSurveyBackend(string model, IJsonSchemaOllamaClient client, HttpClient http, SemanticBudgetHandler transport)
    { Model = model; _client = client; _http = http; Transport = transport; }
    public static SemanticSurveyBackend Gemini(string key, Guid googleScope, ModelOptions options, SemanticSurveyBudget budget, HttpMessageHandler? handler = null)
    {
        options.Validate(); budget.Validate();
        var transport = new SemanticBudgetHandler(budget, options.MaximumOutputTokens, handler ?? new SocketsHttpHandler { AllowAutoRedirect = false });
        var http = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        // One selected model, no pool/fallback. Explicit refusals retain the existing adapter behavior.
        return new(options.Model, new GeminiSurveyClient(http, key, GeminiSurveyLimits.FromObservedFreeTier(options.Model), googleScope, routeFailuresToPool: true), http, transport);
    }
    public static SemanticSurveyBackend Ollama(OllamaBackendProfile profile, ModelOptions options, SemanticSurveyBudget budget, HttpMessageHandler? handler = null)
    {
        var copy = profile.Clone(); copy.Validate(); options.Validate(); budget.Validate();
        if (copy.Kind == OllamaBackendKind.Remote && !copy.RemoteDisclosureAccepted) throw new InvalidOperationException("Remote disclosure is not accepted.");
        var transport = new SemanticBudgetHandler(budget, options.MaximumOutputTokens, handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = copy.Kind != OllamaBackendKind.Local });
        var http = new HttpClient(transport) { Timeout = Timeout.InfiniteTimeSpan };
        return new(options.Model, new OllamaClient(http, new(copy.Endpoint), copy.Kind, copy.Kind == OllamaBackendKind.Local ? null : copy.Token), http, transport);
    }
    public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
    { if (options.Model != Model) throw new InvalidOperationException("Selected model changed; no automatic routing."); return _client.TranslateStructuredAsync(prompt, options, schema, ct); }
    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => TranslateStructuredAsync(prompt, options, SemanticSurveyContract.ResponseSchema, ct);
    public void Dispose() { (_client as IDisposable)?.Dispose(); _http.Dispose(); }
}
