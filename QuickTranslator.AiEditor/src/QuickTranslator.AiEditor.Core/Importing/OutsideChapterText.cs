using System.Globalization;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Importing;

/// <summary>What a stretch of source text outside every chapter is.</summary>
public enum OutsideChapterKind
{
    /// <summary>Only whitespace.</summary>
    Blank,
    /// <summary>Separators, volume markers, book information, links and other notes from the source or the uploader.</summary>
    SourceNote,
    /// <summary>Real prose (a preface, an author's note, text cut off a chapter): kept in the snapshot, worth a look.</summary>
    Prose
}

/// <summary>
/// Classifies text that lies outside the detected chapters. Genre-neutral: it looks only at line shape (decoration, length),
/// generic TXT conventions (volume markers, book-information keys, links, end markers) and never at story content.
/// Nothing here imports or drops text; it only decides how loudly the preview should mention it.
/// </summary>
public static class OutsideChapterText
{
    /// <summary>Non-note text shorter than this (in characters, whitespace excluded) is still treated as a note.</summary>
    public const int ProseThreshold = 40;

    private static readonly string[] InfoKeys =
    [
        "书名", "書名", "作者", "作品", "简介", "簡介", "内容简介", "內容簡介", "文案", "标签", "標籤", "类型", "類型", "分类", "分類",
        "状态", "狀態", "字数", "字數", "更新", "来源", "來源", "主角", "配角", "其它", "其他", "出版", "版权", "版權", "声明", "聲明",
        "本书", "本書", "仅供", "僅供", "下载", "下載", "整理", "制作", "製作", "收藏", "推荐", "推薦", "求票", "公众号", "公眾號", "微信",
        "QQ", "群号", "群號", "书友", "書友", "封面", "Tags", "tags", "Author", "author", "Title", "title"
    ];

    private static readonly Regex VolumeMarker = new(
        @"^(正文|正文卷|作品相关|作品相關|番外|番外篇|外传|外傳|VIP卷|VIP章节|VIP章節|上架感言|完本感言|新书|新書|楔子|序言|序|引子|前言|后记|後記|尾声|尾聲)$|" +
        @"^(第\s*[0-9０-９一二三四五六七八九十百千零〇两兩]+\s*[卷部集篇册冊])|^卷\s*[0-9０-９一二三四五六七八九十百千零〇]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex EndMarker = new(@"^[（(【\[]?\s*(全文完|全书完|全書完|本章完|完结|完結|完|未完待续|未完待續|待续|待續)\s*[）)】\]]?[！!。.]*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex Link = new(@"(https?://|www\.|\.com\b|\.net\b|\.org\b|\.cc\b|\.cn\b|@[\w.]+|t\.me/)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static OutsideChapterKind Classify(string text)
    {
        int prose = 0;
        bool any = false;
        foreach (string raw in text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim().Trim('　', '﻿');
            if (line.Length == 0) continue;
            any = true;
            if (!IsNoteLine(line)) prose += line.Count(c => !char.IsWhiteSpace(c));
        }
        if (!any) return OutsideChapterKind.Blank;
        return prose >= ProseThreshold ? OutsideChapterKind.Prose : OutsideChapterKind.SourceNote;
    }

    /// <summary>Count of characters in the prose lines (what the preview reports for a <see cref="OutsideChapterKind.Prose"/> region).</summary>
    public static int ProseLength(string text) => text.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
        .Select(raw => raw.Trim().Trim('　', '﻿')).Where(line => line.Length > 0 && !IsNoteLine(line))
        .Sum(line => line.Count(c => !char.IsWhiteSpace(c)));

    internal static bool IsNoteLine(string line)
    {
        if (line.All(c => char.IsWhiteSpace(c) || IsDecoration(c))) return true;          // ------------  ＊＊＊  ……  ~~~
        if (VolumeMarker.IsMatch(line) && line.Length <= 40) return true;               // 正文卷 / 第一卷 xxx / 番外
        if (EndMarker.IsMatch(line)) return true;                                       // （全文完）/ 未完待续
        if (Link.IsMatch(line) && line.Length <= 120) return true;                      // ads, uploader handles
        if (line.StartsWith('《') && line.Contains('》') && line.Length <= 60) return true; // 《书名》 作者：xxx
        foreach (string key in InfoKeys)
            if (line.StartsWith(key, StringComparison.Ordinal) && line.Length <= 200 &&
                (line.Length == key.Length || IsKeySeparator(line[key.Length]))) return true;
        return false;
    }

    private static bool IsKeySeparator(char c) => c is ':' or '：' or ' ' or '　' or '】' or ']' or '|' or '丨' || IsDecoration(c);

    private static bool IsDecoration(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is
        UnicodeCategory.DashPunctuation or UnicodeCategory.OtherPunctuation or UnicodeCategory.MathSymbol or
        UnicodeCategory.OtherSymbol or UnicodeCategory.ModifierSymbol or UnicodeCategory.ConnectorPunctuation or
        UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation or
        UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.CurrencySymbol;
}
