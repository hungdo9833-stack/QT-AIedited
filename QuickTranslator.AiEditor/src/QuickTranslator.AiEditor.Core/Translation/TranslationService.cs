using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed partial class TranslationService
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _translationGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private ChapterDocument _chapter;
    private readonly ILlmClient _client;
    private readonly IChapterStore _store;
    private readonly IGlossaryProvider _glossary;
    private readonly IContextMemory _memory;
    private readonly TimeProvider _clock;
    private readonly TranslationPromptBuilder _prompts = new();
    private readonly CharacterConsistencyRuleSelector _characterConsistency = new();
    private readonly TranslationQualityChecker _quality = new();
    internal ChapterAnalysisCache ChapterAnalyses { get; init; } = new();
    private long _revision;
    private long _savedRevision = -1;
    private Projects.ProjectResources? _projectResources;
    private IBookKnowledgeContextProvider? _bookKnowledgeContext;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<TranslationSegment, AttemptTimingCache> _attemptTimings = new();
    internal long FullSnapshotCount { get; private set; }
    internal long AttemptAggregateBuildCount { get; private set; }
    internal long LightweightSnapshotCount { get; private set; }

    // All callers hold _sync. Kept observable for deterministic clone-budget tests.
    private ChapterDocument CloneChapter()
    {
        FullSnapshotCount++;
        return DocumentJson.Clone(_chapter);
    }

    public EditorStateSnapshot GetEditorState(Guid? selectedId)
    {
        lock (_sync)
        {
            LightweightSnapshotCount++;
            var segments = _chapter.Segments;
            var selected = segments.SingleOrDefault(s => s.Id == selectedId);
            int normalizedChanges = 0;
            bool canUndoNormalization = false;
            foreach (var segment in segments)
                foreach (var run in segment.NormalizationHistory)
                    if (!run.Reverted)
                    {
                        normalizedChanges += run.Changes.Count;
                        canUndoNormalization = true;
                    }
            return new(_chapter.Title, selected is null ? null : new(selected.Id, selected.Status,
                selected.Attempts.Count, !string.IsNullOrWhiteSpace(selected.EditableText), selected.Order,
                selected.HanCharacterCount, selected.SourceText, selected.RequiresRetranslation, selected.UserDraft is not null),
                segments.Count, segments.Count(s => s.Status == SegmentStatus.Untranslated),
                segments.Count(s => s.Status == SegmentStatus.Translating),
                segments.Count(s => s.Status == SegmentStatus.AwaitingReview),
                segments.Count(s => s.Status == SegmentStatus.Approved),
                segments.Count(s => s.Status == SegmentStatus.Error),
                segments.Count(s => s.Status == SegmentStatus.InspectionRequired || s.RequiresRetranslation),
                segments.Count(s => s.Status == SegmentStatus.InspectionRequired),
                segments.Count(s => s.RequiresRetranslation), normalizedChanges, canUndoNormalization,
                _chapter.BatchReplacementHistory.Any(r => !r.Reverted && !r.Automatic),
                _chapter.SegmentationHistory.Count > 0, _chapter.Queue.Status);
        }
    }

    public QueueProgress GetQueueProgress()
    {
        lock (_sync)
        {
            LightweightSnapshotCount++;
            var queue = _chapter.Queue;
            int translated = 0, errors = 0, inspection = 0, remaining = 0, durationCount = 0;
            double durationSeconds = 0;
            foreach (var segment in _chapter.Segments)
            {
                if (!segment.RequiresRetranslation && !string.IsNullOrWhiteSpace(segment.EditableText)) translated++;
                if (segment.Status == SegmentStatus.Error) errors++;
                if (segment.Status == SegmentStatus.InspectionRequired || segment.RequiresRetranslation) inspection++;
                if (IsQueueEligible(segment, queue.Mode)) remaining++;
                var cache = _attemptTimings.GetOrCreateValue(segment);
                // Attempts are appended, then the last attempt is finalized under this lock.
                // Structural edits/restores replace segment instances and therefore get fresh caches.
                var outcome = segment.Attempts.LastOrDefault()?.Outcome;
                if (cache.AttemptCount != segment.Attempts.Count || cache.LastOutcome != outcome || cache.Model != queue.ModelOptions)
                {
                    cache.AttemptCount = segment.Attempts.Count;
                    cache.LastOutcome = outcome;
                    cache.Model = queue.ModelOptions;
                    cache.Count = 0;
                    cache.Seconds = 0;
                    foreach (var attempt in segment.Attempts)
                        if (attempt.Outcome == AttemptOutcome.Completed && attempt.ModelOptions == queue.ModelOptions &&
                            attempt.Metrics?.ClientElapsed.TotalSeconds > 0)
                        {
                            cache.Count++;
                            cache.Seconds += attempt.Metrics.ClientElapsed.TotalSeconds;
                        }
                    AttemptAggregateBuildCount++;
                }
                durationCount += cache.Count;
                durationSeconds += cache.Seconds;
            }
            TimeSpan? estimate = durationCount >= 2 ? TimeSpan.FromSeconds(durationSeconds / durationCount * remaining) : null;
            return new(queue.Status, _chapter.Segments.Count, translated, errors, inspection, remaining,
                _chapter.Segments.SingleOrDefault(s => s.Id == queue.CurrentSegmentId)?.Order,
                queue.Elapsed, estimate, queue.LastError);
        }
    }

    private sealed class AttemptTimingCache
    {
        public int AttemptCount = -1;
        public AttemptOutcome? LastOutcome;
        public ModelOptions? Model;
        public int Count;
        public double Seconds;
    }

    public void UseProjectResources(Projects.ProjectResources resources)
    {
        var copy = resources.Clone(); copy.Validate();
        lock (_sync)
        {
            if (_chapter.ProjectId != copy.ProjectId) throw new InvalidOperationException("Tài nguyên không thuộc project của chương.");
            _projectResources = copy;
        }
    }

    public void UseBookKnowledgeContext(IBookKnowledgeContextProvider? provider)
    {
        lock (_sync) _bookKnowledgeContext = provider;
    }

    private ChapterDocument GetOperationSnapshot() => GetOperationSnapshot(out _);

    private ChapterDocument GetOperationSnapshot(out long? resourceRevision)
    {
        lock (_sync)
        {
            var snapshot = CloneChapter();
            resourceRevision = _projectResources?.Revision;
            _projectResources?.ApplyTo(snapshot);
            return snapshot;
        }
    }

    public TranslationService(ChapterDocument chapter, ILlmClient client, IChapterStore store,
        IGlossaryProvider? glossary = null, IContextMemory? memory = null, TimeProvider? clock = null)
    {
        _chapter = DocumentJson.Clone(chapter);
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

    public ChapterDocument GetSnapshot() { lock (_sync) return CloneChapter(); }
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
        {
            EnsureNoBatchCommit();
            if (_chapter.Queue.IsActive && _chapter.Queue.RunId != runId)
                throw new InvalidOperationException("Hàng đợi đang chạy. Hãy tạm dừng hoàn toàn trước khi dịch riêng một đoạn.");
        }
        // Reject a second user request instead of queuing it to run automatically later.
        if (!await _translationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("Đã có một đoạn đang dịch. Hãy chờ hoặc hủy; ứng dụng không xếp hàng dịch tự động.");

        TranslationAttempt? attempt = null;
        bool responseRecorded = false;
        try
        {
            lock (_sync)
            {
                EnsureNoBatchCommit();
                if (_chapter.Queue.IsActive && _chapter.Queue.RunId != runId)
                    throw new InvalidOperationException("Hàng đợi đã bắt đầu. Hãy tạm dừng trước khi dịch riêng.");
            }
            var context = GetOperationSnapshot(out var resourceRevision);
            var selected = context.Segments.SingleOrDefault(s => s.Id == segmentId)
                ?? throw new ArgumentException("Không tìm thấy đoạn đang chọn.", nameof(segmentId));
            var entries = await _glossary.GetEntriesAsync(selected.SourceText, cancellationToken).ConfigureAwait(false);
            string notes = await _memory.GetNotesAsync(context, segmentId, cancellationToken).ConfigureAwait(false);
            preset ??= context.SelectedPreset;
            IBookKnowledgeContextProvider? provider;
            lock (_sync) provider = _bookKnowledgeContext;
            BookKnowledgeContextSnapshot? knowledge = provider?.GetSnapshot(context, selected);
            string prompt = BuildPrompt(context, selected, entries, notes, preset, options, knowledge);
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
            // An in-flight request may be lost on process termination. Persist only completed outcomes.
            var result = await _client.TranslateAsync(prompt, options, cancellationToken).ConfigureAwait(false);
            // Reference notes echoed after the translation are cut; notes mixed into the prose reject the attempt.
            PromptEchoFilter.Result echo = PromptEchoFilter.Strip(result.Text);
            var echoIssues = new List<TranslationIssue>();
            if (echo.RemovedLines > 0)
            {
                result = result with { Text = echo.Text };
                echoIssues.Add(new(IssueCode.EchoedPromptNotes, IssueSeverity.Warning,
                    $"Model chép {echo.RemovedLines} dòng ghi chú nhân vật/lời thoại vào cuối bản dịch; QT Studio đã tự cắt bỏ."));
            }
            if (echo.EchoLinesInsideProse >= 2)
                echoIssues.Add(new(IssueCode.EchoedPromptNotes, IssueSeverity.Error,
                    "Bản dịch lẫn ghi chú nhân vật/lời thoại của prompt giữa các câu; không lưu, hãy dịch lại."));
            Guid postProcessBatchId = Guid.NewGuid();
            var chineseAnalysis = AnalyseAutomaticChinese(context, selected, result.Text, postProcessBatchId);
            var pronounAnalysis = AnalyseAutomaticPronouns(context, selected, chineseAnalysis.Text,
                postProcessBatchId, chineseAnalysis.Run is null ? null : chineseAnalysis.Text);
            var issues = _quality.Check(selected.SourceText, result with { Text = pronounAnalysis.Text }, options)
                .Concat(echoIssues).Concat(chineseAnalysis.Issues).Concat(pronounAnalysis.Issues)
                .Concat(StorySettingRules.CheckDialogue(context.CharacterProject, pronounAnalysis.Text))
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
            // Queue combines this result with its cursor advance into one checkpoint.
            if (runId is null) await SaveAsync(CancellationToken.None).ConfigureAwait(false);
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
                // Do not silently retry a failed disk operation. The UI offers an explicit save action.
                if (runId is null && ex is not IOException and not UnauthorizedAccessException)
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
            lock (_sync) { snapshot = CloneChapter(); revision = _revision; }
            // Take the snapshot after acquiring the save gate, so a late save cannot restore old text.
            await _store.SaveAsync(snapshot, cancellationToken).ConfigureAwait(false);
            lock (_sync) _savedRevision = Math.Max(_savedRevision, revision);
        }
        finally { _saveGate.Release(); }
    }

    private TranslationSegment Find(Guid segmentId)
    {
        EnsureNoBatchCommit();
        return _chapter.Segments.SingleOrDefault(s => s.Id == segmentId)
            ?? throw new ArgumentException("Không tìm thấy đoạn.", nameof(segmentId));
    }

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
