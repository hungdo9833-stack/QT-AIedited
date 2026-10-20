using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.TextCleaning;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed partial class TranslationService
{
    private readonly BatchReplacementEngine _batchReplacement = new();

    public IReadOnlyList<BatchReplacementPreviewItem> PreviewBatchReplacement(
        IEnumerable<BatchReplacementRule> rules, BatchReplacementScope scope)
    {
        var chapter = GetOperationSnapshot();
        if (scope == BatchReplacementScope.CurrentProject)
            throw new ArgumentException("Xem trước toàn dự án cần nạp các chương qua bộ điều phối lưu trữ.", nameof(scope));
        return _batchReplacement.Preview([chapter], rules, scope, chapter.SelectedSegmentId);
    }

    public async Task ConfigureTextCleanerAsync(bool autoApplyConfirmedMappings,
        IReadOnlyList<string>? ignoredChineseText = null, CancellationToken cancellationToken = default)
    {
        if (ignoredChineseText is not null && (ignoredChineseText.Count > 2000 ||
            ignoredChineseText.Any(string.IsNullOrWhiteSpace)))
            throw new ArgumentException("Danh sách giữ nguyên chứa mục trống hoặc vượt 2.000 mục.");
        lock (_sync)
        {
            EnsureStructureAvailable();
            _chapter.TextCleaner.AutoApplyConfirmedMappings = autoApplyConfirmedMappings;
            if (ignoredChineseText is not null)
                _chapter.TextCleaner.IgnoredChineseText = ignoredChineseText
                    .Select(x => x.Trim()).Distinct(StringComparer.Ordinal).ToList();
            Touch();
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<BatchApplyResult> ApplyBatchReplacementAsync(
        IEnumerable<BatchReplacementPreviewItem> selectedItems, bool allowProtected,
        BatchReplacementScope scope, Guid? batchId = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedItems);
        var items = selectedItems.ToArray();
        return CommitBatchAsync(candidate => BatchReplacementEngine.ApplyToChapter(candidate, items,
            allowProtected, scope, batchId ?? Guid.NewGuid(), _clock.GetUtcNow()), cancellationToken);
    }

    private bool _batchCommitInProgress;

    private void EnsureNoBatchCommit()
    {
        if (_batchCommitInProgress)
            throw new InvalidOperationException("Đang ghi thay đổi batch; hãy chờ hoàn tất trước khi sửa hoặc duyệt.");
    }

    private async Task<BatchApplyResult> CommitBatchAsync(Func<ChapterDocument, BatchApplyResult> prepare,
        CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool ownsCommit = false;
        try
        {
            ChapterDocument candidate;
            BatchApplyResult result;
            lock (_sync)
            {
                EnsureStructureAvailable();
                candidate = DocumentJson.Clone(_chapter);
                result = prepare(candidate);
                DocumentJson.Validate(candidate);
                _batchCommitInProgress = true;
                ownsCommit = true;
            }
            await _store.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _chapter = candidate;
                _revision++;
                _savedRevision = _revision;
            }
            return result;
        }
        finally
        {
            if (ownsCommit) lock (_sync) _batchCommitInProgress = false;
            _saveGate.Release();
        }
    }

    public Task<BatchApplyResult> UndoLatestBatchReplacementAsync(Guid? batchId = null,
        CancellationToken cancellationToken = default) =>
        CommitBatchAsync(candidate => BatchReplacementEngine.UndoLatest(candidate, _clock.GetUtcNow(), batchId),
            cancellationToken);

    private ChineseCleaningAnalysis AnalyseAutomaticChinese(ChapterDocument chapter, TranslationSegment selected,
        string translatedText, Guid batchId)
    {
        if (string.IsNullOrWhiteSpace(translatedText))
            return new(translatedText, null, []);
        string cleaned = translatedText;
        IReadOnlyList<BatchReplacementChange> changes = [];
        if (chapter.TextCleaner.AutoApplyConfirmedMappings && !selected.IsProtectedFromAutomaticTranslation)
        {
            var catalog = ReplacementRuleCatalog.FromChapter(chapter);
            (cleaned, changes) = BatchReplacementEngine.ApplyConfirmed(translatedText,
                catalog.Rules.Where(x => x.Confirmed));
        }
        var ignored = chapter.TextCleaner.IgnoredChineseText.ToHashSet(StringComparer.Ordinal);
        bool remains = ChineseTextScanner.FindClusters(cleaned).Any(x => !ignored.Contains(x.Text));
        IReadOnlyList<TranslationIssue> issues = remains
            ? [new(IssueCode.UntranslatedChinese, IssueSeverity.Warning,
                "Còn chữ Hán chưa có ánh xạ xác nhận; mở Quét chữ Hán còn sót để kiểm tra.")]
            : [];
        if (changes.Count == 0) return new(cleaned, null, issues);
        var run = new BatchReplacementRun
        {
            Id = batchId, CreatedAt = _clock.GetUtcNow(), Scope = BatchReplacementScope.SelectedSegment,
            Automatic = true, Description = "Tự làm sạch ánh xạ đã xác nhận sau dịch",
            Segments =
            [
                new()
                {
                    SegmentId = selected.Id, SegmentOrder = selected.Order,
                    BeforeText = translatedText, AfterText = cleaned,
                    PreviousUserDraft = selected.UserDraft, PreviousNormalizedText = selected.NormalizedText,
                    PreviousDraftSourceAttemptId = selected.DraftSourceAttemptId,
                    PreviousDraftRevision = selected.DraftRevision, AppliedDraftRevision = selected.DraftRevision,
                    PreviousStatus = selected.Status, PreviousLastError = selected.LastError,
                    Changes = changes.ToList()
                }
            ]
        };
        return new(cleaned, run, issues);
    }

    private sealed record ChineseCleaningAnalysis(string Text, BatchReplacementRun? Run,
        IReadOnlyList<TranslationIssue> Issues);
}
