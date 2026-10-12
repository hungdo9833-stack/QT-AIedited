using System.Collections.ObjectModel;

namespace QuickTranslator.AiEditor.Core.Traditional;

public enum DictionaryDiagnosticSeverity { Information, Warning, Error }

public sealed record DictionaryDiagnostic(
    DictionaryDiagnosticSeverity Severity,
    string File,
    int? Line,
    string Message);

public sealed class TraditionalDataException(string message, IReadOnlyList<DictionaryDiagnostic> diagnostics)
    : Exception(message)
{
    public IReadOnlyList<DictionaryDiagnostic> Diagnostics { get; } = diagnostics;
}

public sealed record TraditionalDataManifest
{
    public required string RootDirectory { get; init; }
    public required string Fingerprint { get; init; }
    public int HanVietEntries { get; init; }
    public int VietPhraseEntries { get; init; }
    public int NameEntries { get; init; }
    public int PronounEntries { get; init; }
    public int RuleEntries { get; init; }
    public int MaximumPhraseLength { get; init; } = 20;
    public int NhanAlgorithm { get; init; } = 3;
    public IReadOnlyList<DictionaryDiagnostic> Diagnostics { get; init; } = [];
}

public sealed class TraditionalDictionarySnapshot
{
    internal TraditionalDictionarySnapshot(
        TraditionalDataManifest manifest,
        IReadOnlyDictionary<string, string> hanViet,
        IReadOnlyDictionary<string, string> vietPhrase,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, string> pronouns,
        IReadOnlyDictionary<string, string> rules,
        IReadOnlyDictionary<string, string> marks)
    {
        Manifest = manifest;
        HanViet = hanViet;
        VietPhrase = vietPhrase;
        Names = names;
        Pronouns = pronouns;
        Rules = rules;
        Marks = marks;
        var combined = new Dictionary<string, string>(vietPhrase, StringComparer.Ordinal);
        foreach (var item in names) combined[item.Key] = item.Value;
        CombinedOneMeaning = new ReadOnlyDictionary<string, string>(combined.ToDictionary(
            pair => pair.Key, pair => FirstMeaning(pair.Value), StringComparer.Ordinal));
        NhanOneMeaning = BuildNhanDictionary(manifest.NhanAlgorithm, pronouns, names, vietPhrase);
    }

    public TraditionalDataManifest Manifest { get; }
    public IReadOnlyDictionary<string, string> HanViet { get; }
    public IReadOnlyDictionary<string, string> VietPhrase { get; }
    public IReadOnlyDictionary<string, string> Names { get; }
    public IReadOnlyDictionary<string, string> Pronouns { get; }
    public IReadOnlyDictionary<string, string> Rules { get; }
    public IReadOnlyDictionary<string, string> Marks { get; }
    public IReadOnlyDictionary<string, string> CombinedOneMeaning { get; }
    internal IReadOnlyDictionary<string, string> NhanOneMeaning { get; }

    internal static string FirstMeaning(string value)
    {
        int slash = value.IndexOfAny(['/','|']);
        return slash < 0 ? value : value[..slash];
    }

    private static IReadOnlyDictionary<string, string> BuildNhanDictionary(int algorithm,
        IReadOnlyDictionary<string, string> pronouns, IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, string> phrases)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (algorithm >= 3)
            foreach (var item in phrases) result.TryAdd(item.Key, FirstMeaning(item.Value));
        if (algorithm >= 2)
            foreach (var item in names) result[item.Key] = FirstMeaning(item.Value);
        foreach (var item in pronouns) result[item.Key] = FirstMeaning(item.Value);
        return new ReadOnlyDictionary<string, string>(result);
    }
}

public sealed record TraditionalTranslationOptions
{
    public bool PrioritizeNames { get; init; } = true;
    public int TranslationAlgorithm { get; init; }
}

public readonly record struct TextRange(int Start, int Length)
{
    public int End => checked(Start + Length);
    public bool Overlaps(TextRange other) => Length == 0
        ? other.Start <= Start && Start <= other.End
        : Start < other.End && other.Start < End;
}

public readonly record struct AlignmentSpan(TextRange Source, TextRange Target);

public sealed record TraditionalTranslationResult
{
    public required string SourceText { get; init; }
    public required string SourceVersion { get; init; }
    public required string HanVietText { get; init; }
    public required string VietPhraseText { get; init; }
    public required IReadOnlyList<AlignmentSpan> HanVietAlignment { get; init; }
    public required IReadOnlyList<AlignmentSpan> VietPhraseAlignment { get; init; }
    public required string DictionaryFingerprint { get; init; }
}
