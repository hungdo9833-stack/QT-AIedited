using QuickTranslator.AiEditor.Core.Models;
using System.Text.Json;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed record OllamaCompletion(string Text, bool? Done, string? DoneReason, GenerationMetrics Metrics);

public interface IOllamaClient
{
    Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default);
}

public interface IJsonSchemaOllamaClient : IOllamaClient
{
    Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
        JsonElement responseSchema, CancellationToken cancellationToken = default);
}

public enum OllamaErrorKind { Connection, Timeout, Http, InvalidResponse }

public sealed class OllamaRequestException(OllamaErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public OllamaErrorKind Kind { get; } = kind;
}
