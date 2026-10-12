using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed partial class TranslationService
{
    private readonly PronounResolutionEngine _pronouns = new();

    public async Task ConfigureCharacterProjectAsync(CharacterProject project, CancellationToken cancellationToken = default)
    {
        var snapshot = CharacterProjectStore.Clone(project);
        snapshot.Validate();
        lock (_sync)
        {
            EnsureStructureAvailable();
            var oldMappings = _chapter.CharacterProject.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.ChineseName))
                .Select(p => (p.ChineseName, p.VietnameseName)).ToHashSet();
            var updatedTerms = _chapter.ConfirmedTerms.Where(t => !oldMappings.Contains((t.Chinese, t.Vietnamese))).ToList();
            snapshot.UpdatedAt = _clock.GetUtcNow();
            foreach (var profile in snapshot.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.ChineseName)))
            {
                var existing = updatedTerms.SingleOrDefault(t =>
                    string.Equals(t.Chinese, profile.ChineseName, StringComparison.Ordinal));
                if (existing is null) updatedTerms.Add(new(profile.ChineseName, profile.VietnameseName));
                else if (!string.Equals(existing.Vietnamese, profile.VietnameseName, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Tên {profile.ChineseName} đang có ánh xạ glossary khác. Hãy xác nhận một cách viết trước khi lưu hồ sơ.");
            }
            _chapter.CharacterProject = snapshot;
            _chapter.ConfirmedTerms = updatedTerms;
            Touch();
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public PronounScanReport ScanPronouns()
    {
        var chapter = GetSnapshot();
        var items = new List<PronounSuggestion>();
        string previous = "";
        foreach (var segment in chapter.Segments.OrderBy(s => s.Order))
        {
            string text = segment.EditableText;
            items.AddRange(_pronouns.ScanSegment(segment.Id, segment.Order, segment.SourceText, text, previous,
                chapter.CharacterProject, segment.IsProtectedFromAutomaticTranslation, chapter.ConfirmedTerms));
            previous = text;
        }
        return new(chapter.Id, chapter.CharacterProject.Id, _clock.GetUtcNow(), items);
    }

    public async Task<Guid?> ApplySafePronounChangesAsync(PronounScanReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        Guid? batchId = null;
        lock (_sync)
        {
            EnsureStructureAvailable();
            if (report.ChapterId != _chapter.Id || report.CharacterProjectId != _chapter.CharacterProject.Id)
                throw new InvalidOperationException("Báo cáo thuộc chương hoặc dự án nhân vật khác.");
            var safeGroups = report.Items.Where(i => i.Kind == PronounSuggestionKind.SafeReplacement)
                .GroupBy(i => i.SegmentId).ToArray();
            if (safeGroups.Length == 0) return null;
            var prepared = new List<(TranslationSegment Segment, string Before, string After, IGrouping<Guid, PronounSuggestion> Items)>();
            foreach (var group in safeGroups)
            {
                var segment = Find(group.Key);
                if (segment.IsProtectedFromAutomaticTranslation)
                    throw new InvalidOperationException($"Đoạn {segment.Order} đã được sửa/duyệt; hãy quét lại để chỉ xem cảnh báo.");
                string before = segment.EditableText;
                if (!string.Equals(before, group.First().AnalysedText, StringComparison.Ordinal))
                    throw new InvalidOperationException($"Đoạn {segment.Order} đã thay đổi sau khi quét; hãy quét lại.");
                string after = PronounResolutionEngine.Apply(before, group);
                if (after != before) prepared.Add((segment, before, after, group));
            }
            if (prepared.Count == 0) return null;
            batchId = Guid.NewGuid();
            foreach (var item in prepared)
            {
                var run = CreateNormalizationRun(batchId.Value, false, item.Segment.NormalizedText, item.Before, item.After, item.Items);
                item.Segment.NormalizedText = item.After;
                item.Segment.NormalizationHistory.Add(run);
                item.Segment.Status = item.Segment.Status == SegmentStatus.Error ? SegmentStatus.InspectionRequired : item.Segment.Status;
            }
            Touch();
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return batchId;
    }

    public async Task<Guid?> UndoLatestPronounNormalizationAsync(CancellationToken cancellationToken = default)
    {
        Guid? batchId;
        lock (_sync)
        {
            EnsureStructureAvailable();
            var latest = _chapter.Segments.SelectMany(s => s.NormalizationHistory.Select(r => (Segment: s, Run: r)))
                .Where(x => !x.Run.Reverted).OrderByDescending(x => x.Run.CreatedAt).FirstOrDefault();
            if (latest.Run is null) return null;
            batchId = latest.Run.BatchId;
            var runs = _chapter.Segments.SelectMany(s => s.NormalizationHistory
                .Where(r => r.BatchId == batchId && !r.Reverted).Select(r => (Segment: s, Run: r))).ToArray();
            if (runs.Any(x => x.Segment.UserDraft is not null || x.Segment.Approvals.Count != 0 ||
                              !string.Equals(x.Segment.NormalizedText, x.Run.AfterText, StringComparison.Ordinal)))
                throw new InvalidOperationException("Không thể hoàn tác tự động vì một đoạn đã được sửa, duyệt hoặc thay đổi sau lần chuẩn hóa.");
            foreach (var item in runs)
            {
                item.Segment.NormalizedText = item.Run.PreviousNormalizedText;
                item.Run.Reverted = true;
            }
            Touch();
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return batchId;
    }

    private PronounNormalizationAnalysis AnalyseAutomaticPronouns(ChapterDocument chapter, TranslationSegment selected,
        string translatedText, Guid batchId, string? previousNormalizedText = null)
    {
        if (!chapter.CharacterProject.NormalizeAfterTranslation || chapter.CharacterProject.Profiles.Count == 0 ||
            string.IsNullOrWhiteSpace(translatedText))
            return new(translatedText, null, []);
        string previous = chapter.Segments.LastOrDefault(s => s.Order < selected.Order)?.EditableText ?? "";
        var items = _pronouns.ScanSegment(selected.Id, selected.Order, selected.SourceText, translatedText, previous,
            chapter.CharacterProject, false, chapter.ConfirmedTerms);
        var safe = items.Where(i => i.Kind == PronounSuggestionKind.SafeReplacement).ToArray();
        string normalized = safe.Length == 0 ? translatedText : PronounResolutionEngine.Apply(translatedText, safe);
        var run = safe.Length == 0 ? null : CreateNormalizationRun(batchId, true, previousNormalizedText, translatedText, normalized, safe);
        var issues = items.Where(i => i.Kind == PronounSuggestionKind.Ambiguous).Select(i =>
            new TranslationIssue(IssueCode.AmbiguousPronoun, IssueSeverity.Warning,
                $"Đại từ “{i.Original}” ở câu “{i.Sentence}” chưa có chủ thể đủ rõ; giữ nguyên để người dùng xác nhận."))
            .DistinctBy(i => i.Message).ToArray();
        return new(normalized, run, issues);
    }

    private PronounNormalizationRun CreateNormalizationRun(Guid batchId, bool automatic, string? previousNormalized,
        string before, string after, IEnumerable<PronounSuggestion> suggestions) => new()
    {
        BatchId = batchId,
        CreatedAt = _clock.GetUtcNow(),
        Automatic = automatic,
        PreviousNormalizedText = previousNormalized,
        BeforeText = before,
        AfterText = after,
        Changes = suggestions.Select(i => new PronounChange
        {
            StartUtf16 = i.StartUtf16, Length = i.Length, Original = i.Original,
            Replacement = i.Replacement!, CharacterId = i.CharacterId!.Value, Reason = i.Reason
        }).ToList()
    };

    private sealed record PronounNormalizationAnalysis(string Text, PronounNormalizationRun? Run,
        IReadOnlyList<TranslationIssue> Issues);
}
