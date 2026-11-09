using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>A translator for single paragraphs that a repair can borrow, with the options to call it with.</summary>
public sealed record HanRepairTranslator(ILlmClient Client, ModelOptions Options);

/// <summary>
/// Wraps a whole-chapter API translator (Gemini Flash-Lite) that now and then leaves Chinese in its Vietnamese
/// ("Sư姐", "Được嘞", an unlisted name half transliterated). Instead of sending the chapter again it:
/// asks for no Chinese up front; turns Chinese stuck to a capitalised name into Hán-Việt at once; translates only
/// the paragraphs still holding Chinese again, on this machine's GPU when it runs (no API quota) or else in one
/// small request to the wrapped client; and as a last resort writes what is left in Hán-Việt.
/// Only instTrans prompts are touched: their 【源文】 block gives the source paragraph of each translated one.
/// </summary>
public sealed partial class HanResidueRepairClient(
    ILlmClient inner,
    Func<HanRepairTranslator?> localTranslator,
    Func<string, CancellationToken, Task<string>>? hanViet,
    bool askForNoHan = true,
    Func<string, CancellationToken, Task<string>>? vietPhrase = null) : ILlmBackendClient, IJsonSchemaLlmClient
{
    private const int LongRun = 4;
    // askForNoHan is false for the local Index-Translate model: its prompt format is fixed, so only the output is repaired.
    public const string NoHanConstraint =
        "【硬性要求】译文中不得保留任何汉字：语气词、称谓、昵称、人名和地名也必须写成越南语，人名地名用汉越音";
    private const string SourceMarker = "【源文】";
    private const string ConstraintMarker = TranslationPromptBuilder.InstTransMarker;
    private const string OutputOnly = "只输出译文";

    /// <summary>What the last repair did, for logs and tests.</summary>
    public HanRepairReport? LastReport { get; private set; }

    public async Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
    {
        bool instTrans = prompt.Contains(SourceMarker, StringComparison.Ordinal) && prompt.Contains(ConstraintMarker, StringComparison.Ordinal);
        string asked = instTrans && askForNoHan ? WithNoHanConstraint(prompt) : prompt;
        LlmCompletion completion = await inner.TranslateAsync(asked, options, cancellationToken).ConfigureAwait(false);
        LastReport = null;
        if (!instTrans || !HasHan(completion.Text)) return completion;
        try
        {
            (string text, HanRepairReport report) = await RepairAsync(asked, completion.Text, options, cancellationToken)
                .ConfigureAwait(false);
            LastReport = report;
            return completion with { Text = text };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A repair that fails must never cost the chapter: keep Gemini's text as it came.
            LastReport = new HanRepairReport(0, 0, 0, 0, "Không sửa được chữ Hán sót: " + ex.Message);
            return completion;
        }
    }

    /// <summary>Repairs a translation that is already saved (no new request for the whole text). Returns it unchanged
    /// when it holds no Chinese, the prompt is not instTrans, or the repair fails.</summary>
    public async Task<string> RepairTranslationAsync(string prompt, string translated, ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        LastReport = null;
        bool instTrans = prompt.Contains(SourceMarker, StringComparison.Ordinal) && prompt.Contains(ConstraintMarker, StringComparison.Ordinal);
        if (!instTrans || !HasHan(translated)) return translated;
        try
        {
            (string text, HanRepairReport report) = await RepairAsync(prompt, translated, options, cancellationToken)
                .ConfigureAwait(false);
            LastReport = report;
            // Keep the saved text's own line breaks so only the repaired spots change.
            return translated.Contains("\r\n", StringComparison.Ordinal) ? text : text.Replace("\r\n", "\n");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LastReport = new HanRepairReport(0, 0, 0, 0, "Không sửa được chữ Hán sót: " + ex.Message);
            return translated;
        }
    }

    public Task<LlmCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement responseSchema,
        CancellationToken cancellationToken = default) =>
        inner is IJsonSchemaLlmClient structured
            ? structured.TranslateStructuredAsync(prompt, options, responseSchema, cancellationToken)
            : throw new NotSupportedException("Client bên trong không hỗ trợ JSON Schema.");

    public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default) =>
        inner is ILlmBackendClient backend ? backend.CheckConnectionAsync(model, timeout, cancellationToken) : Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
        inner is ILlmBackendClient backend ? backend.ListModelsAsync(timeout, cancellationToken)
            : Task.FromResult<IReadOnlyList<string>>([]);

    public void Dispose() => (inner as IDisposable)?.Dispose();

    public static bool HasHan(string text) => HanRun().IsMatch(text);

    internal static string WithNoHanConstraint(string prompt)
    {
        if (prompt.Contains(NoHanConstraint, StringComparison.Ordinal)) return prompt;
        int output = prompt.LastIndexOf(OutputOnly, StringComparison.Ordinal);
        return output < 0
            ? prompt.TrimEnd() + Environment.NewLine + NoHanConstraint
            : prompt[..output] + NoHanConstraint + Environment.NewLine + Environment.NewLine + prompt[output..];
    }

    /// <summary>The source text of an instTrans prompt, or null.</summary>
    internal static string? SourceOf(string prompt)
    {
        int start = prompt.IndexOf(SourceMarker, StringComparison.Ordinal);
        int end = prompt.IndexOf(ConstraintMarker, StringComparison.Ordinal);
        if (start < 0 || end <= start) return null;
        return prompt[(start + SourceMarker.Length)..end].Trim('\r', '\n', ' ');
    }

    /// <summary>The same prompt with another source text: the constraints (names, notes) stay as they were.</summary>
    internal static string WithSource(string prompt, string source)
    {
        int start = prompt.IndexOf(SourceMarker, StringComparison.Ordinal) + SourceMarker.Length;
        int end = prompt.IndexOf(ConstraintMarker, StringComparison.Ordinal);
        return prompt[..start] + Environment.NewLine + source + Environment.NewLine + Environment.NewLine + prompt[end..];
    }

    private async Task<(string Text, HanRepairReport Report)> RepairAsync(string prompt, string translated, ModelOptions options,
        CancellationToken cancellationToken)
    {
        // 0. Lines where the model copied the prompt's own instructions ("【约束提示】…") are not translation: drop them.
        string[] lines = translated.ReplaceLineEndings("\n").Split('\n').Where(line => !PromptEcho().IsMatch(line)).ToArray();
        int[] textLines = lines.Select((line, index) => (line, index)).Where(item => item.line.Trim().Length > 0)
            .Select(item => item.index).ToArray();
        string[] sourceParagraphs = (SourceOf(prompt) ?? "").ReplaceLineEndings("\n").Split('\n')
            .Where(line => line.Trim().Length > 0).ToArray();
        int names = 0, local = 0, api = 0, spots = 0;

        // 1. Chinese stuck to a name of two or more capitalised words ("Khương Cẩm婳"): Hán-Việt, capitalised.
        if (hanViet is not null)
            for (int i = 0; i < lines.Length; i++)
            {
                if (!HasHan(lines[i])) continue;
                var builder = new StringBuilder();
                int last = 0;
                foreach (Match match in NameThenHan().Matches(lines[i]))
                {
                    Group han = match.Groups["han"];
                    string reading = Capitalise(await hanViet(han.Value, cancellationToken).ConfigureAwait(false));
                    if (reading.Length == 0 || HasHan(reading)) continue;
                    builder.Append(lines[i], last, han.Index - last).Append(' ').Append(reading);
                    last = han.Index + han.Length;
                    names++;
                }
                if (last > 0) lines[i] = builder.Append(lines[i], last, lines[i].Length - last).ToString().Replace("  ", " ");
            }

        // 2. Paragraphs still holding Chinese, when they line up one to one with the source paragraphs.
        List<int> pending = textLines.Length == sourceParagraphs.Length
            ? Enumerable.Range(0, textLines.Length).Where(k => HasHan(lines[textLines[k]]) &&
                !OnlyCantonese(lines[textLines[k]])).ToList() : [];
        if (pending.Count > 0 && localTranslator() is { } gpu)
        {
            foreach (int k in pending.ToArray())
            {
                LlmCompletion again = await gpu.Client.TranslateAsync(WithSource(prompt, sourceParagraphs[k]), gpu.Options,
                    cancellationToken).ConfigureAwait(false);
                string paragraph = OneParagraph(again.Text);
                if (!SafeReplacement(lines[textLines[k]], paragraph)) continue;
                lines[textLines[k]] = Indent(lines[textLines[k]]) + paragraph;
                pending.Remove(k);
                local++;
            }
        }
        else if (pending.Count > 0)
        {
            string batch = string.Join("\n\n", pending.Select(k => sourceParagraphs[k]));
            LlmCompletion again = await inner.TranslateAsync(WithSource(prompt, batch), options, cancellationToken)
                .ConfigureAwait(false);
            string[] answers = again.Text.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim())
                .Where(line => line.Length > 0).ToArray();
            if (answers.Length == pending.Count)
                for (int j = answers.Length - 1; j >= 0; j--)
                {
                    int k = pending[j];
                    if (!SafeReplacement(lines[textLines[k]], answers[j])) continue;
                    lines[textLines[k]] = Indent(lines[textLines[k]]) + answers[j];
                    pending.RemoveAt(j);
                    api++;
                }
        }

        // 3. Whatever is still Chinese: its Hán-Việt reading, so no Chinese reaches the reader.
        if (hanViet is not null)
            for (int i = 0; i < lines.Length; i++)
            {
                if (!HasHan(lines[i])) continue;
                var builder = new StringBuilder();
                int last = 0;
                foreach (Match match in HanRun().Matches(lines[i]))
                {
                    string reading = LeftoverReadings.TryGetValue(match.Value, out string? known) ? known
                        : CantoneseReadings.TryRead(match.Value, out string cantonese) ? cantonese
                        // A whole Chinese phrase reads as nonsense in Hán-Việt: its VietPhrase meaning is rough but readable.
                        : match.Value.Length >= LongRun && vietPhrase is not null
                            ? (await vietPhrase(match.Value, cancellationToken).ConfigureAwait(false)).Trim()
                        : (await hanViet(match.Value, cancellationToken).ConfigureAwait(false)).Trim();
                    if (HasHan(reading)) reading = SpaceBeforePunctuation().Replace(CantoneseReadings.Replace(reading), "$1").Trim();
                    if (HasHan(reading)) continue;
                    builder.Append(lines[i], last, match.Index - last);
                    last = match.Index + match.Length;
                    spots++;
                    if (reading.Length == 0) continue; // a particle Vietnamese does without
                    bool sentenceStart = builder.ToString().TrimEnd().Length == 0 ||
                        builder.ToString().TrimEnd()[^1] is '.' or '!' or '?' or '“' or '"' or ':' or '…' or '【' or '「' or '『' or '（';
                    reading = sentenceStart ? char.ToUpperInvariant(reading[0]) + reading[1..]
                        : char.ToLowerInvariant(reading[0]) + reading[1..];
                    if (builder.Length > 0 && !char.IsWhiteSpace(builder[^1]) && builder[^1] is not ('“' or '"' or '(' or '【' or '（' or '「' or '『')) builder.Append(' ');
                    builder.Append(reading);
                    if (last < lines[i].Length && char.IsLetter(lines[i][last])) builder.Append(' ');
                }
                if (last > 0)
                    lines[i] = SpaceBeforePunctuation().Replace(
                        builder.Append(lines[i], last, lines[i].Length - last).ToString().Replace("  ", " "), "$1");
            }

        string text = string.Join(Environment.NewLine, lines);
        return (text, new HanRepairReport(names, local, api, spots,
            HasHan(text) ? "Vẫn còn chữ Hán sau khi sửa." : null));
    }

    /// <summary>
    /// A new translation of a paragraph replaces the old one only when it is clearly the same paragraph: no Chinese,
    /// most of the old wording shared (a paragraph that drifted out of line shares little), and every name the old
    /// one used still there (a second model must not rename a character).
    /// </summary>
    internal static bool SafeReplacement(string old, string replacement)
    {
        if (replacement.Length == 0 || HasHan(replacement)) return false;
        string[] before = Syllables(HanRun().Replace(old, " ")), after = Syllables(replacement);
        if (before.Length == 0 || after.Length == 0) return false;
        var afterSet = after.ToHashSet(StringComparer.Ordinal);
        double shared = before.Distinct(StringComparer.Ordinal).Count(afterSet.Contains) /
            (double)before.Distinct(StringComparer.Ordinal).Count();
        if (shared < 0.5 || after.Length < before.Length * 0.6 || after.Length > before.Length * 1.6) return false;
        // Every capitalised word the old paragraph had (names, "Cường ca", "Sư tỷ") must still be there.
        return Capitalised(HanRun().Replace(old, " ")).All(afterSet.Contains);
    }

    private static string[] Syllables(string text) =>
        Words().Matches(text.ToLowerInvariant()).Select(match => match.Value).ToArray();

    private static IEnumerable<string> Capitalised(string text) =>
        Words().Matches(text).Where(match => char.IsUpper(match.Value[0])).Select(match => match.Value.ToLowerInvariant()).Distinct();

    private static string OneParagraph(string text) =>
        string.Join(' ', text.ReplaceLineEndings("\n").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));

    private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];

    private static string Capitalise(string reading) =>
        string.Join(' ', reading.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => char.ToUpper(word[0], System.Globalization.CultureInfo.GetCultureInfo("vi-VN")) + word[1..]));

    [GeneratedRegex(@"[㐀-䶿一-鿿豈-﫿]+")]
    private static partial Regex HanRun();

    // A line that is the prompt talking: an instTrans heading such as 【约束提示】【注意】【硬性要求】【源文】, or the output rule.
    [GeneratedRegex(@"^\s*(【(约束|注意|硬性|源文|术语|参考|人物|称谓|角色)[^】]{0,8}】|只输出译文|请将以下)")]
    private static partial Regex PromptEcho();

    /// <summary>No translator knows Cantonese words, so a line whose Chinese is all Cantonese skips step 2.</summary>
    private static bool OnlyCantonese(string line) =>
        HanRun().Matches(line).All(match => CantoneseReadings.TryRead(match.Value, out _));

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Words();

    [GeneratedRegex(@" ([,.!?;:…”])")]
    private static partial Regex SpaceBeforePunctuation();

    /// <summary>Leftovers a word-by-word Hán-Việt reading gets wrong: kinship/sect titles and sentence particles
    /// (dropped, since the Vietnamese sentence already carries their tone).</summary>
    private static readonly Dictionary<string, string> LeftoverReadings = new(StringComparer.Ordinal)
    {
        ["姐"] = "tỷ", ["妹"] = "muội", ["哥"] = "ca", ["弟"] = "đệ", ["兄"] = "huynh",
        ["嘞"] = "", ["啊"] = "", ["呢"] = "", ["吧"] = "", ["呀"] = "", ["啦"] = "", ["嘛"] = "", ["哦"] = "",
        ["倒是"] = "", ["了"] = "", ["的"] = "",
    };

    // Two or more capitalised Vietnamese words, then Chinese with at most one space between.
    [GeneratedRegex(@"\p{Lu}\p{Ll}*(?:[ ]\p{Lu}\p{Ll}*)+[ ]?(?<han>[㐀-䶿一-鿿豈-﫿]+)")]
    private static partial Regex NameThenHan();
}

/// <summary>How many spots a repair fixed and how: names in Hán-Việt, paragraphs on the GPU or in one API request,
/// and leftovers written in Hán-Việt.</summary>
public sealed record HanRepairReport(int Names, int LocalParagraphs, int ApiParagraphs, int HanVietSpots, string? Warning);
