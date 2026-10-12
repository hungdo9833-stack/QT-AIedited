using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Segmentation;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed partial class TranslationService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly ChapterDocument _chapter;
    private readonly IOllamaClient _client;
    private readonly IChapterStore _store;
    private readonly IGlossaryProvider _glossary;
    private readonly IContextMemory _memory;
    private readonly TimeProvider _clock;
    private readonly TranslationPromptBuilder _prompts = new();
    private readonly TranslationQualityChecker _quality = new();
    private long _revision;
    private long _savedRevision = -1;
    private Projects.ProjectResources? _projectResources;

    public void UseProjectResources(Projects.ProjectResources resources)
    {
        var copy = resources.Clone(); copy.Validate();
        lock (_sync)
        {
            if (_chapter.ProjectId != copy.ProjectId) throw new InvalidOperationException("Tài nguyên không thuộc project của chương.");
            _projectResources = copy;
        }
    }

    private ChapterDocument GetOperationSnapshot() => GetOperationSnapshot(out _);

    private ChapterDocument GetOperationSnapshot(out long? resourceRevision)
    {
        lock (_sync)
        {
            var snapshot = DocumentJson.Clone(_chapter);
            resourceRevision = _projectResources?.Revision;
            _projectResources?.ApplyTo(snapshot);
            return snapshot;
        }
    }

    public TranslationService(ChapterDocument chapter, IOllamaClient client, IChapterStore store,
        IGlossaryProvider? glossary = null, IContextMemory? memory = null, TimeProvider? clock = null)
    {
        _chapter = DocumentJson.Clone(chapter);
        DocumentJson.Migrate(_chapter);
        DocumentJson.Validate(_chapter);
        _client = client;
        _store = store;
        _glossary = glossary ?? new EmptyGlossaryProvider();
        _memory = memory ?? new EmptyContextMemory();
        _clock = clock ?? TimeProvider.System;
        RecoverInterruptedAttempts();
    }

    public static ChapterDocument CreateChapter(string title, string originalText, SegmenterOptions? segmentation = null,
        ModelOptions? model = null, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(originalText)) throw new ArgumentException("Hãy nhập nguyên văn tiếng Trung trước khi chia đoạn.");
        segmentation ??= new();
        model ??= new();
        model.Validate();
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var chapter = new ChapterDocument
        {
            Title = string.IsNullOrWhiteSpace(title) ? "Chương chưa đặt tên" : title.Trim(),
            OriginalText = originalText,
            SegmentationOptions = segmentation,
            DefaultModelOptions = model,
            CreatedAt = now,
            UpdatedAt = now
        };
        chapter.Segments = new TextSegmenter().Segment(originalText, chapter.Id, segmentation).ToList();
        chapter.SelectedSegmentId = chapter.Segments[0].Id;
        DocumentJson.Validate(chapter);
        return chapter;
    }

    public ChapterDocument GetSnapshot() { lock (_sync) return DocumentJson.Clone(_chapter); }
    public ChapterDocument GetWorkingSnapshot() => GetOperationSnapshot();
    public bool HasUnsavedChanges { get { lock (_sync) return _revision > _savedRevision; } }
    public string SavePath { get { lock (_sync) return _store.GetPath(_chapter.Id); } }

    public void SelectSegment(Guid segmentId)
    {
        lock (_sync)
        {
            Find(segmentId);
            if (_chapter.SelectedSegmentId == segmentId) return;
            _chapter.SelectedSegmentId = segmentId;
            Touch();
        }
    }

    // Called synchronously on each UI edit. A save can be debounced without allowing an AI result to replace this text.
    public void UpdateDraft(Guid segmentId, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_sync)
        {
            var segment = Find(segmentId);
            if (segment.UserDraft == text) return;
            if (segment.UserDraft is null) segment.DraftSourceAttemptId = segment.LatestAiAttempt?.Id;
            segment.UserDraft = text;
            segment.DraftRevision++;
            if (segment.Status != SegmentStatus.Translating)
            {
                segment.Status = segment.RequiresRetranslation || segment.LatestAiAttempt?.Issues.Count > 0
                    ? SegmentStatus.InspectionRequired : SegmentStatus.AwaitingReview;
                segment.LastError = null;
            }
            Touch();
        }
    }

    public async Task UseAiAsDraftAsync(Guid segmentId, Guid attemptId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var segment = Find(segmentId);
            if (segment.Status == SegmentStatus.Translating) throw new InvalidOperationException("Đoạn này đang dịch; hãy chờ hoặc hủy trước khi chọn bản AI.");
            var attempt = segment.Attempts.SingleOrDefault(a => a.Id == attemptId);
            if (string.IsNullOrWhiteSpace(attempt?.AiText)) throw new InvalidOperationException("Lần dịch này không có văn bản để dùng.");
            ArchiveDraft(segment, "Trước khi chọn bản AI khác");
            segment.UserDraft = attempt.AiText;
            segment.DraftSourceAttemptId = attempt.Id;
            segment.DraftRevision++;
            segment.Status = segment.RequiresRetranslation || attempt.Issues.Count > 0
                ? SegmentStatus.InspectionRequired : SegmentStatus.AwaitingReview;
            segment.LastError = null;
            Touch();
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ApproveAsync(Guid segmentId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            var segment = Find(segmentId);
            if (segment.Status == SegmentStatus.Translating) throw new InvalidOperationException("Không thể duyệt khi đoạn này đang dịch.");
            string text = segment.EditableText;
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Bản dịch trống không thể được duyệt.");
            if (segment.UserDraft is null)
            {
                segment.UserDraft = text;
                segment.DraftSourceAttemptId = segment.LatestAiAttempt?.Id;
                segment.DraftRevision++;
            }
            segment.Approvals.Add(new()
            {
                ApprovedAt = _clock.GetUtcNow(), Text = text,
                DraftRevision = segment.DraftRevision, SourceAttemptId = segment.DraftSourceAttemptId
            });
            ArchiveDraft(segment, "Bản được người dùng duyệt");
            segment.Status = SegmentStatus.Approved;
            segment.RequiresRetranslation = false;
            segment.LastError = null;
            Touch();
        }
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<TranslationAttempt> TranslateAsync(Guid segmentId, ModelOptions? options = null,
        CancellationToken cancellationToken = default, PromptPreset? preset = null) =>
        TranslateInternalAsync(segmentId, options, cancellationToken, preset, null);

    internal Task<TranslationAttempt> TranslateQueuedAsync(Guid segmentId, ModelOptions options,
        CancellationToken cancellationToken, PromptPreset preset, Guid runId) =>
        TranslateInternalAsync(segmentId, options, cancellationToken, preset, runId);

    private async Task<TranslationAttempt> TranslateInternalAsync(Guid segmentId, ModelOptions? options,
        CancellationToken cancellationToken, PromptPreset? preset, Guid? runId)
    {
        options ??= GetSnapshot().DefaultModelOptions;
        options.Validate();
        lock (_sync)
            if (_chapter.Queue.IsActive && _chapter.Queue.RunId != runId)
                throw new InvalidOperationException("Hàng đợi đang chạy. Hãy tạm dừng hoàn toàn trước khi dịch riêng một đoạn.");
        // Reject a second user request instead of queuing it to run automatically later.
        if (!await _translationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Đã có một đoạn đang dịch. Hãy chờ hoặc hủy; ứng dụng không xếp hàng dịch tự động.");

        TranslationAttempt? attempt = null;
        bool responseRecorded = false;
        try
        {
            lock (_sync)
                if (_chapter.Queue.IsActive && _chapter.Queue.RunId != runId)
                    throw new InvalidOperationException("Hàng đợi đã bắt đầu. Hãy tạm dừng trước khi dịch riêng.");
            var context = GetOperationSnapshot(out var resourceRevision);
            var selected = context.Segments.SingleOrDefault(s => s.Id == segmentId)
                ?? throw new ArgumentException("Không tìm thấy đoạn đang chọn.", nameof(segmentId));
            var entries = await _glossary.GetEntriesAsync(selected.SourceText, cancellationToken).ConfigureAwait(false);
            string notes = await _memory.GetNotesAsync(context, segmentId, cancellationToken).ConfigureAwait(false);
            preset ??= context.SelectedPreset;
            string prompt = BuildPrompt(context, selected, entries, notes, preset);
            attempt = new()
            {
                StartedAt = _clock.GetUtcNow(), Outcome = AttemptOutcome.InProgress,
                ModelOptions = options with { }, PromptVersion = TranslationPromptBuilder.Version, Prompt = prompt,
                PresetSnapshot = preset with { }, SourceSha256 = SourceHash(selected.SourceText),
                ProjectResourceRevision = resourceRevision
            };
            lock (_sync)
            {
                var segment = Find(segmentId);
                if (runId is not null && segment.IsProtectedFromAutomaticTranslation) throw new QueueSegmentSkippedException();
                segment.Attempts.Add(attempt);
                // Capture resources used for this operation. Historical attempts/prompts remain intact.
                if (_projectResources is not null)
                {
                    _chapter.CharacterProject = CharacterProjectStore.Clone(context.CharacterProject);
                    _chapter.ConfirmedTerms = context.ConfirmedTerms.ToList();
                    _chapter.ConfirmedContextNotes = context.ConfirmedContextNotes;
                }
                segment.Status = SegmentStatus.Translating;
                segment.LastError = null;
                _chapter.DefaultModelOptions = options with { };
                Touch();
            }
            // Checkpoint before contacting Ollama: a crash can be distinguished from an approved translation.
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            var result = await _client.TranslateAsync(prompt, options, cancellationToken).ConfigureAwait(false);
            Guid postProcessBatchId = Guid.NewGuid();
            var chineseAnalysis = AnalyseAutomaticChinese(context, selected, result.Text, postProcessBatchId);
            var pronounAnalysis = AnalyseAutomaticPronouns(context, selected, chineseAnalysis.Text,
                postProcessBatchId, chineseAnalysis.Run is null ? null : chineseAnalysis.Text);
            var issues = _quality.Check(selected.SourceText, result with { Text = pronounAnalysis.Text }, options)
                .Concat(chineseAnalysis.Issues).Concat(pronounAnalysis.Issues)
                .DistinctBy(i => (i.Code, i.Message)).ToList();
            lock (_sync)
            {
                var segment = Find(segmentId);
                attempt.AiText = result.Text;
                attempt.Metrics = result.Metrics;
                attempt.ResponseDone = result.Done;
                attempt.DoneReason = result.DoneReason;
                attempt.Issues = issues;
                attempt.FinishedAt = _clock.GetUtcNow();
                bool invalid = issues.Any(i => i.Severity == IssueSeverity.Error);
                attempt.Outcome = invalid ? AttemptOutcome.InvalidOutput : AttemptOutcome.Completed;
                segment.NormalizedText = invalid ? selected.NormalizedText : pronounAnalysis.Run is null ? null : pronounAnalysis.Text;
                if (!invalid && pronounAnalysis.Run is null && chineseAnalysis.Run is not null)
                    segment.NormalizedText = chineseAnalysis.Text;
                if (chineseAnalysis.Run is not null && !invalid)
                    _chapter.BatchReplacementHistory.Add(chineseAnalysis.Run);
                if (pronounAnalysis.Run is not null && !invalid)
                {
                    segment.NormalizationHistory.Add(pronounAnalysis.Run);
                    attempt.NormalizationRunId = pronounAnalysis.Run.Id;
                }
                segment.Status = invalid ? SegmentStatus.Error : issues.Count != 0 ? SegmentStatus.InspectionRequired : SegmentStatus.AwaitingReview;
                if (!invalid) segment.RequiresRetranslation = false;
                segment.LastError = invalid ? issues.First(i => i.Severity == IssueSeverity.Error).Message : null;
                // UserDraft and all previous attempts/approvals are deliberately untouched.
                Touch();
                responseRecorded = true;
            }
            // A cancel click must not discard a response already received, or its checkpoint.
            await SaveAsync(CancellationToken.None).ConfigureAwait(false);
            return GetSnapshot().Segments.Single(s => s.Id == segmentId).Attempts.Single(a => a.Id == attempt.Id);
        }
        catch (QueueSegmentSkippedException) { throw; }
        catch (Exception ex)
        {
            if (attempt is not null)
            {
                lock (_sync)
                {
                    var segment = Find(segmentId);
                    if (!responseRecorded)
                    {
                        attempt.FinishedAt = _clock.GetUtcNow();
                        attempt.Outcome = ex is OperationCanceledException ? AttemptOutcome.Cancelled : AttemptOutcome.Failed;
                        attempt.Error = ex is OperationCanceledException ? "Đã hủy lần dịch; bản nháp vẫn được giữ." : ex.Message;
                    }
                    segment.Status = SegmentStatus.Error;
                    segment.LastError = ex is OperationCanceledException ? "Đã hủy lần dịch; bản nháp vẫn được giữ." : ex.Message;
                    Touch();
                }
                // Do not silently retry a failed disk operation. The UI offers an explicit save/recovery path.
                if (ex is not IOException and not UnauthorizedAccessException)
                {
                    try { await SaveAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception saveError)
                    {
                        throw new IOException("Lần dịch bị lỗi và chưa lưu được trạng thái. Bản nháp vẫn còn trong ứng dụng; hãy lưu trước khi đóng.",
                            new AggregateException(ex, saveError));
                    }
                }
            }
            throw;
        }
        finally { _translationGate.Release(); }
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ChapterDocument snapshot;
            long revision;
            lock (_sync) { snapshot = DocumentJson.Clone(_chapter); revision = _revision; }
            // Take the snapshot after acquiring the save gate, so a late save cannot restore old text.
            await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_sync) _savedRevision = Math.Max(_savedRevision, revision);
        }
        finally { _saveGate.Release(); }
    }

    private TranslationSegment Find(Guid segmentId) => _chapter.Segments.SingleOrDefault(s => s.Id == segmentId)
        ?? throw new ArgumentException("Không tìm thấy đoạn.", nameof(segmentId));

    private void Touch() { _revision++; _chapter.UpdatedAt = _clock.GetUtcNow(); }

    private void RecoverInterruptedAttempts()
    {
        if (_chapter.Queue.IsActive)
        {
            _chapter.Queue.Status = QueueStatus.Interrupted;
            _chapter.Queue.CurrentSegmentId = null;
            _chapter.Queue.LastError = "Đã phục hồi hàng đợi bị gián đoạn. Bấm Tiếp tục; không gửi yêu cầu tự động khi mở tệp.";
            Touch();
        }
        foreach (var segment in _chapter.Segments)
        {
            bool interrupted = segment.Status == SegmentStatus.Translating;
            foreach (var attempt in segment.Attempts.Where(a => a.Outcome == AttemptOutcome.InProgress))
            {
                attempt.Outcome = AttemptOutcome.Interrupted;
                attempt.FinishedAt = _clock.GetUtcNow();
                attempt.Error = "Lần dịch trước bị gián đoạn khi ứng dụng đóng. Chỉ dịch lại khi bạn bấm Dịch lại.";
                interrupted = true;
            }
            if (!interrupted) continue;
            segment.Status = SegmentStatus.Error;
            segment.LastError = "Đã phục hồi sau lần dịch bị gián đoạn; bản nháp cũ được giữ. Không tự dịch tiếp.";
            Touch();
        }
    }
}
