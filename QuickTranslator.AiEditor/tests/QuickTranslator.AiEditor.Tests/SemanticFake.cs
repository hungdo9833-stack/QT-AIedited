using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

internal sealed class SemanticFake(Func<string, CancellationToken, Task<OllamaCompletion>> respond) : IJsonSchemaOllamaClient
{
    public List<string> Prompts { get; } = [];
    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => TranslateStructuredAsync(prompt, options, default, ct);
    public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
    { Prompts.Add(prompt); return respond(prompt, ct); }
    public static OllamaCompletion Completion(SemanticResponse response) => new(SemanticFixture.Json(response), true, "stop", new() { InputTokens = 100, OutputTokens = 30 });
}
