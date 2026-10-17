using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

internal sealed class MemoryStore : IChapterStore
{
    public ChapterDocument? LastSaved { get; private set; }
    public Func<ChapterDocument, CancellationToken, Task>? BeforeSave { get; set; }
    public int Saves { get; private set; }
    public string GetPath(Guid chapterId) => $"memory/{chapterId:D}.qtaie.json";

    public async Task SaveAsync(ChapterDocument chapter, CancellationToken cancellationToken = default)
    {
        if (BeforeSave is not null) await BeforeSave(chapter, cancellationToken);
        LastSaved = DocumentJson.Clone(chapter);
        Saves++;
    }

    public Task<ChapterLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChapterLoadResult(DocumentJson.Clone(LastSaved!), path));
}

internal sealed class FakeClient : IOllamaClient
{
    public int Calls { get; private set; }
    public List<string> Prompts { get; } = [];
    public Func<int, CancellationToken, Task<OllamaCompletion>> Respond { get; set; } =
        (_, _) => Task.FromResult(Completion("Một bản dịch thử nghiệm."));

    public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
    {
        Calls++;
        Prompts.Add(prompt);
        return Respond(Calls, cancellationToken);
    }

    public static OllamaCompletion Completion(string text) => new(text, true, "stop", new() { InputTokens = 200, OutputTokens = 80 });
}
