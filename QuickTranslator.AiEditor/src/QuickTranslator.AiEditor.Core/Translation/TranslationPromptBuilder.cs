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
    public const string Version = "translategemma-zh-vi-v3-character-notes-in-context";
    internal const string CharacterNotesHeader =
        "Character notes (reference only: never translate, repeat or output these notes; use them only to choose names and forms of address):";

    public string Build(string sourceText, IReadOnlyList<GlossaryEntry> glossary, string contextNotes)
        => Build(sourceText, glossary, contextNotes, PromptPreset.Default);

    public string Build(string sourceText, IReadOnlyList<GlossaryEntry> glossary, string contextNotes, PromptPreset preset)
        => Build(sourceText, glossary, contextNotes, preset, "");

    public string Build(string sourceText, IReadOnlyList<GlossaryEntry> glossary, string contextNotes,
        PromptPreset preset, string characterConsistency)
    {
        preset.Validate();
        var prompt = new StringBuilder();
        // A single user message leaves model-specific chat template rendering to llama-server (--jinja).
        if (glossary.Count > 0)
        {
            prompt.AppendLine("Reference glossary (reference only, do not translate this list):");
            foreach (var entry in glossary) prompt.AppendLine($"{entry.Chinese} = {entry.Vietnamese}");
        }
        string context = string.IsNullOrWhiteSpace(contextNotes) ? "" :
            "Context notes (reference only, do not translate these notes):\n" + contextNotes;
        // Character rules and the dialogue map quote Chinese snippets. Placed before the translator instructions without
        // a label, a translation-tuned model (Index-Translate) translated them as if they were the passage. They now sit
        // with the other reference blocks, after the instructions and clearly marked as notes that are never output.
        string characters = string.IsNullOrWhiteSpace(characterConsistency) ? "" :
            CharacterNotesHeader + "\n" + characterConsistency.TrimEnd();
        string template = preset.Template.ReplaceLineEndings(Environment.NewLine);
        bool hasContextSlot = template.Contains("{{context}}", StringComparison.Ordinal);
        if (characters.Length > 0 && hasContextSlot) context = context.Length == 0 ? characters : characters + "\n" + context;
        // One regex pass: placeholder-like text in source/glossary is never interpreted as template code.
        string rendered = System.Text.RegularExpressions.Regex.Replace(template, @"\{\{(source_text|glossary|context)\}\}",
            match => match.Groups[1].Value switch
            {
                "source_text" => sourceText, "glossary" => prompt.ToString(), _ => context
            });
        // A custom preset without {{context}} still gets the notes, labelled, ahead of its own text.
        return characters.Length == 0 || hasContextSlot
            ? rendered
            : characters + Environment.NewLine + Environment.NewLine + rendered;
    }
}

internal static class PromptTokenBudget
{
    // UTF-8 bytes / 3 is deliberately conservative for Chinese and Vietnamese prompts while
    // avoiding a model-specific tokenizer dependency. The caller also reserves 128 tokens.
    public static int Estimate(string value) =>
        Math.Max(1, (Encoding.UTF8.GetByteCount(value) + 2) / 3);
}
