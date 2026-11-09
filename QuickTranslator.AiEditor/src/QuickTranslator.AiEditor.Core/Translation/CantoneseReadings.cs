namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// Vietnamese meanings for Cantonese words that Hong Kong-set novels put in dialogue. Neither the QuickTranslator
/// dictionaries nor the translation models know them, so they survive translation as raw Chinese. Only words that
/// are Cantonese-specific are listed, so a Mandarin leftover is never misread. An empty meaning is a sentence
/// particle that Vietnamese does without.
/// </summary>
public static class CantoneseReadings
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.Ordinal)
    {
        // verbs and adjectives
        ["瞓"] = "ngủ", ["瞓覺"] = "ngủ", ["氹"] = "dỗ", ["睇"] = "xem", ["嚟"] = "đến", ["返嚟"] = "về",
        ["搵"] = "tìm", ["揾"] = "tìm", ["攞"] = "lấy", ["俾"] = "cho", ["畀"] = "cho", ["揸"] = "cầm",
        ["靚"] = "đẹp", ["靚女"] = "người đẹp", ["靚仔"] = "anh đẹp trai", ["嬲"] = "giận", ["攰"] = "mệt", ["癐"] = "mệt",
        ["嘥"] = "phí", ["啱"] = "đúng", ["掂"] = "ổn", ["傾偈"] = "tán gẫu", ["得閒"] = "rảnh", ["得闲"] = "rảnh",
        ["唔該"] = "cảm ơn", ["唔该"] = "cảm ơn", ["唔好"] = "đừng", ["唔"] = "không", ["冇"] = "không có",
        ["冇問題"] = "không vấn đề gì", ["冇问题"] = "không vấn đề gì", ["喺"] = "ở", ["咗"] = "rồi", ["冚"] = "hết",
        // pronouns, question words, time and place
        ["佢"] = "người đó", ["佢哋"] = "bọn họ", ["我哋"] = "chúng tôi", ["你哋"] = "các người", ["哋"] = "",
        ["嘢"] = "đồ", ["乜嘢"] = "cái gì", ["乜"] = "gì", ["咩"] = "gì", ["點解"] = "tại sao", ["点解"] = "tại sao",
        ["點樣"] = "thế nào", ["点样"] = "thế nào", ["邊個"] = "ai", ["边个"] = "ai", ["邊度"] = "ở đâu", ["边度"] = "ở đâu",
        ["幾多"] = "bao nhiêu", ["几多"] = "bao nhiêu", ["而家"] = "bây giờ", ["宜家"] = "bây giờ", ["琴日"] = "hôm qua",
        ["聽日"] = "ngày mai", ["听日"] = "ngày mai", ["屋企"] = "nhà", ["細路"] = "nhóc", ["细路"] = "nhóc",
        ["嗰"] = "kia", ["嗰度"] = "ở đó", ["呢度"] = "ở đây", ["啲"] = "chút", ["噉"] = "như vậy", ["咁"] = "như vậy",
        ["嘅"] = "của",
        // sentence particles
        ["㗎"] = "", ["喇"] = "", ["囉"] = "", ["啰"] = "", ["喎"] = "", ["啫"] = "", ["咯"] = "", ["吖"] = "",
        ["嘞"] = "", ["㖭"] = "", ["啩"] = "", ["噃"] = "", ["嚱"] = "", ["咋"] = "",
    };

    private static readonly int Longest = Words.Keys.Max(key => key.Length);

    /// <summary>The meaning of a run made only of Cantonese words (longest match first); false when any
    /// character is not one.</summary>
    public static bool TryRead(string run, out string reading)
    {
        reading = "";
        if (string.IsNullOrEmpty(run)) return false;
        var parts = new List<string>();
        for (int i = 0; i < run.Length;)
        {
            int length = Math.Min(Longest, run.Length - i);
            while (length > 0 && !Words.ContainsKey(run.Substring(i, length))) length--;
            if (length == 0) return false;
            string meaning = Words[run.Substring(i, length)];
            if (meaning.Length > 0) parts.Add(meaning);
            i += length;
        }
        reading = string.Join(' ', parts);
        return true;
    }

    /// <summary>Replaces every Cantonese word inside <paramref name="text"/> with its meaning, leaving the rest.</summary>
    public static string Replace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var builder = new System.Text.StringBuilder(text.Length);
        for (int i = 0; i < text.Length;)
        {
            int length = Math.Min(Longest, text.Length - i);
            while (length > 0 && !Words.ContainsKey(text.Substring(i, length))) length--;
            if (length == 0) { builder.Append(text[i++]); continue; }
            string meaning = Words[text.Substring(i, length)];
            if (meaning.Length > 0)
            {
                if (builder.Length > 0 && char.IsLetter(builder[^1])) builder.Append(' ');
                builder.Append(meaning);
                if (i + length < text.Length && char.IsLetter(text[i + length])) builder.Append(' ');
            }
            i += length;
        }
        return builder.ToString();
    }
}
