using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private bool _transitioning;
    private Task? _activeWorkCompletion;

    // All context replacements use this lifecycle, including programmatic navigation.
    // The dispatcher owns the operation lock; overlapping commands are rejected, never queued invisibly.
    private async Task<bool> RunWorkspaceTransitionAsync(Func<Task> commit)
    {
        if (!BeginUiOperation(allowDuringTranslation: true)) return false;
        _transitioning = true;
        _autosave.Stop();
        try
        {
            if (_service is null && !string.IsNullOrWhiteSpace(ChapterInput.Text))
            {
                if (!ConfirmWorkspaceChange("Lưu nguyên văn chưa chia thành chương trước khi chuyển? Chọn Không để giữ nguyên cửa sổ và nội dung đang nhập.",
                    "Bảo vệ nguyên văn chưa lưu")) return false;
                await PrepareChapterAsync();
            }
            await SafelyLeaveCurrentProjectAsync();
            await commit();
            return true;
        }
        finally { _transitioning = false; EndUiOperation(); }
    }

    private async Task WaitForCurrentWorkAsync()
    {
        var queue = _queue;
        var request = _activeRequest;
        var completion = _activeWorkCompletion;
        if (queue?.IsRunning == true)
        {
            SetStatus("Đang chờ request hiện tại và lưu checkpoint trước khi chuyển…");
            await queue.PauseAsync();
        }
        if (request is not null)
        {
            try { await request; }
            catch (OperationCanceledException) { /* Recorded by the source service; save below. */ }
            catch (OllamaRequestException) { /* API failure is recorded; disk failures must still block switching. */ }
        }
        if (completion is not null) await completion;
        if (ReferenceEquals(request, _activeRequest) && request is { IsCompleted: true })
        {
            _activeRequest = null;
            _busy = false;
        }
    }
}
