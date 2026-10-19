using System.Globalization;
using System.Net;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.GeminiSchema.Probe;

return await ProbeCommand.RunAsync(args, Console.In, Console.Out, () => Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY"),
    () => new SocketsHttpHandler { AllowAutoRedirect = false, Credentials = null, UseCookies = false, AutomaticDecompression = DecompressionMethods.None });

public static class ProbeCommand
{
    public static async Task<int> RunAsync(string[] args, TextReader input, TextWriter output, Func<string?> readKey, Func<HttpMessageHandler> transportFactory)
    {
        var json = new JsonSerializerOptions { WriteIndented = true };
        try
        {
            string model = "gemini-3.6-flash"; int maximumOutput = 4096; bool live = false, synthetic = false;
            var schemaMode = ProbeSchemaMode.Production; bool schemaOptionSupplied = false;
            void SelectSchema(ProbeSchemaMode selected)
            {
                if (schemaOptionSupplied && schemaMode != selected) throw new ArgumentException("ConflictingWireSchemaOptions");
                schemaMode = selected; schemaOptionSupplied = true;
            }
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--live" when !live: live = true; break;
                    case "--synthetic-survey" when !synthetic: synthetic = true; break;
                    case "--model" when i + 1 < args.Length: model = args[++i]; break;
                    case "--max-output-tokens" when i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.None, CultureInfo.InvariantCulture, out int n): maximumOutput = n; break;
                    case "--wire-omit-max-length": SelectSchema(ProbeSchemaMode.GeminiCompatibleWireSchema); break;
                    case "--wire-schema" when i + 1 < args.Length:
                        SelectSchema(args[++i] switch { "production" => ProbeSchemaMode.Production, "gemini-compatible" => ProbeSchemaMode.GeminiCompatibleWireSchema, "gemini-semantic-wire" => ProbeSchemaMode.GeminiSemanticWire, _ => throw new ArgumentException("UnknownWireSchemaMode") }); break;
                    default: await output.WriteLineAsync("InvalidArguments: --model gemini-3.6-flash|gemini-3.8-flash --max-output-tokens 4096 [--wire-omit-max-length | --wire-schema production|gemini-compatible|gemini-semantic-wire] [--synthetic-survey] [--live]"); return 2;
                }
            }
            var options = ProbeRequest.Options(model, maximumOutput);
            if (synthetic) return await SyntheticAsync(options, schemaOptionSupplied && schemaMode != ProbeSchemaMode.GeminiSemanticWire, live, input, output, readKey, transportFactory, json);
            var inspection = await ProbeRequest.InspectAsync(options, schemaMode: schemaMode);
            await output.WriteLineAsync(JsonSerializer.Serialize(new { mode = live ? "AwaitingConfirmation" : "Offline", httpRequests = 0,
                maximumHttpRequests = 1, inspection, note = schemaMode switch
                {
                    ProbeSchemaMode.Production => "No prompt, key, response or Project is exported. Schema/config only; no production contract change.",
                    ProbeSchemaMode.GeminiSemanticWire => "Production Gemini wire " + GeminiSemanticWire.Version + " exactly as SemanticSurveyBackend.Gemini sends it (schema + prompt addendum). Reply is decoded and strictly re-validated by the internal v2 contract.",
                    _ => "EXPERIMENTAL maxLength-only wire projection, not verified provider compatibility. Internal parser/schema unchanged; no prompt, key, response or Project is exported."
                } }, json));
            if (!live) return 0; // No key read and no transport constructed in offline mode.
            await output.WriteLineAsync("Wire mode = " + inspection.Comparison.Mode + "; removed constraints = " + inspection.Comparison.RemovedConstraintCount + ". Internal schema/parser unchanged.");
            await output.WriteLineAsync("One neutral POST only; no retry, redirect, fallback, Project or checkpoint access. Output-token cap = " + maximumOutput + ".");
            await output.WriteLineAsync("Type exactly: " + ProbeRequest.Confirmation + " (anything else cancels)");
            if (await input.ReadLineAsync() != ProbeRequest.Confirmation) { await output.WriteLineAsync("CancelledBeforeSend: HTTP=0"); return 0; }
            var report = await new ProbeSession(options, schemaMode).RunOnceAsync(readKey() ?? "", ProbeRequest.Confirmation, transportFactory);
            await output.WriteLineAsync(JsonSerializer.Serialize(report, json));
            return report.Code == "SchemaAcceptedNeutralResponseValid" ? 0 : 1;
        }
        catch (Exception)
        {
            // Never print exception messages, stack traces, raw command-line values or HTTP bodies.
            await output.WriteLineAsync("ProbeConfigurationOrExecutionFailure: no automatic retry; inspect safe configuration and run explicitly again."); return 2;
        }
    }

    private static async Task<int> SyntheticAsync(ModelOptions options, bool conflictingSchema, bool live, TextReader input, TextWriter output,
        Func<string?> readKey, Func<HttpMessageHandler> transportFactory, JsonSerializerOptions json)
    {
        if (conflictingSchema) throw new ArgumentException("SyntheticSurveyUsesProductionGeminiWireOnly");
        // Offline preview: in-memory plan only; no Project, key or network.
        var preview = SyntheticSurveySmoke.Plan(options, Guid.NewGuid(), Guid.NewGuid(), SyntheticSurveySmoke.Text);
        var inspection = await ProbeRequest.InspectAsync(options, schemaMode: ProbeSchemaMode.GeminiSemanticWire);
        await output.WriteLineAsync(JsonSerializer.Serialize(new { mode = live ? "AwaitingConfirmation" : "Offline", httpRequests = 0, maximumHttpRequests = 1,
            wire = GeminiSemanticWire.Version, evidenceInBatch = preview.Units.Count, internalWireVersion = preview.WireVersion,
            promptUtf8Bytes = System.Text.Encoding.UTF8.GetByteCount(preview.Prompt + GeminiSemanticWire.PromptAddendum), inspection.GenerationConfig, inspection.Schema,
            note = "Synthetic fictional chapter only; a NEW temporary Project is created under %TEMP% on --live and kept for inspection. No user Project is opened." }, json));
        if (!live) return 0;
        await output.WriteLineAsync("One POST at most through the production engine/backend; no retry, redirect or fallback. Output-token cap = " + options.MaximumOutputTokens + ".");
        await output.WriteLineAsync("Type exactly: " + SyntheticSurveySmoke.Confirmation + " (anything else cancels)");
        if (await input.ReadLineAsync() != SyntheticSurveySmoke.Confirmation) { await output.WriteLineAsync("CancelledBeforeSend: HTTP=0"); return 0; }
        string root = Path.Combine(Path.GetTempPath(), "qtstudio-gemini-smoke-" + Guid.NewGuid().ToString("N"));
        var report = await SyntheticSurveySmoke.RunOnceAsync(options, root, readKey() ?? "", SyntheticSurveySmoke.Confirmation, transportFactory);
        await output.WriteLineAsync(JsonSerializer.Serialize(report, json));
        return report.HttpStatus == 200 && report.Code is "SurveyValidated" && report.Proposals > 0 ? 0 : 1;
    }
}
