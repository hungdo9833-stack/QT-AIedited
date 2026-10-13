using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Segmentation;

namespace QuickTranslator.AiEditor.Core.Translation;

public static class SourceEditPlanner
{
    public static SourceEditPreview Preview(ChapterDocument chapter, string newSource,
        SegmenterOptions? segmentation = null)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(newSource);
        if (string.IsNullOrWhiteSpace(newSource)) throw new ArgumentException("Nguyên văn mới không được để trống.", nameof(newSource));
        if (string.Equals(chapter.OriginalText, newSource, StringComparison.Ordinal))
            throw new InvalidOperationException("Nguyên văn không có thay đổi để lưu.");
        segmentation ??= chapter.SegmentationOptions;
        segmentation.Validate();
        SourceTextChange change = Diff(chapter.OriginalText, newSource);
        BuildSegments(chapter, newSource, segmentation, change, out List<TranslationSegment> proposed,
            out int retained, out List<TranslationSegment> affected);
        int changedExtent = Math.Max(change.OldLengthUtf16, change.NewLengthUtf16);
        bool most = changedExtent >= Math.Max(1, Math.Max(chapter.OriginalText.Length, newSource.Length) / 2);
        return new(chapter.SourceRevision, Hash(chapter.OriginalText), Hash(newSource), newSource,
            segmentation with { }, change, chapter.Segments.Count, proposed.Count, retained,
            affected.Count, affected.Count(HasProtectedData), most);
    }

    public static ChapterDocument Apply(ChapterDocument chapter, SourceEditPreview preview,
        bool confirmed, DateTimeOffset changedAt)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(preview);
        if (chapter.SourceRevision != preview.ExpectedSourceRevision ||
            !string.Equals(Hash(chapter.OriginalText), preview.ExpectedSourceSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Nguyên văn đã đổi sau khi mở preview. Hãy tạo preview mới.");
        if (!string.Equals(Hash(preview.NewSourceText), preview.NewSourceSha256, StringComparison.Ordinal))
            throw new InvalidDataException("Preview nguyên văn không còn khớp nội dung đã xác nhận.");
        if ((preview.ProtectedAffectedSegmentCount > 0 || preview.ReplacesMostOfSource) && !confirmed)
            throw new InvalidOperationException("Thay đổi lớn hoặc chạm dữ liệu đã dịch/duyệt cần xác nhận riêng.");

        ChapterDocument candidate = DocumentJson.Clone(chapter);
        BuildSegments(candidate, preview.NewSourceText, preview.SegmentationOptions, preview.Change,
            out List<TranslationSegment> replacements, out _, out List<TranslationSegment> affected);
        candidate.SourceEditHistory.Add(new()
        {
            ChangedAt = changedAt,
            PreviousSourceRevision = candidate.SourceRevision,
            Description = Describe(preview.Change),
            PreviousOriginalText = candidate.OriginalText,
            PreviousSegments = DocumentJson.Clone(candidate).Segments,
            PreviousSegmentationHistory = DocumentJson.Clone(candidate).SegmentationHistory,
            PreviousQueue = DocumentJson.Clone(candidate).Queue,
            PreviousSourceSha256 = preview.ExpectedSourceSha256,
            NewSourceSha256 = preview.NewSourceSha256
        });
        bool protectedChange = affected.Any(HasProtectedData);
        foreach (TranslationSegment segment in replacements.Where(segment => !HasProtectedData(segment) &&
                     !candidate.Segments.Any(old => IsUnaffectedMapping(old, segment, preview.Change))))
        {
            if (!protectedChange) continue;
            segment.RequiresRetranslation = true;
            segment.ProtectedByPreviousEdit = true;
            segment.Status = SegmentStatus.InspectionRequired;
        }
        candidate.OriginalText = preview.NewSourceText;
        candidate.Segments = replacements;
        candidate.SegmentationOptions = preview.SegmentationOptions with { };
        candidate.SegmentationHistory = [];
        candidate.SourceRevision++;
        candidate.SelectedSegmentId = replacements.FirstOrDefault()?.Id;
        candidate.Queue = new QueueCheckpoint();
        candidate.UpdatedAt = changedAt;
        DocumentJson.Validate(candidate);
        return candidate;
    }

    private static void BuildSegments(ChapterDocument chapter, string newSource, SegmenterOptions segmentation,
        SourceTextChange change, out List<TranslationSegment> result, out int retained,
        out List<TranslationSegment> affected)
    {
        int oldEnd = change.OldStartUtf16 + change.OldLengthUtf16;
        int delta = change.NewLengthUtf16 - change.OldLengthUtf16;
        List<TranslationSegment> prefix = chapter.Segments.Where(segment => End(segment) <= change.OldStartUtf16).ToList();
        List<TranslationSegment> suffix = chapter.Segments.Where(segment => segment.StartUtf16 >= oldEnd).ToList();
        if (change.OldLengthUtf16 == 0)
        {
            // An insertion inside a segment changes that segment. An insertion exactly at a
            // verified boundary can retain both neighboring segments.
            TranslationSegment? containing = chapter.Segments.FirstOrDefault(segment =>
                segment.StartUtf16 < change.OldStartUtf16 && End(segment) > change.OldStartUtf16);
            if (containing is not null)
            {
                prefix.RemoveAll(segment => segment.Order >= containing.Order);
                suffix.RemoveAll(segment => segment.Order <= containing.Order);
            }
        }
        int prefixEnd = prefix.LastOrDefault() is { } lastPrefix ? End(lastPrefix) : 0;
        int suffixOldStart = suffix.FirstOrDefault()?.StartUtf16 ?? chapter.OriginalText.Length;
        int suffixNewStart = suffixOldStart + delta;
        if (prefixEnd < 0 || suffixNewStart < prefixEnd || suffixNewStart > newSource.Length)
            throw new InvalidDataException("Không ánh xạ được source spans sau thay đổi.");
        if (!newSource.AsSpan(0, prefixEnd).SequenceEqual(chapter.OriginalText.AsSpan(0, prefixEnd)) ||
            !newSource.AsSpan(suffixNewStart).SequenceEqual(chapter.OriginalText.AsSpan(suffixOldStart)))
            throw new InvalidDataException("Vùng nguyên văn được giữ không khớp preview.");

        affected = chapter.Segments.Except(prefix).Except(suffix).ToList();
        result = prefix.Select(DocumentJson.CloneSegment).ToList();
        int middleRetained = 0;
        string middleText = newSource[prefixEnd..suffixNewStart];
        if (middleText.Length > 0)
        {
            IReadOnlyList<TranslationSegment> middle = new TextSegmenter().Segment(middleText, chapter.Id, segmentation);
            foreach (TranslationSegment detected in middle)
            {
                TranslationSegment segment = detected;
                segment.StartUtf16 += prefixEnd;
                segment.Id = TextSegmenter.CreateStableId(chapter.Id, segment.StartUtf16, segment.SourceText);
                TranslationSegment[] exact = affected.Where(old =>
                    string.Equals(old.SourceText, segment.SourceText, StringComparison.Ordinal)).ToArray();
                if (exact.Length == 1)
                {
                    segment = CopyPreserved(exact[0], segment.StartUtf16, chapter.Id);
                    middleRetained++;
                }
                result.Add(segment);
            }
        }
        foreach (TranslationSegment old in suffix)
            result.Add(CopyPreserved(old, old.StartUtf16 + delta, chapter.Id));
        for (int index = 0; index < result.Count; index++) result[index].Order = index + 1;
        retained = prefix.Count + suffix.Count + middleRetained;
        if (result.Count == 0 || !string.Equals(TextSegmenter.Reconstruct(result), newSource, StringComparison.Ordinal))
            throw new InvalidDataException("Kế hoạch sửa nguồn không tái tạo chính xác nguyên văn mới.");
    }

    private static TranslationSegment CopyPreserved(TranslationSegment old, int newStart, Guid chapterId)
    {
        TranslationSegment copy = DocumentJson.CloneSegment(old);
        copy.StartUtf16 = newStart;
        copy.Id = TextSegmenter.CreateStableId(chapterId, newStart, copy.SourceText);
        return copy;
    }

    private static bool IsUnaffectedMapping(TranslationSegment old, TranslationSegment proposed, SourceTextChange change)
    {
        if (!string.Equals(old.SourceText, proposed.SourceText, StringComparison.Ordinal)) return false;
        int oldEnd = change.OldStartUtf16 + change.OldLengthUtf16;
        int delta = change.NewLengthUtf16 - change.OldLengthUtf16;
        return End(old) <= change.OldStartUtf16 && proposed.StartUtf16 == old.StartUtf16 ||
               old.StartUtf16 >= oldEnd && proposed.StartUtf16 == old.StartUtf16 + delta;
    }

    private static bool HasProtectedData(TranslationSegment segment) => segment.Attempts.Count > 0 ||
        segment.UserDraft is not null || segment.Approvals.Count > 0 || segment.DraftHistory.Count > 0 ||
        segment.PreservedText is not null || segment.ProtectedByPreviousEdit || segment.NormalizationHistory.Count > 0;

    private static SourceTextChange Diff(string oldText, string newText)
    {
        int prefix = 0;
        while (prefix < oldText.Length && prefix < newText.Length && oldText[prefix] == newText[prefix]) prefix++;
        int suffix = 0;
        while (suffix < oldText.Length - prefix && suffix < newText.Length - prefix &&
               oldText[^(suffix + 1)] == newText[^(suffix + 1)]) suffix++;
        int oldLength = oldText.Length - prefix - suffix;
        int newLength = newText.Length - prefix - suffix;
        return new(prefix, oldLength, prefix, newLength,
            oldText.Substring(prefix, oldLength), newText.Substring(prefix, newLength));
    }

    private static int End(TranslationSegment segment) => segment.StartUtf16 + segment.SourceText.Length;
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Describe(SourceTextChange change) =>
        $"Sửa nguyên văn: -{change.OldLengthUtf16:N0} / +{change.NewLengthUtf16:N0} UTF-16 tại {change.OldStartUtf16:N0}";
}
