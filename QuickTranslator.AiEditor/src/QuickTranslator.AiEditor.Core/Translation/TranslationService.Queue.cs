using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

internal sealed class QueueSegmentSkippedException : Exception;

public sealed partial class TranslationService
{
    internal static bool IsQueueEligible(TranslationSegment segment, QueueMode mode)
    {
        if (segment.IsProtectedFromAutomaticTranslation || segment.Status == SegmentStatus.Approved) return false;
        if (mode == QueueMode.FailedSegments) return segment.Status == SegmentStatus.Error;
        if (segment.HasCompletedTranslation) return false;
        return segment.RequiresRetranslation || segment.Attempts.Count == 0 ||
            segment.Attempts.Last().Outcome is AttemptOutcome.Cancelled or AttemptOutcome.Interrupted;
    }

    internal QueueCheckpoint BeginQueue(QueueMode mode, ModelOptions options, PromptPreset preset, bool resume)
    {
        options.Validate(); preset.Validate();
        lock (_sync)
        {
            EnsureStructureAvailable();
            var previous = _chapter.Queue;
            _chapter.Queue = new()
            {
                RunId = resume && previous.RunId != Guid.Empty ? previous.RunId : Guid.NewGuid(),
                Mode = mode, Status = QueueStatus.Running, ModelOptions = options with { }, Preset = preset with { },
                Elapsed = resume ? previous.Elapsed : TimeSpan.Zero,
                StartedAt = resume ? previous.StartedAt : _clock.GetUtcNow(),
                CandidateIds = _chapter.Segments.Where(s => IsQueueEligible(s, mode)).Select(s => s.Id).ToList()
            };
            _chapter.DefaultModelOptions = options with { };
            _chapter.SelectedPreset = preset with { };
            Touch();
            return GetSnapshot().Queue;
        }
    }

    internal void SetQueueCurrent(Guid id)
    {
        lock (_sync) { _chapter.Queue.CurrentSegmentId = id; Touch(); }
    }

    internal void AdvanceQueue()
    {
        lock (_sync)
        {
            _chapter.Queue.NextIndex++;
            _chapter.Queue.CurrentSegmentId = null;
            Touch();
        }
    }

    internal void CheckpointElapsed(TimeSpan elapsed)
    {
        lock (_sync) { _chapter.Queue.Elapsed = elapsed; Touch(); }
    }

    internal void SetQueueStatus(QueueStatus status, TimeSpan? elapsed = null, string? error = null)
    {
        lock (_sync)
        {
            _chapter.Queue.Status = status;
            if (elapsed is not null) _chapter.Queue.Elapsed = elapsed.Value;
            if (!_chapter.Queue.IsActive) _chapter.Queue.CurrentSegmentId = null;
            _chapter.Queue.LastError = error;
            Touch();
        }
    }
}
