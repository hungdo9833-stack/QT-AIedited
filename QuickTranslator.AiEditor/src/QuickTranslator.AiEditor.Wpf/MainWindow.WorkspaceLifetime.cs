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
        if (_hostedPublicationView?.IsBusy == true)
        {
            SetStatus("Xuất bản đang chạy. Hãy chờ hoàn tất hoặc hủy tác vụ trước khi đóng/chuyển Project.");
            return false;
        }
        if (!BeginUiOperation(allowDuringTranslation: true)) return false;
        _transitioning = true;
        _autosave.Stop();
        try
        {
            if (HasPendingSourceEdit)
            {
                SetStatus("Nguyên văn đang sửa chưa được lưu. Hãy chọn Lưu nguyên văn hoặc Hủy sửa nguồn trước khi chuyển.");
                return false;
            }
            if (_sourceEditMode) EndSourceEditMode(assignSnapshot: true);
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

    // Adjustment E (BL-15): Allow opening/editing completed or non-queued chapters while ProjectTranslationCoordinator runs in background,
    // without pausing the project translation queue or mutating ProjectResources.Revision.
    private async Task<bool> RunSafeBackgroundChapterSwitchAsync(Func<Task> commit)
    {
        if (!BeginUiOperation(allowDuringTranslation: true)) return false;
        _transitioning = true;
        _autosave.Stop();
        try
        {
            if (HasPendingSourceEdit)
            {
                SetStatus("Nguyên văn đang sửa chưa được lưu. Hãy chọn Lưu nguyên văn hoặc Hủy sửa nguồn trước khi chuyển.");
                return false;
            }
            if (_sourceEditMode) EndSourceEditMode(assignSnapshot: true);
            var queue = _queue;
            var request = _activeRequest;
            var completion = _activeWorkCompletion;
            if (queue?.IsRunning == true) await queue.PauseAsync();
            if (request is not null)
            {
                try { await request; }
                catch (OperationCanceledException)
                {
                    // Cancellation is the expected completion path while leaving the current workspace.
                }
                catch (LlmRequestException ex) { ShowError(ex); }
            }
            if (completion is not null) await completion;
            if (ReferenceEquals(request, _activeRequest) && request is { IsCompleted: true })
            {
                _activeRequest = null;
                _busy = false;
            }
            await SaveCurrentAsync();
            await commit();
            return true;
        }
        finally { _transitioning = false; EndUiOperation(); }
    }

    private async Task WaitForCurrentWorkAsync()
    {
        await PauseProjectTranslationForContextChangeAsync();
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
            catch (OperationCanceledException) { SetStatus("Request đã hủy; đang lưu bản nháp trước khi chuyển."); }
            catch (LlmRequestException ex) { ShowError(ex); } // Recorded by source service; disk errors still block switching.
        }
        if (completion is not null) await completion;
        if (ReferenceEquals(request, _activeRequest) && request is { IsCompleted: true })
        {
            _activeRequest = null;
            _busy = false;
        }
    }
}
