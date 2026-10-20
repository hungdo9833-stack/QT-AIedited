using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

/// <summary>The built-in "Gemini Flash Pool" survey backend offered beside the user's Ollama profiles.</summary>
internal static class GeminiSurveyProfile
{
    /// <summary>Stable id: a saved survey choice keeps pointing at Gemini across restarts.</summary>
    internal static Guid Id { get; } = Guid.Parse("c528d3a8-147a-4b7d-8471-e4cbe39d6f38");

    internal static OllamaBackendProfile Create()
    {
        const string model = "gemini-3.8-flash";
        return new OllamaBackendProfile
        {
            Id = Id,
            Name = "Gemini Flash Pool",
            Kind = OllamaBackendKind.Remote,
            Model = model,
            Runtime = AiRuntimeProfile.CreateDefault(model),
            RemoteDisclosureAccepted = true
        };
    }
}
