using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.GeminiSchema.Probe;

public sealed record ProbeReport(string Model, int HttpRequests, int? HttpStatus, string Code, ProviderDiagnostic? Provider,
    bool ProviderAcceptedRequest, bool? ResponseContractValid, long ElapsedMilliseconds, int? InputTokens, int? OutputTokensIncludingThinking)
{
    public string WireSchemaMode { get; init; } = nameof(ProbeSchemaMode.Production);
    public string? WireSchemaSha256 { get; init; }
}

/// <summary>One explicit approval, one POST at most; never accesses a Project or writes a journal.</summary>
public sealed class ProbeSession(ModelOptions options, ProbeSchemaMode schemaMode = ProbeSchemaMode.Production)
{
    private int _approvalUsed;
    public async Task<ProbeReport> RunOnceAsync(string apiKey, string confirmation, Func<HttpMessageHandler> transportFactory, CancellationToken ct = default)
    {
        ProbeReport Denied(string code) => new(options.Model, 0, null, code, null, false, null, 0, null, null) { WireSchemaMode = schemaMode.ToString() };
        if (confirmation != ProbeRequest.Confirmation) return Denied("ConfirmationRequired");
        if (Interlocked.Exchange(ref _approvalUsed, 1) != 0) return Denied("ApprovalAlreadyUsed");
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains('\r') || apiKey.Contains('\n')) return Denied("ApiKeyMissingOrInvalid");
        var watch = new Stopwatch(); SinglePost? transport = null; bool? valid = null; int? input = null, output = null;
        string code; string? schemaHash = null; var stage = SemanticFailureStage.Provider;
        try
        {
            ProbeRequest.Options(options.Model, options.MaximumOutputTokens).Validate(); options.Validate();
            var inspection = await ProbeRequest.InspectAsync(options, ct, schemaMode); schemaHash = inspection.Schema.Sha256;
            using var one = transport = new SinglePost(inspection, transportFactory());
            using var http = new HttpClient(one) { Timeout = Timeout.InfiniteTimeSpan };
            using var client = new GeminiSurveyClient(http, apiKey, new(100, 1_000_000, 100, 1), Guid.NewGuid(), routeFailuresToPool: true);
            watch.Start();
            var completion = await client.TranslateStructuredAsync(ProbeRequest.PromptFor(schemaMode), options, inspection.GenerationConfig.GetProperty("responseJsonSchema"), ct);
            input = completion.Metrics.InputTokens; output = completion.Metrics.OutputTokens;
            stage = SemanticFailureStage.Parse; valid = false;
            var response = SemanticSurveyContract.Parse(schemaMode == ProbeSchemaMode.GeminiSemanticWire ? GeminiSemanticWire.Decode(completion.Text, 2) : completion.Text);
            if (response.SchemaVersion != 2 || response.ProjectId != ProbeRequest.SyntheticProject || response.ChapterId != ProbeRequest.SyntheticChapter ||
                response.ReviewedEvidenceIds.Count != 0 || response.Findings.Count != 0) code = "ProbeResponseScopeMismatch";
            else { valid = true; code = "SchemaAcceptedNeutralResponseValid"; }
        }
        catch (ProbeAbort ex) { code = ex.Diagnostic.Code; }
        catch (GeminiSurveyBlockedException) { code = "ProviderRefusal"; }
        catch (OperationCanceledException) { code = ct.IsCancellationRequested ? "Cancelled" : "Timeout"; }
        catch (Exception ex) { code = SemanticProviderDiagnostics.Classify(ex, stage).Code; }
        watch.Stop(); int? status = transport?.HttpStatus;
        return new(options.Model, transport?.Requests ?? 0, status, code, transport?.Diagnostic,
            status is >= 200 and <= 299, valid, watch.ElapsedMilliseconds, input, output) { WireSchemaMode = schemaMode.ToString(), WireSchemaSha256 = schemaHash };
    }

    private sealed class ProbeAbort(ProviderDiagnostic diagnostic) : Exception("ControlledProbeAbort")
    { public ProviderDiagnostic Diagnostic { get; } = diagnostic; }

    private sealed class SinglePost(ProbeInspection inspection, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        private int _requests;
        public int Requests => Volatile.Read(ref _requests);
        public int? HttpStatus { get; private set; }
        public ProviderDiagnostic? Diagnostic { get; private set; }
        private ProbeAbort Abort(string code) => new(Diagnostic = new(code, "", [], []));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Pin the endpoint and exact neutral request produced by the real adapter.
            // Redirects are disabled by the CLI's SocketsHttpHandler; 3xx also aborts here.
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != inspection.Endpoint || request.Content is null) throw Abort("UnexpectedProbeRequest");
            using (var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)))
                if (!ProbeRequest.Matches(body.RootElement, inspection)) throw Abort("ProbeRequestDrift");
            if (Interlocked.CompareExchange(ref _requests, 1, 0) != 0) throw Abort("SingleHttpBudgetExceeded");
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, ct); }
            catch (HttpRequestException) { throw Abort("NetworkFailure"); } // Do not trigger the shared adapter's retries.
            HttpStatus = (int)response.StatusCode;
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.BadRequest)
                    {
                        byte[]? bytes = await ReadBounded(response.Content, 32_768, ct);
                        Diagnostic = bytes is null ? new("Http400BodyOversized", "UNKNOWN", [], []) : ProviderDiagnostic.Classify400(bytes, inspection.GenerationConfig.GetProperty("responseJsonSchema"));
                        throw new ProbeAbort(Diagnostic);
                    }
                    throw Abort("Http" + (int)response.StatusCode);
                }
                byte[]? success = await ReadBounded(response.Content, 262_144, ct);
                if (success is null) throw Abort("ProviderResponseOversized");
                var replacement = new ByteArrayContent(success); replacement.Headers.ContentType = response.Content.Headers.ContentType;
                response.Content.Dispose(); response.Content = replacement; return response;
            }
            catch (IOException) { response.Dispose(); throw Abort("ResponseReadFailure"); }
            catch (HttpRequestException) { response.Dispose(); throw Abort("ResponseReadFailure"); }
            catch { response.Dispose(); throw; }
        }
        private static async Task<byte[]?> ReadBounded(HttpContent content, int maximum, CancellationToken ct)
        {
            using var stream = await content.ReadAsStreamAsync(ct); using var bytes = new MemoryStream(); byte[] buffer = new byte[4096];
            while (bytes.Length <= maximum)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, maximum + 1 - bytes.Length)), ct);
                if (read == 0) return bytes.ToArray(); await bytes.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            return null;
        }
    }
}
