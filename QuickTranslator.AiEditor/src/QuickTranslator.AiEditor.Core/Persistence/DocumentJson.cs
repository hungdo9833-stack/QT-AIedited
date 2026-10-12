using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Segmentation;

namespace QuickTranslator.AiEditor.Core.Persistence;

public static class DocumentJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    public static ChapterDocument Clone(ChapterDocument chapter) =>
        JsonSerializer.Deserialize<ChapterDocument>(JsonSerializer.Serialize(chapter, Options), Options)!;

    public static void Migrate(ChapterDocument chapter)
    {
        if (chapter.SchemaVersion == 1)
            foreach (var segment in chapter.Segments)
                if (segment.Status == SegmentStatus.AwaitingReview && segment.LatestAiAttempt?.Issues.Count > 0)
                    segment.Status = SegmentStatus.InspectionRequired;
        if (chapter.SchemaVersion is 1 or 2)
        {
            chapter.SchemaVersion = 3;
            // Keep stored snapshots; initialize only fields absent in the older schema.
            chapter.CharacterProject ??= new() { Id = chapter.ProjectId ?? chapter.Id, UpdatedAt = chapter.UpdatedAt,
                Name = string.IsNullOrWhiteSpace(chapter.Title) ? "Bộ truyện mới" : chapter.Title };
            foreach (var segment in chapter.Segments) segment.NormalizationHistory ??= [];
        }
        chapter.TextCleaner ??= new();
        chapter.TextCleaner.IgnoredChineseText ??= [];
        chapter.BatchReplacementHistory ??= [];
        foreach (var segment in chapter.Segments)
        {
            segment.DraftHistory ??= [];
            segment.NormalizationHistory ??= [];
        }
    }

    public static void Validate(ChapterDocument chapter)
    {
        if (chapter.SchemaVersion is not (1 or 2 or 3))
            throw new InvalidDataException("Phiên bản dữ liệu chưa được hỗ trợ. Hãy giữ bản gốc và mở bằng phiên bản ứng dụng phù hợp.");
        if (chapter.Id == Guid.Empty || chapter.Title is null || string.IsNullOrWhiteSpace(chapter.OriginalText) ||
            chapter.Segments is null || chapter.Segments.Count == 0 || chapter.SegmentationOptions is null || chapter.DefaultModelOptions is null)
            throw new InvalidDataException("Dữ liệu chương không đầy đủ.");
        if (chapter.ProjectId == Guid.Empty || chapter.ChapterNumber is <= 0)
            throw new InvalidDataException("Liên kết dự án hoặc số chương không hợp lệ.");
        chapter.SegmentationOptions.Validate();
        chapter.DefaultModelOptions.Validate();
        if (chapter.SelectedPreset is null || chapter.Queue is null || chapter.SegmentationHistory is null ||
            chapter.ConfirmedTerms is null || chapter.ConfirmedContextNotes is null || chapter.CharacterProject is null ||
            chapter.TextCleaner is null || chapter.TextCleaner.IgnoredChineseText is null || chapter.BatchReplacementHistory is null)
            throw new InvalidDataException("Thiếu preset, hàng đợi hoặc lịch sử phân đoạn.");
        chapter.CharacterProject.Validate();
        chapter.SelectedPreset.Validate();
        if (chapter.Queue.Preset is null || chapter.Queue.ModelOptions is null) throw new InvalidDataException("Thiếu cấu hình hàng đợi.");
        chapter.Queue.Preset.Validate();
        chapter.Queue.ModelOptions.Validate();
        if (!Enum.IsDefined(chapter.Queue.Status) || !Enum.IsDefined(chapter.Queue.Mode) ||
            chapter.Queue.CandidateIds is null || chapter.Queue.NextIndex < 0 ||
            chapter.Queue.NextIndex > chapter.Queue.CandidateIds.Count || chapter.Queue.Elapsed < TimeSpan.Zero ||
            chapter.Queue.CandidateIds.Distinct().Count() != chapter.Queue.CandidateIds.Count ||
            chapter.ConfirmedTerms.Any(t => t is null || string.IsNullOrWhiteSpace(t.Chinese) || string.IsNullOrWhiteSpace(t.Vietnamese)) ||
            chapter.ConfirmedTerms.Count > 1000 || chapter.ConfirmedContextNotes.Length > 4000 ||
            chapter.TextCleaner.IgnoredChineseText.Count > 2000 ||
            chapter.TextCleaner.IgnoredChineseText.Any(string.IsNullOrWhiteSpace) ||
            chapter.TextCleaner.IgnoredChineseText.Distinct(StringComparer.Ordinal).Count() != chapter.TextCleaner.IgnoredChineseText.Count)
            throw new InvalidDataException("Dữ liệu hàng đợi hoặc ngữ cảnh không hợp lệ.");
        int offset = 0, translating = 0;
        var ids = new HashSet<Guid>();
        var attemptIds = new HashSet<Guid>();
        for (int i = 0; i < chapter.Segments.Count; i++)
        {
            var segment = chapter.Segments[i];
            if (segment is null || segment.SourceText is null || segment.SourceText.Length == 0 ||
                segment.Order != i + 1 || segment.StartUtf16 != offset || !ids.Add(segment.Id) ||
                segment.Id != TextSegmenter.CreateStableId(chapter.Id, offset, segment.SourceText) ||
                !Enum.IsDefined(segment.Status) || segment.Attempts is null || segment.Approvals is null ||
                segment.DraftHistory is null || segment.NormalizationHistory is null || segment.DraftRevision < 0 ||
                segment.HanCharacterCount != TextSegmenter.CountHanCharacters(segment.SourceText))
                throw new InvalidDataException("Danh sách đoạn hoặc ID không hợp lệ.");
            offset += segment.SourceText.Length;
            if (segment.Status == SegmentStatus.Translating) translating++;
            foreach (var attempt in segment.Attempts)
            {
                if (attempt is null || attempt.Id == Guid.Empty || !attemptIds.Add(attempt.Id) ||
                    !Enum.IsDefined(attempt.Outcome) || attempt.ModelOptions is null || attempt.Prompt is null ||
                    attempt.PromptVersion is null || attempt.Issues is null || attempt.ProjectResourceRevision < 0)
                    throw new InvalidDataException("Lịch sử dịch không hợp lệ.");
                attempt.ModelOptions.Validate();
                attempt.PresetSnapshot?.Validate();
            }
            if (segment.Approvals.Any(a => a is null || string.IsNullOrWhiteSpace(a.Text)))
                throw new InvalidDataException("Lịch sử duyệt không hợp lệ.");
            foreach (var run in segment.NormalizationHistory)
                if (run is null || run.Id == Guid.Empty || run.BatchId == Guid.Empty || run.BeforeText is null ||
                    run.AfterText is null || run.Changes is null || run.Changes.Count == 0 ||
                    run.Changes.Any(c => c is null || c.StartUtf16 < 0 || c.Length <= 0 ||
                        string.IsNullOrEmpty(c.Original) || string.IsNullOrEmpty(c.Replacement) || c.CharacterId == Guid.Empty))
                    throw new InvalidDataException("Lịch sử chuẩn hóa xưng hô không hợp lệ.");
        }
        if (chapter.SelectedSegmentId is Guid selectedId && !ids.Contains(selectedId))
            throw new InvalidDataException("Đoạn đang chọn không thuộc chương.");
        if (chapter.Queue.CandidateIds.Any(id => !ids.Contains(id)) ||
            chapter.Queue.CurrentSegmentId is Guid current && !ids.Contains(current))
            throw new InvalidDataException("Hàng đợi tham chiếu đoạn không tồn tại.");
        var batchIds = new HashSet<Guid>();
        foreach (var run in chapter.BatchReplacementHistory)
        {
            if (run is null || run.Id == Guid.Empty || !batchIds.Add(run.Id) || !Enum.IsDefined(run.Scope) ||
                run.Description is null || run.Segments is null || run.Segments.Count == 0 ||
                run.Segments.Select(x => x.SegmentId).Distinct().Count() != run.Segments.Count)
                throw new InvalidDataException("Lịch sử thay thế hàng loạt không hợp lệ.");
            foreach (var revision in run.Segments)
            {
                if (revision is null || !ids.Contains(revision.SegmentId) || revision.SegmentOrder <= 0 ||
                    revision.BeforeText is null || revision.AfterText is null || revision.Changes is null ||
                    revision.Changes.Count == 0 || revision.PreviousDraftRevision < 0 || revision.AppliedDraftRevision < 0 ||
                    revision.AppliedApprovalVersion < 0 || revision.AppliedLastApprovalId == Guid.Empty ||
                    revision.AppliedApprovalVersion == 0 && revision.AppliedLastApprovalId is not null ||
                    revision.AppliedApprovalVersion > 0 && revision.AppliedLastApprovalId is null ||
                    !Enum.IsDefined(revision.PreviousStatus) || revision.Changes.Any(change =>
                        change is null || change.StartUtf16 < 0 || change.LengthUtf16 <= 0 ||
                        string.IsNullOrEmpty(change.Original) || change.Replacement is null ||
                        change.SourceLabel is null || !Enum.IsDefined(change.Source)))
                    throw new InvalidDataException("Revision thay thế hàng loạt không hợp lệ.");
            }
        }
        foreach (var change in chapter.SegmentationHistory)
        {
            if (change is null || change.PreviousSegments is null || change.PreviousSegments.Count == 0 ||
                change.ReplacementIds is null || change.ReplacementIds.Count == 0)
                throw new InvalidDataException("Lịch sử thay đổi đoạn không hợp lệ.");
            int previousOffset = change.PreviousSegments[0].StartUtf16;
            if (previousOffset < 0) throw new InvalidDataException("Vị trí lưu trữ không hợp lệ.");
            foreach (var previous in change.PreviousSegments)
            {
                if (previous.SourceText is null || previous.StartUtf16 != previousOffset ||
                    previous.Id != TextSegmenter.CreateStableId(chapter.Id, previousOffset, previous.SourceText) ||
                    previous.Attempts is null || previous.Approvals is null || previous.DraftHistory is null)
                    throw new InvalidDataException("Bản trước khi thay đổi đoạn không hợp lệ.");
                previousOffset += previous.SourceText.Length;
            }
            string archivedSource = string.Concat(change.PreviousSegments.Select(s => s.SourceText));
            if (previousOffset > chapter.OriginalText.Length || !chapter.OriginalText.AsSpan(change.PreviousSegments[0].StartUtf16, archivedSource.Length).SequenceEqual(archivedSource))
                throw new InvalidDataException("Lịch sử phân đoạn không khớp nguyên văn.");
        }
        if (translating > 1 || !string.Equals(TextSegmenter.Reconstruct(chapter.Segments), chapter.OriginalText, StringComparison.Ordinal))
            throw new InvalidDataException("Các đoạn không khôi phục đúng nguyên văn hoặc có nhiều đoạn đang dịch.");
    }
}
