using System.Text;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// Index-Translate's canonical "instTrans" prompt: source under 【源文】, numbered Chinese constraints under 【约束要求】.
/// A 2026-11 side-by-side test on two books showed this form keeps 51/51 glossary terms (the English rulebook prompt
/// kept 49/51) and applies the story register and addressing notes far more faithfully. The model also follows a wrong
/// note just as faithfully, so the project's story setting must be right.
/// A preset opts in by containing <see cref="InstTransMarker"/>; the English reference blocks the translation service
/// produces are rewritten here as Chinese constraint lines, and every constraint line in that section is numbered.
/// </summary>
public sealed partial class TranslationPromptBuilder
{
    public const string InstTransMarker = "【约束要求】";
    private const string Hard = "【硬性要求】";
    private const string Note = "【注意】";
    private const string Reference = "【参考】";

    public static bool IsInstTrans(string template) => template.Contains(InstTransMarker, StringComparison.Ordinal);

    private static string BuildInstTrans(string template, string sourceText, IReadOnlyList<GlossaryEntry> glossary,
        string contextNotes, string characterConsistency)
    {
        var glossaryLines = new List<string>();
        if (glossary.Count > 0)
        {
            glossaryLines.Add($"{Hard}专名/术语对照（必须使用箭头右侧的越南语译名）: " +
                string.Join("、", glossary.Select(entry => $"{entry.Chinese}→{entry.Vietnamese}")));
            if (glossary.Any(entry => HanVietTitle().IsMatch(entry.Vietnamese)))
                glossaryLines.Add($"{Hard}术语表中的汉越称谓（如 Đàm phu nhân、Trương tổng、Lâm thiếu gia）必须原样使用，" +
                    "不要改成 bà/ông/cậu/cô/anh/chị");
        }
        var contextLines = new List<string>();
        bool characterRules = false;
        foreach (string line in (characterConsistency + "\n" + contextNotes).ReplaceLineEndings("\n").Split('\n'))
            if (ConvertNote(line.Trim(), ref characterRules) is { } converted) contextLines.Add(converted);
        if (characterRules) contextLines.Add($"{Note}以上人物与称呼规则只在原文明确说话人和听话人时使用，不要编造说话人、关系或称呼");

        string normalized = template.ReplaceLineEndings("\n");
        bool hasContextSlot = normalized.Contains("{{context}}", StringComparison.Ordinal);
        bool hasGlossarySlot = normalized.Contains("{{glossary}}", StringComparison.Ordinal);
        // Notes without their own slot ride with the glossary; with neither slot they open the constraint list.
        if (!hasContextSlot) glossaryLines.AddRange(contextLines);
        if (!hasGlossarySlot)
            normalized = normalized.Replace(InstTransMarker, InstTransMarker + "\n" + string.Join("\n", glossaryLines), StringComparison.Ordinal);
        string rendered = Regex.Replace(normalized, @"\{\{(source_text|glossary|context)\}\}", match => match.Groups[1].Value switch
        {
            "source_text" => sourceText,
            "glossary" => string.Join("\n", glossaryLines),
            _ => string.Join("\n", contextLines)
        });
        return NumberConstraints(rendered).ReplaceLineEndings(Environment.NewLine);
    }

