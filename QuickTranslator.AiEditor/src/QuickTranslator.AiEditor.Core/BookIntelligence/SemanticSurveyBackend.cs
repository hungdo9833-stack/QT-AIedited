using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class SemanticTransportBudgetException : Exception;
public sealed class SemanticTransportFailureException(System.Net.HttpStatusCode? statusCode = null, bool schemaRejected = false) : Exception("Semantic transport failure.")
{
    public System.Net.HttpStatusCode? StatusCode { get; } = statusCode;
    public bool SchemaRejected { get; } = schemaRejected;
}

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
        HttpResponseMessage response;
        try { response = await base.SendAsync(request, ct).ConfigureAwait(false); }
        // The shared Gemini adapter retries HttpRequestException/5xx. Semantic survey
        // requires an explicit Resume instead; do not modify the shared adapter.
        catch (HttpRequestException) { throw new SemanticTransportFailureException(); }
        if ((int)response.StatusCode >= 500 || response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
        { var status = response.StatusCode; response.Dispose(); throw new SemanticTransportFailureException(status); }
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && await SchemaRejectedAsync(response, ct).ConfigureAwait(false))
        { response.Dispose(); throw new SemanticTransportFailureException(System.Net.HttpStatusCode.BadRequest, schemaRejected: true); }
        return response;
    }

    private static async Task<bool> SchemaRejectedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        // Inspect a bounded error envelope in memory only. Persist neither body, field
        // paths nor messages. Oversized/malformed/non-schema errors remain plain HTTP 400.
        try
        {
            using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            byte[] buffer = new byte[16_385]; int length = 0;
            while (length < buffer.Length) { int read = await input.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false); if (read == 0) break; length += read; }
            if (length == buffer.Length) return false;
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, length), new() { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return false;
            if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                foreach (var detail in details.EnumerateArray())
                    if (detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("fieldViolations", out var violations) && violations.ValueKind == JsonValueKind.Array)
                        foreach (var violation in violations.EnumerateArray())
                            if (violation.ValueKind == JsonValueKind.Object && violation.TryGetProperty("field", out var field) && field.ValueKind == JsonValueKind.String &&
                                new[] { "generation_config.response_json_schema", "generationConfig.responseJsonSchema", "generation_config.response_schema", "generationConfig.responseSchema" }
                                    .Any(prefix => field.GetString() == prefix || field.GetString()!.StartsWith(prefix + ".", StringComparison.Ordinal) || field.GetString()!.StartsWith(prefix + "[", StringComparison.Ordinal))) return true;
            if (!error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String) return false;
            string text = message.GetString()!;
            return new[] { "response schema contains too many states", "response schema is too complex" }.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase)) ||
                new[] { "response_json_schema", "responseJsonSchema" }.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase)) &&
                new[] { "invalid", "unsupported", "unknown", "too complex" }.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (HttpRequestException) { return false; }
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
