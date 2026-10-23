using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// Removes reference notes a model echoed into its translation. Seen live with Index-Translate-9B: the passage was
/// translated correctly, then the DIALOGUE MAP ("“…”: speaker → listener (self=…; address=…)") followed it, itself
/// translated into Vietnamese. Only a trailing block is removed; note-like lines inside the prose are reported so the
/// segment is retranslated instead of silently edited. Pure; performs no I/O.
/// </summary>
public static class PromptEchoFilter
{
    // "“小白道长…”: Tô Bạch → Tô đạo trưởng (tôi = tôi; xưng hô = Tô đạo trưởng)" and the English form of the same line.
    private static readonly Regex MapLine = new(@"^\s*[-•*]?\s*[“""「『«].{0,80}?[”""」』»]\s*[:：]\s*[^\n]{1,80}?(?:→|->)",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex NoteLine = new(
        @"DIALOGUE MAP|CHARACTER CONSISTENCY|Character notes|BẢN ĐỒ (?:LỜI |HỘI )?THOẠI|bản đồ (?:lời thoại|hội thoại)|listener unclear|người nghe không rõ|\bself\s*=|\baddress\s*=|narrative pronoun\s*=|\(LOCKED\)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public sealed record Result(string Text, int RemovedLines, int EchoLinesInsideProse);

    public static bool IsEchoLine(string line) => MapLine.IsMatch(line) || NoteLine.IsMatch(line);

    public static Result Strip(string text)
    {
        if (string.IsNullOrEmpty(text)) return new(text, 0, 0);
        string[] lines = text.ReplaceLineEndings("\n").Split('\n');
        int end = lines.Length, removed = 0;
        // Walk back over the trailing block: echo lines and the blank lines between them.
        while (end > 0)
        {
            string line = lines[end - 1];
            if (line.Trim().Length == 0) { end--; continue; }
            if (!IsEchoLine(line)) break;
            end--; removed++;
        }
        // A lone arrow line after real prose could be prose ("A → B" in a story is rare but possible), so a tail needs two
        // lines; output made only of note lines is never a translation, whatever its length.
        bool onlyNotes = lines.Take(end).All(l => l.Trim().Length == 0);
        if (removed < 2 && !onlyNotes) { end = lines.Length; removed = 0; }
        int inside = lines.Take(end).Count(IsEchoLine);
        if (removed == 0) return new(text, 0, inside);
        string kept = string.Join("\n", lines.Take(end)).TrimEnd();
        return new(kept, removed, inside);
    }
}
