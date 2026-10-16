using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.TextCleaning;

public enum ChineseRetranslationMode
{
    EntireSegment,
    ResidualHanOnly
}

public enum ChineseRetranslationMethod
{
    Ollama,
    VietPhrase,
    HanViet
}

/// <summary>
/// Converts one fresh Chinese-text scan result and a reviewed translation into the
/// existing immutable Batch Replace preview. Applying it therefore reuses revision,
/// approval, draft-history and undo guards instead of creating a second writer.
/// </summary>
public static class ChineseRetranslationPreviewBuilder
{
    public static IReadOnlyList<ChineseTextOccurrence> PlanTargets(IEnumerable<ChapterDocument> chapters,
        IEnumerable<ChineseTextOccurrence> occurrences, ChineseRetranslationMode mode)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(occurrences);
        ChapterDocument[] chapterList = chapters.ToArray();
        var chapterOrder = chapterList.Select((chapter, index) => (chapter.Id, index))
            .ToDictionary(item => item.Id, item => item.index);
        var segmentOrder = chapterList.SelectMany(chapter => chapter.Segments.Select(segment =>
                (Key: (chapter.Id, segment.Id), segment.Order)))
            .ToDictionary(item => item.Key, item => item.Order);
        ChineseTextOccurrence[] selected = occurrences
            .DistinctBy(item => (item.ChapterId, item.SegmentId, item.StartUtf16, item.LengthUtf16))
            .ToArray();
        if (selected.Any(item => !chapterOrder.ContainsKey(item.ChapterId)))
            throw new InvalidOperationException("Có kết quả quét thuộc chapter không còn trong phạm vi; hãy quét lại.");
        if (selected.Any(item => !segmentOrder.ContainsKey((item.ChapterId, item.SegmentId))))
            throw new InvalidOperationException("Có kết quả quét thuộc segment không còn trong chapter; hãy quét lại.");
        if (mode == ChineseRetranslationMode.EntireSegment)
            selected = selected.GroupBy(item => (item.ChapterId, item.SegmentId))
                .Select(group => group.OrderBy(item => item.StartUtf16).First()).ToArray();
        return selected.OrderBy(item => chapterOrder[item.ChapterId])
            .ThenBy(item => segmentOrder[(item.ChapterId, item.SegmentId)])
            .ThenBy(item => item.StartUtf16).ToArray();
    }

    public static BatchReplacementPreviewItem Build(ChapterDocument chapter, ChineseTextOccurrence occurrence,
        ChineseRetranslationMode mode, ChineseRetranslationMethod method, string translatedText)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(occurrence);
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(method))
            throw new ArgumentOutOfRangeException(nameof(mode));
        string replacement = translatedText?.Trim() ?? "";
        if (replacement.Length == 0)
            throw new InvalidDataException("Kết quả dịch lại đang trống; chưa tạo preview.");
        if (replacement.Length > 1_000_000)
            throw new InvalidDataException("Kết quả dịch lại vượt giới hạn an toàn.");
        if (chapter.Id != occurrence.ChapterId)
            throw new InvalidOperationException("Kết quả quét thuộc chapter khác; hãy quét lại.");
        TranslationSegment segment = chapter.Segments.SingleOrDefault(item => item.Id == occurrence.SegmentId)
            ?? throw new InvalidOperationException("Segment đã thay đổi sau lần quét; hãy quét lại.");
        string before = segment.EditableText;
        if (occurrence.StartUtf16 < 0 || occurrence.LengthUtf16 <= 0 ||
            occurrence.StartUtf16 > before.Length - occurrence.LengthUtf16 ||
            !before.AsSpan(occurrence.StartUtf16, occurrence.LengthUtf16).SequenceEqual(occurrence.Text))
            throw new InvalidOperationException("Nội dung hoặc vị trí chữ Hán đã đổi sau lần quét; hãy quét lại.");

        int start = mode == ChineseRetranslationMode.EntireSegment ? 0 : occurrence.StartUtf16;
        int length = mode == ChineseRetranslationMode.EntireSegment ? before.Length : occurrence.LengthUtf16;
        string original = before.Substring(start, length);
        if (string.Equals(original, replacement, StringComparison.Ordinal))
            throw new InvalidDataException("Kết quả dịch lại không làm thay đổi nội dung; chưa tạo preview.");
        string after = string.Concat(before.AsSpan(0, start), replacement, before.AsSpan(start + length));
        string source = method switch
        {
            ChineseRetranslationMethod.Ollama => "Dịch lại bằng Ollama",
            ChineseRetranslationMethod.VietPhrase => "Dịch lại bằng VietPhrase",
            _ => "Dịch lại bằng Hán Việt"
        };
        source += mode == ChineseRetranslationMode.EntireSegment ? " · toàn bộ đoạn" : " · chữ Hán còn sót";

        return new BatchReplacementPreviewItem
        {
            ChapterId = chapter.Id,
            CharacterProjectId = chapter.CharacterProject.Id,
            ChapterTitle = chapter.Title,
            SegmentId = segment.Id,
            SegmentOrder = segment.Order,
            StartUtf16 = start,
            LengthUtf16 = length,
            Original = original,
            Replacement = replacement,
            Source = ReplacementRuleSource.Manual,
            RuleConfirmed = true,
            SourceLabel = source,
            BeforeSentence = mode == ChineseRetranslationMode.EntireSegment
                ? Excerpt(before) : ChineseTextScanner.SentenceAround(before, start, length),
            AfterSentence = mode == ChineseRetranslationMode.EntireSegment
                ? Excerpt(after) : ChineseTextScanner.SentenceAround(after, start, replacement.Length),
            AnalysedText = before,
            DraftRevision = segment.DraftRevision,
            ApprovalVersion = segment.Approvals.Count,
            LastApprovalId = segment.Approvals.LastOrDefault()?.Id,
            SegmentStatus = segment.Status,
            Protected = segment.IsProtectedFromAutomaticTranslation,
            Conflict = false
        };
    }

    private static string Excerpt(string text)
    {
        const int limit = 360;
        if (text.Length <= limit) return text;
        return text[..limit] + "…";
    }
}

