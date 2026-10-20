using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed partial class TranslationService
{
    public async Task RenameAsync(string title, string chineseTitle, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(chineseTitle);
        lock (_sync)
        {
            EnsureNoBatchCommit();
            _chapter.Title = title.Trim();
            _chapter.ChineseTitle = chineseTitle.Trim();
            Touch();
        }
        // The same save gate as autosave captures the renamed live document, including pending edits.
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool IsProcessing { get { lock (_sync) return _batchCommitInProgress || _chapter.Queue.IsActive || _translationGate.CurrentCount == 0; } }

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
        IBookKnowledgeContextProvider? provider;
        lock (_sync) provider = _bookKnowledgeContext;
        return BuildPrompt(chapter, segment, [], "", preset, chapter.DefaultModelOptions,
            provider?.GetSnapshot(chapter, segment));
    }

    public CharacterConsistencySelection PreviewCharacterConsistency(Guid segmentId)
    {
        var chapter = GetOperationSnapshot();
        var segment = chapter.Segments.Single(s => s.Id == segmentId);
        return _characterConsistency.Select(chapter.CharacterProject, segment.SourceText, chapter.ChapterNumber, SceneText(chapter, segment));
    }

    /// <summary>Source of the two segments before this one: who is "in the scene" for dialogue rules.</summary>
    internal static string SceneText(ChapterDocument chapter, TranslationSegment segment) => string.Join("\n",
        chapter.Segments.Where(s => s.Order < segment.Order).OrderByDescending(s => s.Order).Take(2).OrderBy(s => s.Order).Select(s => s.SourceText));

    private string BuildPrompt(ChapterDocument chapter, TranslationSegment segment, IReadOnlyList<GlossaryEntry> external,
        string memory, PromptPreset preset, ModelOptions options, BookKnowledgeContextSnapshot? knowledge = null)
    {
        CharacterConsistencySelection consistency = _characterConsistency.Select(chapter.CharacterProject, segment.SourceText,
            chapter.ChapterNumber, SceneText(chapter, segment));
        var confirmed = chapter.ConfirmedTerms.Where(t => segment.SourceText.Contains(t.Chinese, StringComparison.Ordinal) &&
                !consistency.CoveredChineseTerms.Contains(t.Chinese))
            .Select(t => new GlossaryEntry(t.Chinese, t.Vietnamese)).ToArray();
        // Explicitly confirmed spellings override optional provider entries. References stay short.
        var glossary = confirmed.Concat(external).DistinctBy(t => t.Chinese).Take(Math.Max(64, confirmed.Length)).ToArray();
        var contextBlocks = new List<string>();
        var previous = chapter.Segments.LastOrDefault(s => s.Order < segment.Order &&
            !string.IsNullOrWhiteSpace(s.EditableText));
        if (previous is not null && !string.IsNullOrWhiteSpace(previous.EditableText))
        {
            string previousText = previous.Status == SegmentStatus.Approved && previous.Approvals.Count > 0
                ? previous.Approvals[^1].Text : previous.EditableText;
            previousText = TakeTextElements(previousText, 200);
            string source = previous.Status == SegmentStatus.Approved ? "human-approved" :
                previous.UserDraft is not null ? "user draft, not approved" : "AI draft, not approved";
            contextBlocks.Add($"Previous {source} excerpt (reference only; never overwrite user text): {previousText}");
        }
        if (!string.IsNullOrWhiteSpace(chapter.ConfirmedContextNotes))
            contextBlocks.Add(chapter.ConfirmedContextNotes);
        if (!string.IsNullOrWhiteSpace(memory)) contextBlocks.Add(memory);
        if (!string.IsNullOrWhiteSpace(knowledge?.PromptBlock)) contextBlocks.Add(knowledge.PromptBlock);
        return BuildWithinTokenBudget(segment.SourceText, glossary, contextBlocks, preset,
            consistency.PromptBlock, options);
    }

    private string BuildWithinTokenBudget(string source, IReadOnlyList<GlossaryEntry> glossary,
        IReadOnlyList<string> contextBlocks, PromptPreset preset, string characterConsistency, ModelOptions options)
    {
        int budget = options.ContextSize - options.MaximumOutputTokens - 128;
        string Build(IReadOnlyList<GlossaryEntry> terms, string context, string characters) =>
            _prompts.Build(source, terms, context, preset, characters);
        string minimal = Build([], "", "");
        if (PromptTokenBudget.Estimate(minimal) > budget)
            throw new InvalidOperationException(
                "Đoạn nguồn và prompt vượt ngân sách context của model. Hãy tăng Context hoặc giảm kích thước segment.");

        string characters = FitOptional(characterConsistency,
            value => Build([], "", value), budget);
        var selectedTerms = new List<GlossaryEntry>();
        foreach (GlossaryEntry term in glossary)
        {
            selectedTerms.Add(term);
            if (PromptTokenBudget.Estimate(Build(selectedTerms, "", characters)) <= budget) continue;
            selectedTerms.RemoveAt(selectedTerms.Count - 1);
            break;
        }

        string context = "";
        foreach (string block in contextBlocks.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            string separator = context.Length == 0 ? "" : Environment.NewLine;
            string candidate = context + separator + block;
            if (PromptTokenBudget.Estimate(Build(selectedTerms, candidate, characters)) <= budget)
            {
                context = candidate;
                continue;
            }
            string fitted = FitOptional(block,
                value => Build(selectedTerms, context + separator + value, characters), budget);
            if (fitted.Length > 0) context += separator + fitted;
            break;
        }
        return Build(selectedTerms, context, characters);
    }

    private static string FitOptional(string value, Func<string, string> build, int tokenBudget)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (PromptTokenBudget.Estimate(build(value)) <= tokenBudget) return value;
        int low = 0, high = value.Length;
        while (low < high)
        {
            int middle = low + (high - low + 1) / 2;
            if (PromptTokenBudget.Estimate(build(value[..middle])) <= tokenBudget) low = middle;
            else high = middle - 1;
        }
        if (low > 0 && low < value.Length && char.IsHighSurrogate(value[low - 1]) && char.IsLowSurrogate(value[low])) low--;
        return value[..low].TrimEnd();
    }

    private static string TakeTextElements(string value, int count)
    {
        int[] positions = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        return positions.Length <= count ? value : value[..positions[count]];
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
        EnsureNoBatchCommit();
        if (_chapter.Queue.IsActive || _translationGate.CurrentCount == 0)
            throw new InvalidOperationException("Hãy Tạm dừng và chờ yêu cầu đang chạy kết thúc trước khi đổi cấu trúc/thiết lập.");
    }

    private static string SourceHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}
