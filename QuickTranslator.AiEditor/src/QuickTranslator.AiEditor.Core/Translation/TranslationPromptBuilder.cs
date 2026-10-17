using System.Text;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed record GlossaryEntry(string Chinese, string Vietnamese);

public interface IGlossaryProvider
{
    ValueTask<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(string sourceText, CancellationToken cancellationToken);
}

public interface IContextMemory
{
    ValueTask<string> GetNotesAsync(ChapterDocument chapter, Guid segmentId, CancellationToken cancellationToken);
}

public sealed class EmptyGlossaryProvider : IGlossaryProvider
{
    public ValueTask<IReadOnlyList<GlossaryEntry>> GetEntriesAsync(string sourceText, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<GlossaryEntry>>([]);
}

public sealed class EmptyContextMemory : IContextMemory
{
    public ValueTask<string> GetNotesAsync(ChapterDocument chapter, Guid segmentId, CancellationToken cancellationToken) => ValueTask.FromResult("");
}

public sealed class TranslationPromptBuilder
{
    public const string Version = "translategemma-zh-vi-v2-character-consistency";

    public string Build(string sourceText, IReadOnlyList<GlossaryEntry> glossary, string contextNotes)
        => Build(sourceText, glossary, contextNotes, PromptPreset.Default);

    public string Build(string sourceText, IReadOnlyList<GlossaryEntry> glossary, string contextNotes, PromptPreset preset)
        => Build(sourceText, glossary, contextNotes, preset, "");

    public string Build(string sourceText, IReadOnlyList<GlossaryEntry> glossary, string contextNotes,
        PromptPreset preset, string characterConsistency)
    {
        preset.Validate();
        var prompt = new StringBuilder();
        // A single user message leaves model-specific chat template rendering to Ollama.
        if (glossary.Count > 0)
        {
            prompt.AppendLine("Reference glossary (reference only, do not translate this list):");
            foreach (var entry in glossary) prompt.AppendLine($"{entry.Chinese} = {entry.Vietnamese}");
        }
        string context = string.IsNullOrWhiteSpace(contextNotes) ? "" :
            "Context notes (reference only, do not translate these notes):\n" + contextNotes;
        // One regex pass: placeholder-like text in source/glossary is never interpreted as template code.
        string rendered = System.Text.RegularExpressions.Regex.Replace(preset.Template.ReplaceLineEndings(Environment.NewLine), @"\{\{(source_text|glossary|context)\}\}",
            match => match.Groups[1].Value switch
            {
                "source_text" => sourceText, "glossary" => prompt.ToString(), _ => context
            });
        return string.IsNullOrWhiteSpace(characterConsistency)
            ? rendered
            : characterConsistency.TrimEnd() + Environment.NewLine + Environment.NewLine + rendered;
    }
}

internal static class PromptTokenBudget
{
    // UTF-8 bytes / 3 is deliberately conservative for Chinese and Vietnamese prompts while
    // avoiding a model-specific tokenizer dependency. The caller also reserves 128 tokens.
    public static int Estimate(string value) =>
        Math.Max(1, (Encoding.UTF8.GetByteCount(value) + 2) / 3);
}
