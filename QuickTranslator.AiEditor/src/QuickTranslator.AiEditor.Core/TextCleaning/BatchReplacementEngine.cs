using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.TextCleaning;

public sealed class BatchReplacementEngine
{
    public IReadOnlyList<BatchReplacementPreviewItem> Preview(IEnumerable<ChapterDocument> chapters,
        IEnumerable<BatchReplacementRule> rules, BatchReplacementScope scope, Guid? selectedSegmentId = null)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(rules);
        var chapterList = chapters.ToArray();
        if (chapterList.Length == 0) return [];
        Guid projectId = chapterList[0].CharacterProject.Id;
        if (chapterList.Any(x => x.CharacterProject.Id != projectId))
            throw new InvalidOperationException("Phạm vi có chương thuộc bộ truyện/dự án khác.");
        if (scope == BatchReplacementScope.SelectedSegment && selectedSegmentId is null)
            throw new ArgumentException("Hãy chọn segment trước khi tìm.", nameof(selectedSegmentId));

        var normalized = rules.Where(IsUsable)
            .GroupBy(x => x.Find, StringComparer.Ordinal)
            .Select(group => new RuleGroup(group.Key, group.ToArray(),
                group.Select(x => x.Replacement).Distinct(StringComparer.Ordinal).Count() > 1))
            .OrderByDescending(x => x.Find.Length).ThenBy(x => x.Find, StringComparer.Ordinal).ToArray();
        // Index by first character (longest first): each text position only tries the rules that can start there.
        var byFirstCharacter = normalized.GroupBy(x => x.Find[0]).ToDictionary(group => group.Key, group => group.ToArray());
        var output = new List<BatchReplacementPreviewItem>();
        foreach (var chapter in chapterList)
        foreach (var segment in chapter.Segments.OrderBy(x => x.Order))
        {
            if (scope == BatchReplacementScope.SelectedSegment && segment.Id != selectedSegmentId) continue;
            FindMatches(chapter, segment, byFirstCharacter, output);
        }
        return output;
    }

    public static string Apply(string text, IEnumerable<BatchReplacementPreviewItem> items)
    {
        string result = text;
        var ordered = items.OrderByDescending(x => x.StartUtf16).ToArray();
        int previousStart = text.Length;
        foreach (var item in ordered)
        {
            if (item.Conflict || item.Replacement is null)
                throw new InvalidOperationException("Không thể áp dụng quy tắc đang xung đột.");
            if (item.StartUtf16 < 0 || item.LengthUtf16 <= 0 || item.StartUtf16 > text.Length || item.LengthUtf16 > text.Length - item.StartUtf16 ||
                item.StartUtf16 + item.LengthUtf16 > previousStart ||
                !string.Equals(text.Substring(item.StartUtf16, item.LengthUtf16), item.Original, StringComparison.Ordinal))
                throw new InvalidOperationException("Bản dịch hoặc danh sách chọn đã thay đổi/chồng lấn; hãy xem trước lại.");
            result = string.Concat(result.AsSpan(0, item.StartUtf16), item.Replacement,
                result.AsSpan(item.StartUtf16 + item.LengthUtf16));
            previousStart = item.StartUtf16;
        }
        return result;
    }

    public static BatchApplyResult ApplyToChapter(ChapterDocument chapter,
        IEnumerable<BatchReplacementPreviewItem> selectedItems, bool allowProtected,
        BatchReplacementScope scope, Guid batchId, DateTimeOffset createdAt, bool automatic = false)
    {
        ArgumentNullException.ThrowIfNull(chapter);
        ArgumentNullException.ThrowIfNull(selectedItems);
        // Validate the complete plan on a private copy before touching the caller's document.
        var candidate = DocumentJson.Clone(chapter);
        var result = ApplyToCandidate(candidate, selectedItems.ToArray(), allowProtected, scope, batchId, createdAt, automatic);
        DocumentJson.Validate(candidate);
        foreach (var changed in candidate.Segments)
        {
            var live = chapter.Segments.Single(x => x.Id == changed.Id);
            live.UserDraft = changed.UserDraft;
            live.DraftSourceAttemptId = changed.DraftSourceAttemptId;
            live.DraftRevision = changed.DraftRevision;
            live.DraftHistory = changed.DraftHistory;
            live.Status = changed.Status;
            live.LastError = changed.LastError;
        }
        chapter.BatchReplacementHistory = candidate.BatchReplacementHistory;
        chapter.UpdatedAt = candidate.UpdatedAt;
        return result;
    }

    private static BatchApplyResult ApplyToCandidate(ChapterDocument chapter,
        BatchReplacementPreviewItem[] items, bool allowProtected, BatchReplacementScope scope,
        Guid batchId, DateTimeOffset createdAt, bool automatic)
    {
        if (items.Length == 0) throw new InvalidOperationException("Không có vị trí hợp lệ được chọn trong chương này.");
        if (items.Any(x => x.ChapterId != chapter.Id || x.CharacterProjectId != chapter.CharacterProject.Id))
            throw new InvalidOperationException("Kết quả xem trước thuộc bộ truyện/dự án khác.");
        var run = new BatchReplacementRun
        {
            Id = batchId, CreatedAt = createdAt, Scope = scope, Automatic = automatic,
            Description = automatic ? "Tự làm sạch ánh xạ đã xác nhận sau dịch" : "Tìm và thay thế hàng loạt"
        };
        foreach (var group in items.GroupBy(x => x.SegmentId))
        {
            var segment = chapter.Segments.SingleOrDefault(x => x.Id == group.Key)
                ?? throw new InvalidOperationException("Segment trong xem trước không còn tồn tại.");
            var groupItems = group.OrderBy(x => x.StartUtf16).ToArray();
            if (groupItems.Any(x => x.Conflict || x.Replacement is null))
                throw new InvalidOperationException($"Đoạn {segment.Order} có quy tắc xung đột chưa được giải quyết.");
            if (segment.IsProtectedFromAutomaticTranslation && !allowProtected)
                throw new InvalidOperationException($"Đoạn {segment.Order} đã sửa/duyệt và đang được bảo vệ.");
            string before = segment.EditableText;
            if (segment.Status == SegmentStatus.Translating ||
                groupItems.Any(x => x.SegmentStatus != segment.Status ||
                                    x.Protected != segment.IsProtectedFromAutomaticTranslation ||
                                    x.ApprovalVersion != segment.Approvals.Count ||
                                    x.LastApprovalId != segment.Approvals.LastOrDefault()?.Id ||
                                    x.DraftRevision != segment.DraftRevision ||
                                    !string.Equals(x.AnalysedText, before, StringComparison.Ordinal)))
                throw new InvalidOperationException($"Đoạn {segment.Order} đã đổi sau khi xem trước; hãy quét lại.");
            string after = Apply(before, groupItems);
            if (after == before) continue;
            if (segment.UserDraft is string existing && segment.DraftHistory.LastOrDefault()?.Text != existing)
                segment.DraftHistory.Add(new()
                {
                    RecordedAt = createdAt, Revision = segment.DraftRevision, Text = existing,
                    SourceAttemptId = segment.DraftSourceAttemptId, Reason = "Trước khi tìm và thay thế hàng loạt"
                });
            var revision = new BatchReplacementSegmentRevision
            {
                SegmentId = segment.Id, SegmentOrder = segment.Order, BeforeText = before, AfterText = after,
                PreviousUserDraft = segment.UserDraft, PreviousNormalizedText = segment.NormalizedText,
                PreviousDraftSourceAttemptId = segment.DraftSourceAttemptId,
                PreviousDraftRevision = segment.DraftRevision, PreviousStatus = segment.Status,
                PreviousLastError = segment.LastError,
                AppliedApprovalVersion = segment.Approvals.Count,
                AppliedLastApprovalId = segment.Approvals.LastOrDefault()?.Id,
                Changes = groupItems.Select(ToChange).ToList()
            };
            segment.UserDraft = after;
            segment.DraftSourceAttemptId ??= segment.LatestAiAttempt?.Id;
            segment.DraftRevision++;
            revision.AppliedDraftRevision = segment.DraftRevision;
            segment.Status = ChineseTextScanner.ContainsHan(after)
                ? SegmentStatus.InspectionRequired : SegmentStatus.AwaitingReview;
            segment.LastError = null;
            run.Segments.Add(revision);
        }
        if (run.Segments.Count == 0) throw new InvalidOperationException("Không có nội dung nào thay đổi.");
        chapter.BatchReplacementHistory.Add(run);
        chapter.UpdatedAt = createdAt;
        return new(batchId, chapter.Id, chapter.Title, run.Segments.Count, run.Segments.Sum(x => x.Changes.Count));
    }

    public static BatchApplyResult UndoLatest(ChapterDocument chapter, DateTimeOffset changedAt, Guid? batchId = null)
    {
        var run = chapter.BatchReplacementHistory
            .Where(x => !x.Reverted && !x.Automatic && (batchId is null || x.Id == batchId))
            .OrderByDescending(x => x.CreatedAt).FirstOrDefault()
            ?? throw new InvalidOperationException("Không có đợt thay thế thủ công nào để hoàn tác.");
        foreach (var revision in run.Segments)
        {
            var segment = chapter.Segments.SingleOrDefault(x => x.Id == revision.SegmentId)
                ?? throw new InvalidOperationException("Không thể hoàn tác vì cấu trúc segment đã thay đổi.");
            if (segment.Approvals.Count != revision.AppliedApprovalVersion ||
                segment.Approvals.LastOrDefault()?.Id != revision.AppliedLastApprovalId)
                throw new InvalidOperationException($"Đoạn {segment.Order} có quyết định duyệt mới sau batch; không tự ghi đè khi hoàn tác.");
            if (segment.DraftRevision != revision.AppliedDraftRevision ||
                !string.Equals(segment.EditableText, revision.AfterText, StringComparison.Ordinal))
                throw new InvalidOperationException($"Đoạn {segment.Order} đã được sửa sau đợt thay thế; không tự ghi đè khi hoàn tác.");
        }
        foreach (var revision in run.Segments)
        {
            var segment = chapter.Segments.Single(x => x.Id == revision.SegmentId);
            if (segment.UserDraft is string current && segment.DraftHistory.LastOrDefault()?.Text != current)
                segment.DraftHistory.Add(new()
                {
                    RecordedAt = changedAt, Revision = segment.DraftRevision, Text = current,
                    SourceAttemptId = segment.DraftSourceAttemptId, Reason = "Trước khi hoàn tác thay thế hàng loạt"
                });
            segment.UserDraft = revision.PreviousUserDraft;
            segment.NormalizedText = revision.PreviousNormalizedText;
            segment.DraftSourceAttemptId = revision.PreviousDraftSourceAttemptId;
            segment.DraftRevision = revision.PreviousDraftRevision;
            segment.Status = revision.PreviousStatus;
            segment.LastError = revision.PreviousLastError;
        }
        run.Reverted = true;
        chapter.UpdatedAt = changedAt;
        return new(run.Id, chapter.Id, chapter.Title, run.Segments.Count, run.Segments.Sum(x => x.Changes.Count));
    }

    public static (string Text, IReadOnlyList<BatchReplacementChange> Changes) ApplyConfirmed(
        string text, IEnumerable<BatchReplacementRule> rules)
    {
        var temporaryChapter = new ChapterDocument { CharacterProject = new() };
        var segment = new TranslationSegment { Id = Guid.NewGuid(), Order = 1, SourceText = "占", UserDraft = text };
        temporaryChapter.Segments = [segment];
        var engine = new BatchReplacementEngine();
        var preview = engine.Preview([temporaryChapter], rules.Where(x => x.Confirmed),
            BatchReplacementScope.CurrentChapter).Where(x => !x.Conflict && x.Replacement is not null).ToArray();
        return (Apply(text, preview), preview.Select(ToChange).ToArray());
    }

    private static void FindMatches(ChapterDocument chapter, TranslationSegment segment,
        Dictionary<char, RuleGroup[]> rulesByFirstCharacter, List<BatchReplacementPreviewItem> output)
    {
        string text = segment.EditableText;
        int offset = 0;
        while (offset < text.Length)
        {
            // Candidates are ordered longest first and their Find texts are distinct, so the first match is the longest.
            RuleGroup? selected = null;
            if (rulesByFirstCharacter.TryGetValue(text[offset], out RuleGroup[]? candidates))
                foreach (RuleGroup candidate in candidates)
                    if (offset + candidate.Find.Length <= text.Length &&
                        text.AsSpan(offset, candidate.Find.Length).SequenceEqual(candidate.Find)) { selected = candidate; break; }
            if (selected is null) { offset++; continue; }
            var preferred = selected.Rules.OrderByDescending(x => x.Confirmed)
                .ThenBy(x => x.Source == ReplacementRuleSource.CharacterProfile ? 0 : 1).First();
            string? replacement = selected.Conflict ? null : preferred.Replacement;
            string beforeSentence = ChineseTextScanner.SentenceAround(text, offset, selected.Find.Length);
            string afterFull = replacement is null ? text : string.Concat(text.AsSpan(0, offset), replacement,
                text.AsSpan(offset + selected.Find.Length));
            output.Add(new()
            {
                ChapterId = chapter.Id, CharacterProjectId = chapter.CharacterProject.Id,
                ChapterTitle = chapter.Title, SegmentId = segment.Id, SegmentOrder = segment.Order,
                StartUtf16 = offset, LengthUtf16 = selected.Find.Length, Original = selected.Find,
                Replacement = replacement, Source = preferred.Source, RuleConfirmed = preferred.Confirmed,
                SourceLabel = preferred.SourceLabel,
                BeforeSentence = beforeSentence,
                AfterSentence = ChineseTextScanner.SentenceAround(afterFull, offset, replacement?.Length ?? selected.Find.Length),
                AnalysedText = text, DraftRevision = segment.DraftRevision, SegmentStatus = segment.Status,
                ApprovalVersion = segment.Approvals.Count, LastApprovalId = segment.Approvals.LastOrDefault()?.Id,
                Protected = segment.IsProtectedFromAutomaticTranslation, Conflict = selected.Conflict,
                ConflictMessage = selected.Conflict
                    ? $"Có nhiều ánh xạ cho “{selected.Find}”: {string.Join(" / ", selected.Rules.Select(x => x.Replacement).Distinct())}."
                    : ""
            });
            offset += selected.Find.Length;
        }
    }

    private static BatchReplacementChange ToChange(BatchReplacementPreviewItem item) => new()
    {
        StartUtf16 = item.StartUtf16, LengthUtf16 = item.LengthUtf16, Original = item.Original,
        Replacement = item.Replacement!, Source = item.Source, SourceLabel = item.SourceLabel
    };
    private static bool IsUsable(BatchReplacementRule rule) =>
        !string.IsNullOrEmpty(rule.Find) && rule.Replacement is not null;
    private sealed record RuleGroup(string Find, BatchReplacementRule[] Rules, bool Conflict);
}
