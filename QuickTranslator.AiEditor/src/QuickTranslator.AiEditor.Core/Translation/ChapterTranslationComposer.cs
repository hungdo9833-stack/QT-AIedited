using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed record TranslationSpan(Guid SegmentId, int Order, int Start, int Length);
public sealed record ComposedChapter(string Text, IReadOnlyList<TranslationSpan> Spans,
    IReadOnlyList<Guid> MissingIds, IReadOnlyList<Guid> ErrorIds, IReadOnlyList<Guid> InspectionIds)
{
    public bool IsComplete => MissingIds.Count == 0 && ErrorIds.Count == 0;
}

public static class ChapterTranslationComposer
{
    public static string Join(IEnumerable<string> texts) => string.Join("\n\n", texts);

    public static ComposedChapter Compose(ChapterDocument chapter)
    {
        var text = new StringBuilder();
        var spans = new List<TranslationSpan>();
        var missing = new List<Guid>();
        var errors = new List<Guid>();
        var inspection = new List<Guid>();
        foreach (var segment in chapter.Segments.OrderBy(s => s.Order))
        {
            if (spans.Count > 0) text.Append("\n\n");
            string translated = segment.EditableText;
            spans.Add(new(segment.Id, segment.Order, text.Length, translated.Length));
            text.Append(translated);
            if (string.IsNullOrWhiteSpace(translated) || segment.RequiresRetranslation) missing.Add(segment.Id);
            if (segment.Status == SegmentStatus.Error) errors.Add(segment.Id);
            if (segment.Status == SegmentStatus.InspectionRequired || segment.RequiresRetranslation) inspection.Add(segment.Id);
        }
        return new(text.ToString(), spans, missing, errors, inspection);
    }

    public static async Task ExportAsync(ChapterDocument chapter, string path, bool allowIncomplete = false, CancellationToken cancellationToken = default)
    {
        var composed = Compose(chapter);
        if (!composed.IsComplete && !allowIncomplete)
            throw new InvalidOperationException("Còn đoạn lỗi/chưa có bản dịch hợp lệ. Chỉ xuất bản nháp nếu bạn xác nhận rõ.");
        await AtomicLocalFile.WriteAsync(path, Encoding.UTF8.GetBytes(composed.Text), cancellationToken).ConfigureAwait(false);
    }
}
