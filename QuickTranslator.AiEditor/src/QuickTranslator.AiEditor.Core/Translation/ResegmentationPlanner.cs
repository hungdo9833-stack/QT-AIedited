using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Segmentation;

namespace QuickTranslator.AiEditor.Core.Translation;

public static class ResegmentationPlanner
{
    public static ResegmentationPreview Preview(ChapterDocument chapter, SegmenterOptions options,
        bool allowRecoverableCheckpoint = false)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        EnsureQueueIdle(chapter, allowRecoverableCheckpoint);
        Build(chapter, options, out List<TranslationSegment> proposed, out int retained,
            out int protectedCount, out int regions, out List<string> warnings);
        bool changed = !chapter.Segments.Select(segment => segment.Id).SequenceEqual(proposed.Select(segment => segment.Id));
        return new(DocumentHash(chapter), options with { }, chapter.Segments.Count, proposed.Count,
            retained, protectedCount, regions, changed, warnings.AsReadOnly());
    }

    public static ChapterDocument Apply(ChapterDocument chapter, ResegmentationPreview preview,
        bool confirmed, DateTimeOffset changedAt, bool allowRecoverableCheckpoint = false)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(preview);
        EnsureQueueIdle(chapter, allowRecoverableCheckpoint);
        if (!string.Equals(DocumentHash(chapter), preview.ExpectedDocumentSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("Chương đã thay đổi sau khi tạo preview chia lại đoạn. Hãy xem trước lại.");
        if (preview.ProtectedSegmentCount > 0 && !confirmed)
            throw new SegmentationConfirmationRequiredException();
        Build(chapter, preview.Options, out List<TranslationSegment> proposed, out _, out _, out _, out _);
        ChapterDocument candidate = DocumentJson.Clone(chapter);
        if (preview.BoundariesChanged)
        {
            ChapterDocument archive = DocumentJson.Clone(chapter);
            candidate.SegmentationHistory.Add(new()
            {
                ChangedAt = changedAt,
                Description = $"Chia lại đoạn: {chapter.Segments.Count:N0} → {proposed.Count:N0}",
                PreviousSegments = archive.Segments,
                ReplacementIds = proposed.Select(segment => segment.Id).ToList(),
                PreviousQueue = archive.Queue
            });
            candidate.Segments = proposed;
            candidate.SelectedSegmentId = proposed.FirstOrDefault()?.Id;
            candidate.Queue = new QueueCheckpoint();
        }
        candidate.SegmentationOptions = preview.Options with { };
        candidate.UpdatedAt = changedAt;
        DocumentJson.Validate(candidate);
        return candidate;
    }

    private static void Build(ChapterDocument chapter, SegmenterOptions options,
        out List<TranslationSegment> proposed, out int retained, out int protectedCount,
        out int regionCount, out List<string> warnings)
    {
        proposed = [];
        warnings = [];
        retained = 0;
        protectedCount = 0;
        regionCount = 0;
        for (int index = 0; index < chapter.Segments.Count;)
        {
            TranslationSegment current = chapter.Segments[index];
            if (HasProtectedData(current))
            {
                TranslationSegment anchor = DocumentJson.CloneSegment(current);
                proposed.Add(anchor);
                retained++;
                protectedCount++;
                if (anchor.HanCharacterCount > options.MaximumHanCharacters ||
                    anchor.SourceText.EnumerateRunes().Count() > options.MaximumTextElements)
                    warnings.Add($"Đoạn {anchor.Order} có dữ liệu được bảo vệ và vượt ngưỡng mới; giữ nguyên ranh giới.");
                index++;
                continue;
            }

            int startIndex = index;
            while (index < chapter.Segments.Count && !HasProtectedData(chapter.Segments[index])) index++;
            TranslationSegment[] oldRegion = chapter.Segments.Skip(startIndex).Take(index - startIndex).ToArray();
            string source = string.Concat(oldRegion.Select(segment => segment.SourceText));
            int baseOffset = oldRegion[0].StartUtf16;
            IReadOnlyList<TranslationSegment> detected = new TextSegmenter().Segment(source, chapter.Id, options);
            regionCount++;
            foreach (TranslationSegment raw in detected)
            {
                int absoluteStart = baseOffset + raw.StartUtf16;
                TranslationSegment? exact = oldRegion.SingleOrDefault(old => old.StartUtf16 == absoluteStart &&
                    string.Equals(old.SourceText, raw.SourceText, StringComparison.Ordinal));
                if (exact is not null)
                {
                    proposed.Add(DocumentJson.CloneSegment(exact));
                    retained++;
                }
                else
                {
                    raw.StartUtf16 = absoluteStart;
                    raw.Id = TextSegmenter.CreateStableId(chapter.Id, absoluteStart, raw.SourceText);
                    proposed.Add(raw);
                }
            }
        }
        for (int index = 0; index < proposed.Count; index++) proposed[index].Order = index + 1;
        if (proposed.Count == 0 || !string.Equals(TextSegmenter.Reconstruct(proposed), chapter.OriginalText,
                StringComparison.Ordinal))
            throw new InvalidDataException("Kế hoạch chia lại đoạn không tái tạo chính xác nguyên văn.");
    }

    internal static bool HasProtectedData(TranslationSegment segment) => segment.Attempts.Count > 0 ||
        segment.UserDraft is not null || segment.Approvals.Count > 0 || segment.DraftHistory.Count > 0 ||
        segment.PreservedText is not null || segment.ProtectedByPreviousEdit || segment.NormalizationHistory.Count > 0;

    private static void EnsureQueueIdle(ChapterDocument chapter, bool allowRecoverableCheckpoint)
    {
        if (chapter.Queue.IsActive || !allowRecoverableCheckpoint &&
            chapter.Queue.Status is QueueStatus.Paused or QueueStatus.Interrupted)
            throw new InvalidOperationException("Queue của chương chưa ở trạng thái an toàn. Hãy hoàn tất hoặc hủy checkpoint trước khi chia lại đoạn.");
    }

    private static string DocumentHash(ChapterDocument chapter) =>
        Convert.ToHexString(SHA256.HashData(ChapterFileStore.SerializeDocument(chapter)));
}
