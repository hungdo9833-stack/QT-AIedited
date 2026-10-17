using System.Globalization;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Editing;

public sealed record TextSelectionSnapshot(int Start, int Length, string Text)
{
    public static TextSelectionSnapshot Capture(string source, int start, int length)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (start < 0 || length < 0 || start > source.Length - length) throw new ArgumentOutOfRangeException(nameof(start));
        return new(start, length, source.Substring(start, length));
    }
}

public static class SourceContextActions
{
    private static readonly char[] SentencePunctuation = ['。', '！', '？', '；', '：', '，', '.', '!', '?', ';', ':', ',', '\r', '\n'];

    public static string ValidateCharacterName(string selection)
    {
        string value = ValidateChineseSelection(selection, 16, "Tên nhân vật");
        if (value.IndexOfAny(SentencePunctuation) >= 0 || value.Any(char.IsWhiteSpace))
            throw new ArgumentException("Tên nhân vật không được chứa cả câu, dấu câu hoặc xuống dòng.");
        return value;
    }

    public static string ValidateGlossaryTerm(string selection) => ValidateChineseSelection(selection, 80, "Thuật ngữ");
    public static string ValidateTranslationSelection(string selection) => ValidateChineseSelection(selection, 2000, "Vùng cần dịch");

    public static CharacterProfile? FindCharacter(CharacterProject project, string chineseName)
    {
        ArgumentNullException.ThrowIfNull(project);
        string value = ValidateCharacterName(chineseName);
        return project.Profiles.FirstOrDefault(p => string.Equals(p.ChineseName, value, StringComparison.Ordinal) ||
            p.Aliases.Any(a => string.Equals(a, value, StringComparison.Ordinal)));
    }

    public static CharacterProfile CreateQuickCharacter(string chineseName, string vietnameseName, CharacterGender gender,
        string primaryPronoun, string secondaryPronoun)
    {
        string chinese = ValidateCharacterName(chineseName);
        if (string.IsNullOrWhiteSpace(vietnameseName)) throw new ArgumentException("Hãy nhập hoặc xác nhận tên Hán Việt; ứng dụng không tự đoán.");
        var profile = new CharacterProfile
        {
            ChineseName = chinese,
            VietnameseName = vietnameseName.Trim(),
            Gender = gender,
            PrimaryNarrativePronoun = primaryPronoun.Trim(),
            SecondaryNarrativePronoun = secondaryPronoun.Trim(),
            NormalizationEnabled = gender is CharacterGender.Male or CharacterGender.Female
        };
        profile.ApplyGenderDefaults();
        profile.Validate();
        return profile;
    }

    public static IReadOnlyList<int> FindOccurrences(string source, string selection)
    {
        ArgumentNullException.ThrowIfNull(source);
        string value = ValidateGlossaryTerm(selection);
        var result = new List<int>();
        for (int offset = 0; offset <= source.Length - value.Length;)
        {
            int found = source.IndexOf(value, offset, StringComparison.Ordinal);
            if (found < 0) break;
            result.Add(found);
            offset = found + Math.Max(1, value.Length);
        }
        return result;
    }

    private static string ValidateChineseSelection(string selection, int maximumTextElements, string label)
    {
        if (string.IsNullOrWhiteSpace(selection)) throw new ArgumentException($"{label} đang trống. Hãy bôi đen chữ Trung trước.");
        string value = selection.Trim();
        if (StringInfo.ParseCombiningCharacters(value).Length > maximumTextElements)
            throw new ArgumentException($"{label} quá dài (tối đa {maximumTextElements} ký tự hiển thị).");
        if (!value.EnumerateRunes().Any(r => r.Value is >= 0x3400 and <= 0x9FFF or >= 0x20000 and <= 0x3134F))
            throw new ArgumentException($"{label} cần chứa ít nhất một chữ Hán.");
        return value;
    }
}

public sealed record SourceContextCapabilities(bool AddCharacter, bool AddGlossary, bool FindOccurrences,
    bool TranslateSelection, bool Copy);

public static class SourceContextMenuPolicy
{
    public static SourceContextCapabilities Evaluate(string selection, bool workActive, bool isReadOnly)
    {
        bool character = Valid(() => SourceContextActions.ValidateCharacterName(selection));
        bool glossary = Valid(() => SourceContextActions.ValidateGlossaryTerm(selection));
        bool translation = Valid(() => SourceContextActions.ValidateTranslationSelection(selection));
        // isReadOnly is intentionally not a blocker: all actions operate on the selection, never mutate source text.
        _ = isReadOnly;
        return new(!workActive && character, !workActive && glossary, glossary,
            !workActive && translation, !string.IsNullOrEmpty(selection));
    }

    private static bool Valid(Action validate)
    {
        try { validate(); return true; }
        catch (ArgumentException) { return false; }
    }
}
