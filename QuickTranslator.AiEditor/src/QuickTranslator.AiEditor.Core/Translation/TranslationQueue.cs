using System.Diagnostics;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class TranslationQueue(TranslationService service)
{
    private readonly object _sync = new();
    private CancellationTokenSource? _cancellation;
    private Task? _runTask;
    private volatile bool _pauseRequested;
    private Stopwatch? _watch;
    private TimeSpan _elapsedAtStart;
    public event EventHandler? ProgressChanged;
    public bool IsRunning { get { lock (_sync) return _runTask is { IsCompleted: false }; } }

    public Task StartAsync(QueueMode mode = QueueMode.MissingSegments, ModelOptions? options = null,
        PromptPreset? preset = null, CancellationToken cancellationToken = default) =>
        Launch(mode, options ?? service.GetSnapshot().DefaultModelOptions, preset ?? service.GetSnapshot().SelectedPreset, false, cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken = default)
        => ResumeAsync(null, null, cancellationToken);

    public Task ResumeAsync(ModelOptions? options, PromptPreset? preset,
        CancellationToken cancellationToken = default)
    {
        var checkpoint = service.GetSnapshot().Queue;
        if (checkpoint.Status is not (QueueStatus.Paused or QueueStatus.Cancelled or QueueStatus.Interrupted or QueueStatus.Faulted))
            throw new InvalidOperationException("Hàng đợi chưa ở trạng thái có thể tiếp tục.");
        return Launch(checkpoint.Mode, options ?? checkpoint.ModelOptions, preset ?? checkpoint.Preset, true,
            cancellationToken);
    }

    private Task Launch(QueueMode mode, ModelOptions options, PromptPreset preset, bool resume, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_runTask is { IsCompleted: false }) throw new InvalidOperationException("Hàng đợi đang chạy; không thể mở thêm hàng đợi.");
            cancellationToken.ThrowIfCancellationRequested();
            var checkpoint = service.BeginQueue(mode, options, preset, resume);
            _pauseRequested = false;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _watch = Stopwatch.StartNew();
            _elapsedAtStart = checkpoint.Elapsed;
            _runTask = RunAsync(checkpoint, _cancellation);
            return _runTask;
        }
    }

    public async Task PauseAsync()
    {
        Task? running;
        lock (_sync)
        {
            running = _runTask;
            if (running is null || running.IsCompleted) return;
            _pauseRequested = true;
            service.SetQueueStatus(QueueStatus.PauseRequested);
        }
        Notify();
        // Let the active request finish and checkpoint; no new request is sent after the pause boundary.
        await running.ConfigureAwait(false);
    }

    public async Task CancelAsync()
    {
        Task? running;
        lock (_sync)
        {
            running = _runTask;
            if (running is null || running.IsCompleted) return;
            service.SetQueueStatus(QueueStatus.Cancelling);
            _cancellation?.Cancel();
        }
        Notify();
        await running.ConfigureAwait(false);
    }

    private async Task RunAsync(QueueCheckpoint checkpoint, CancellationTokenSource cancellation)
    {
        try
        {
            await service.SaveAsync(CancellationToken.None).ConfigureAwait(false);
            Notify();
            while (service.GetSnapshot().Queue.NextIndex < checkpoint.CandidateIds.Count)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (_pauseRequested) { await FinishAsync(QueueStatus.Paused).ConfigureAwait(false); return; }
                var current = service.GetSnapshot();
                var id = checkpoint.CandidateIds[current.Queue.NextIndex];
                var segment = current.Segments.Single(s => s.Id == id);
                if (TranslationService.IsQueueEligible(segment, checkpoint.Mode))
                {
                    service.SetQueueCurrent(id);
                    Notify();
                    try
                    {
                        await service.TranslateQueuedAsync(id, checkpoint.ModelOptions, cancellation.Token, checkpoint.Preset, checkpoint.RunId).ConfigureAwait(false);
                    }
                    catch (QueueSegmentSkippedException)
                    {
                        // The service checkpointed the conflicting segment; continue with the next eligible segment.
                    }
                    catch (LlmRequestException) { /* Segment error is already checkpointed. Continue the chapter without an automatic retry. */ }
                    // Disk errors and unexpected exceptions stop the entire queue; continuing could lose results.
                }
                service.AdvanceQueue();
                service.CheckpointElapsed(CurrentElapsed());
                await service.SaveAsync(CancellationToken.None).ConfigureAwait(false);
                Notify();
                cancellation.Token.ThrowIfCancellationRequested();
            }
            var progress = GetProgress();
            var status = progress.Errors > 0 || progress.Translated < progress.Total ? QueueStatus.CompletedWithErrors :
                progress.NeedsInspection > 0 ? QueueStatus.CompletedWithWarnings : QueueStatus.Completed;
            await FinishAsync(status, status == QueueStatus.CompletedWithErrors
                ? $"Hàng đợi kết thúc: {progress.Errors} đoạn lỗi; {progress.Total - progress.Translated} đoạn chưa có bản dịch hoàn chỉnh. Chưa thể báo toàn chương thành công." : null).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await FinishAsync(QueueStatus.Cancelled).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            service.SetQueueStatus(QueueStatus.Faulted, StopElapsed(), ex.Message);
            try { await service.SaveAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception saveError)
            {
                throw new IOException("Hàng đợi đã dừng vì chưa lưu được. Giữ ứng dụng mở và lưu tiến độ trước khi đóng.", new AggregateException(ex, saveError));
            }
            throw;
        }
        finally { cancellation.Dispose(); Notify(); }
    }

    private async Task FinishAsync(QueueStatus status, string? error = null)
    {
        service.SetQueueStatus(status, StopElapsed(), error);
        await service.SaveAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private TimeSpan StopElapsed()
    {
        lock (_sync)
        {
            var elapsed = CurrentElapsed();
            _watch = null;
            _elapsedAtStart = elapsed;
            return elapsed;
        }
    }

    private TimeSpan CurrentElapsed()
    {
        lock (_sync) return _elapsedAtStart + (_watch?.Elapsed ?? TimeSpan.Zero);
    }

    public QueueProgress GetProgress()
    {
        lock (_sync)
        {
            var progress = service.GetQueueProgress();
            return _watch is null ? progress : progress with { Elapsed = _elapsedAtStart + _watch.Elapsed };
        }
    }

    private void Notify() => ProgressChanged?.Invoke(this, EventArgs.Empty);
}
