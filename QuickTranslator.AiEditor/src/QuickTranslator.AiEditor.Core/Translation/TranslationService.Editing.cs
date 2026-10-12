using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed partial class TranslationService
{
    public async Task RenameAsync(string title, string chineseTitle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(chineseTitle);
        lock (_sync)
        {
            _chapter.Title = title.Trim();
            _chapter.ChineseTitle = chineseTitle.Trim();
            Touch();
        }
        // The same save gate as autosave captures the renamed live document, including pending edits.
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool IsProcessing { get { lock (_sync) return _chapter.Queue.IsActive || _translationGate.CurrentCount == 0; } }

    public async Task ConfigureAsync(ModelOptions options, PromptPreset preset, IReadOnlyList<ConfirmedTerm>? terms = null,
        string? confirmedContext = null, CancellationToken cancellationToken = default)
    {
        options.Validate();
        preset.Validate();
        if (terms is not null && (terms.Count > 1000 || terms.Any(t => string.IsNullOrWhiteSpace(t.Chinese) || string.IsNullOrWhiteSpace(t.Vietnamese))))
            throw new ArgumentException("Thuật ngữ phải có đủ Trung/Việt, tối đa 1.000 mục.");
        if (confirmedContext?.Length > 1000) throw new ArgumentException("Ngữ cảnh xác nhận tối đa 1.000 ký tự; chỉ nhập ghi chú ngắn.");
        lock (_sync)
        {
            EnsureStructureAvailable();
            _chapter.DefaultModelOptions = options with { };
            _chapter.SelectedPreset = preset with { };
            if (terms is not null) _chapter.ConfirmedTerms = terms.ToList();
            if (confirmedContext is not null) _chapter.ConfirmedContextNotes = confirmedContext;
            Touch();
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public string PreviewPrompt(Guid segmentId, PromptPreset preset, IReadOnlyList<ConfirmedTerm>? terms = null, string? confirmedContext = null)
    {
        var chapter = GetOperationSnapshot();
        if (terms is not null) chapter.ConfirmedTerms = terms.ToList();
        if (confirmedContext is not null) chapter.ConfirmedContextNotes = confirmedContext;
        var segment = chapter.Segments.Single(s => s.Id == segmentId);
        return BuildPrompt(chapter, segment, [], "", preset);
    }

    private string BuildPrompt(ChapterDocument chapter, TranslationSegment segment, IReadOnlyList<GlossaryEntry> external, string memory, PromptPreset preset)
    {
        var confirmed = chapter.ConfirmedTerms.Where(t => segment.SourceText.Contains(t.Chinese, StringComparison.Ordinal))
            .Select(t => new GlossaryEntry(t.Chinese, t.Vietnamese)).ToArray();
        // Explicitly confirmed spellings override optional provider entries. References stay short.
        var glossary = confirmed.Concat(external).DistinctBy(t => t.Chinese).Take(Math.Max(64, confirmed.Length)).ToArray();
        string notes = chapter.ConfirmedContextNotes;
        var previous = chapter.Segments.LastOrDefault(s => s.Order < segment.Order && s.Status == SegmentStatus.Approved);
        if (previous is not null)
        {
            string approved = previous.Approvals.Last().Text;
            var positions = System.Globalization.StringInfo.ParseCombiningCharacters(approved);
            if (positions.Length > 200) approved = approved[..positions[200]];
            notes += "\nPrevious human-approved excerpt (reference only): " + approved;
        }
        if (!string.IsNullOrWhiteSpace(memory)) notes += "\n" + memory;
        return _prompts.Build(segment.SourceText, glossary, notes, preset);
    }

    public async Task RestoreDraftAsync(Guid segmentId, Guid versionId, bool approval,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var segment = Find(segmentId);
            if (segment.Status == SegmentStatus.Translating) throw new InvalidOperationException("Hãy chờ hoặc hủy lần dịch trước khi đổi bản nháp.");
            string text;
            Guid? source;
            if (approval)
            {
                var version = segment.Approvals.Single(a => a.Id == versionId);
                text = version.Text; source = version.SourceAttemptId;
            }
            else
            {
                var version = segment.DraftHistory.Single(a => a.Id == versionId);
                text = version.Text; source = version.SourceAttemptId;
            }
            ArchiveDraft(segment, "Trước khi khôi phục bản cũ");
            segment.UserDraft = text;
            segment.DraftSourceAttemptId = source;
            segment.DraftRevision++;
            segment.Status = segment.RequiresRetranslation || segment.LatestAiAttempt?.Issues.Count > 0
                ? SegmentStatus.InspectionRequired : SegmentStatus.AwaitingReview;
            segment.LastError = null;
            Touch();
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ArchiveDraft(TranslationSegment segment, string reason)
    {
        if (segment.UserDraft is not string text || segment.DraftHistory.LastOrDefault()?.Text == text) return;
        segment.DraftHistory.Add(new() { RecordedAt = _clock.GetUtcNow(), Revision = segment.DraftRevision,
            Text = text, SourceAttemptId = segment.DraftSourceAttemptId, Reason = reason });
    }

    private void EnsureStructureAvailable()
    {
        if (_chapter.Queue.IsActive || _translationGate.CurrentCount == 0)
            throw new InvalidOperationException("Hãy Tạm dừng và chờ yêu cầu đang chạy kết thúc trước khi đổi cấu trúc/thiết lập.");
    }

    private static string SourceHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}