    /// <summary>Numbers every constraint line after the marker and drops the blank lines empty slots leave behind.</summary>
    private static string NumberConstraints(string rendered)
    {
        int marker = rendered.IndexOf(InstTransMarker, StringComparison.Ordinal);
        string head = rendered[..(marker + InstTransMarker.Length)];
        var output = new StringBuilder(head);
        string[] lines = rendered[(marker + InstTransMarker.Length)..].Split('\n');
        int number = 0;
        bool inList = true;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            bool constraint = line.StartsWith(Hard, StringComparison.Ordinal) || line.StartsWith(Note, StringComparison.Ordinal) ||
                line.StartsWith(Reference, StringComparison.Ordinal);
            if (i == 0) { output.Append(line); continue; }
            if (constraint && inList) { output.Append('\n').Append(++number).Append(". ").Append(line); continue; }
            if (line.Length == 0 && inList) continue;
            // The first non-constraint text closes the list; keep one blank line before it, as in the official format.
            if (inList) { output.Append('\n'); inList = false; }
            output.Append('\n').Append(line);
        }
        return output.ToString();
    }

    private static string? ConvertNote(string line, ref bool characterRules)
    {
        if (line.Length == 0 || line == CharacterNotesHeader || line.StartsWith("DIALOGUE MAP", StringComparison.Ordinal) ||
            line.StartsWith("Translate each listed quote", StringComparison.Ordinal) ||
            line.StartsWith("Context notes (reference only", StringComparison.Ordinal) ||
            line.StartsWith("CHARACTER CONSISTENCY", StringComparison.Ordinal) ||
            line.StartsWith("Use these rules only when", StringComparison.Ordinal)) return null;

        if (MapLine().Match(line) is { Success: true } map)
            return map.Groups["listener"].Value.Contains("unclear", StringComparison.Ordinal) ? null
                : map.Groups["self"].Success
                    ? $"{Note}“{map.Groups["quote"].Value}”这句话是{map.Groups["speaker"].Value}对{map.Groups["listener"].Value}说的：" +
                      $"说话人自称“{map.Groups["self"].Value}”，称呼对方“{map.Groups["address"].Value}”"
                    // Who speaks to whom still settles the pronouns when no Vietnamese pair is known.
                    : $"{Note}“{map.Groups["quote"].Value}”这句话是{map.Groups["speaker"].Value}对{map.Groups["listener"].Value}说的";
        if (CharacterLine().Match(line) is { Success: true } character)
        {
            characterRules = true;
            string gender = character.Groups["gender"].Value switch { "female" => "（女性）", "male" => "（男性）", _ => "" };
            string name = $"{Hard}人物 {character.Groups["zh"].Value.Trim()}{gender} 译为 {character.Groups["vi"].Value.Trim()}";
            if (!character.Groups["pronoun"].Success) return name;
            string pronoun = character.Groups["pronoun"].Value.Trim();
            // A locked pronoun is part of the hard rule; the survey's unlocked choice is guidance on its own line.
            return character.Groups["locked"].Success
                ? $"{name}；叙述中用“{pronoun}”指代此人"
                : $"{name}\n{Note}叙述中指代{character.Groups["vi"].Value.Trim()}时用“{pronoun}”，不要换成其他性别的代词";
        }
        if (DialogueRuleLine().Match(line) is { Success: true } rule)
        {
            characterRules = true;
            return $"{(rule.Groups["locked"].Success ? Hard : Note)}{rule.Groups["speaker"].Value}对{rule.Groups["listener"].Value}说话时" +
                $"自称“{rule.Groups["self"].Value.Trim()}”，称呼对方“{rule.Groups["address"].Value.Trim()}”";
        }
        if (line.StartsWith("STORY SETTING: historical", StringComparison.Ordinal))
            return $"{Note}故事背景为古代：对话使用越南语古典称呼（ta、ngươi、huynh、muội、tại hạ…），符合人物身份与关系，同一对人物的称呼保持一致";
        if (line.StartsWith("STORY SETTING: modern", StringComparison.Ordinal))
        {
            string banned = Regex.Match(line, @"Never use archaic addressing: (.+?)\.?$") is { Success: true } m ? $"；禁止使用古风称呼：{m.Groups[1].Value}" : "";
            return $"{Note}故事背景为现代：对话按人物关系和语气使用现代越南语称呼（tôi、anh、em、chị、cô、cậu、ông、bà、con、bố、mẹ、tao、mày…）{banned}";
        }
        if (line.StartsWith(StorySettingRules.StylePrefix, StringComparison.Ordinal))
            return $"{Note}译文要像越南作家写的小说：按越南语习惯调整语序，不要逐字照搬中文句式；有常用的越南语说法时，不要硬套生僻的汉越词" +
                "（如“表姨母”译 dì họ，不译 biểu di mẫu）；过长的句子可以拆开；原文重复的连接词（如“甚至”）不必每次照译" +
                (line.Contains(" historical.", StringComparison.Ordinal) ? "；古代背景可保留常见古风称谓（phụ thân、cô nương、công tử…），叙述仍要通顺易懂"
                    : line.Contains(" modern.", StringComparison.Ordinal) ? "；现代背景的叙述用日常越南语，少用汉越词" : "");
        if (line.StartsWith("The passage opens mid-conversation", StringComparison.Ordinal))
            return $"{Note}本段开头的对话承接上文，说话人与听话人的自称和称呼与上文保持一致";
        if (Regex.Match(line, @"^Preceding source \(reference only, do not translate\): (.+)$") is { Success: true } preceding)
            return $"{Reference}上文原文（仅用于理解语境，不要翻译，不要输出）：{preceding.Groups[1].Value}";
        if (Regex.Match(line, @"^Previous .+? excerpt \(reference only; never overwrite user text\): (.+)$") is { Success: true } previous)
            return $"{Reference}上文已有译文（仅用于保持称呼和用词一致，不要输出）：{previous.Groups[1].Value}";
        // Project notes, memory and book knowledge stay in their own words; the label keeps them out of the output.
        return $"{Note}{line}";
    }

    [GeneratedRegex(@"^- “(?<quote>.+?)”: (?<speaker>.+?) → (?<listener>.+?)(?: \(likely\))?(?: \(self=(?<self>.+?); address=(?<address>.+?)\))?$")]
    private static partial Regex MapLine();

    [GeneratedRegex(@"^- CHARACTER (?<zh>.+?) = (?<vi>[^;(]+?)(?:; gender = (?<gender>female|male))?(?:; narrative pronoun = (?<pronoun>[^;(]+?)(?<locked> \(LOCKED\))?)?(?: \(.*\))?$")]
    private static partial Regex CharacterLine();

    [GeneratedRegex(@"^- DIALOGUE (?<speaker>.+?) -> (?<listener>.+?): self = (?<self>.+?); address = (?<address>.+?)(?<locked> \(LOCKED\))?$")]
    private static partial Regex DialogueRuleLine();
}
