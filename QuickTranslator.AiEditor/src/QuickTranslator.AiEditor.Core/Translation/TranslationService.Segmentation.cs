using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Segmentation;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class SegmentationConfirmationRequiredException() : InvalidOperationException(
    "Phần này đã có bản dịch/nháp/lịch sử. Đổi ranh giới sẽ lưu bản cũ trong lịch sử và đánh dấu các đoạn bị ảnh hưởng cần xử lý lại. Bạn phải xác nhận trước.");

public sealed partial class TranslationService
{
    public Task SplitSegmentAsync(Guid segmentId, int relativeBoundary, bool confirmed = false, CancellationToken cancellationToken = default) =>
        ReshapeAsync(segmentId, 1, relativeBoundary, "Tách đoạn", confirmed, cancellationToken);

    public Task MergeWithNextAsync(Guid segmentId, bool confirmed = false, CancellationToken cancellationToken = default) =>
        ReshapeAsync(segmentId, 2, null, "Gộp hai đoạn", confirmed, cancellationToken);

    public Task MoveBoundaryAsync(Guid leftSegmentId, int relativeBoundary, bool confirmed = false, CancellationToken cancellationToken = default) =>
        ReshapeAsync(leftSegmentId, 2, relativeBoundary, "Đổi ranh giới hai đoạn", confirmed, cancellationToken);

    private async Task ReshapeAsync(Guid firstId, int count, int? boundary, string description, bool confirmed, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            EnsureStructureAvailable();
            int index = _chapter.Segments.FindIndex(s => s.Id == firstId);
            if (index < 0 || index + count > _chapter.Segments.Count) throw new ArgumentException("Không đủ hai đoạn liền kề để thay đổi.");
            var previous = DocumentJson.Clone(_chapter).Segments.Skip(index).Take(count).ToList();
            string source = string.Concat(previous.Select(s => s.SourceText));
            if (boundary is int cut && !TextSegmenter.GetSentenceBoundaries(source).Contains(cut))
                throw new ArgumentException("Chỉ tách/đổi ranh giới tại cuối câu, lời thoại hoặc đoạn văn hoàn chỉnh.");
            if (count == 2 && boundary == previous[0].SourceText.Length) return;
            bool hasWork = previous.Any(s => s.Attempts.Count > 0 || s.UserDraft is not null || s.Approvals.Count > 0 || s.PreservedText is not null || s.ProtectedByPreviousEdit);
            if (hasWork && !confirmed) throw new SegmentationConfirmationRequiredException();
            string[] parts = boundary is int position ? [source[..position], source[position..]] : [source];
            if (parts.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Ranh giới không được tạo đoạn chỉ có khoảng trắng/xuống dòng.");
            int offset = previous[0].StartUtf16;
            bool protectedEdit = previous.Any(s => s.IsProtectedFromAutomaticTranslation);
            var replacements = new List<TranslationSegment>();
            foreach (string text in parts)
            {
                replacements.Add(new()
                {
                    Id = TextSegmenter.CreateStableId(_chapter.Id, offset, text), StartUtf16 = offset, SourceText = text,
                    HanCharacterCount = TextSegmenter.CountHanCharacters(text), RequiresRetranslation = hasWork,
                    ProtectedByPreviousEdit = protectedEdit,
                    Status = hasWork ? SegmentStatus.InspectionRequired : SegmentStatus.Untranslated
                });
                offset += text.Length;
            }
            if (parts.Length == 1 && previous.Any(s => !string.IsNullOrWhiteSpace(s.EditableText)))
            {
                string combined = ChapterTranslationComposer.Join(previous.Select(s => s.EditableText));
                if (protectedEdit) replacements[0].UserDraft = combined;
                else replacements[0].PreservedText = combined;
            }
            // A translated split has no reliable target-text alignment. Keep all old text/history in the archive.
            ReplaceRange(index, count, replacements, previous, description);
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreLastSegmentationAsync(bool confirmed = false, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            EnsureStructureAvailable();
            var change = _chapter.SegmentationHistory.LastOrDefault() ?? throw new InvalidOperationException("Chưa có thay đổi phân đoạn để khôi phục.");
            int index = _chapter.Segments.FindIndex(s => s.Id == change.ReplacementIds[0]);
            if (index < 0 || !_chapter.Segments.Skip(index).Take(change.ReplacementIds.Count).Select(s => s.Id).SequenceEqual(change.ReplacementIds))
                throw new InvalidOperationException("Các đoạn đã thay đổi tiếp; không thể khôi phục ranh giới này trực tiếp.");
            if (!confirmed) throw new SegmentationConfirmationRequiredException();
            var clone = DocumentJson.Clone(_chapter);
            var current = clone.Segments.Skip(index).Take(change.ReplacementIds.Count).ToList();
            var restored = clone.SegmentationHistory.Last().PreviousSegments;
            ReplaceRange(index, current.Count, restored, current, "Khôi phục ranh giới trước đó");
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ReplaceRange(int index, int count, List<TranslationSegment> replacements, List<TranslationSegment> previous, string description)
    {
        _chapter.SegmentationHistory.Add(new()
        {
            ChangedAt = _clock.GetUtcNow(), Description = description, PreviousSegments = previous,
            ReplacementIds = replacements.Select(s => s.Id).ToList(),
            PreviousQueue = DocumentJson.Clone(_chapter).Queue
        });
        _chapter.Segments.RemoveRange(index, count);
        _chapter.Segments.InsertRange(index, replacements);
        for (int i = 0; i < _chapter.Segments.Count; i++) _chapter.Segments[i].Order = i + 1;
        _chapter.SelectedSegmentId = replacements[0].Id;
        _chapter.Queue = new QueueCheckpoint();
        Touch();
        DocumentJson.Validate(_chapter);
    }
}
