using QuickTranslator.AiEditor.Core.Models;
using System.Text.Json;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed record LlmCompletion(string Text, bool? Done, string? DoneReason, GenerationMetrics Metrics);

public interface ILlmClient
{
    Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default);
}

public interface IJsonSchemaLlmClient : ILlmClient
{
    Task<LlmCompletion> TranslateStructuredAsync(string prompt, ModelOptions options,
        JsonElement responseSchema, CancellationToken cancellationToken = default);
}

public enum LlmErrorKind { Connection, Timeout, Http, InvalidResponse }

public sealed class LlmRequestException(LlmErrorKind kind, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public LlmErrorKind Kind { get; } = kind;
}
