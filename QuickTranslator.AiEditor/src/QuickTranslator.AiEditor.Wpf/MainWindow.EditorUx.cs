using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private readonly Dictionary<Button, object?> _normalActionTooltips = [];
    private EditorActionPlan? _currentEditorActionPlan;
    private bool _pendingEditorFocus;

    internal WorkspaceId CurrentWorkspace { get; private set; } = WorkspaceId.Editor;
    internal EditorMode CurrentEditorMode { get; private set; } = EditorMode.Translate;

    internal void NavigateTo(WorkspaceId id, EditorMode? mode = null)
    {
        switch (id)
        {
            case WorkspaceId.Editor:
                ShowTranslationWorkspace((mode ?? CurrentEditorMode) == EditorMode.Review);
                break;
            case WorkspaceId.Chapters:
                ShowChaptersWorkspace();
                break;
            case WorkspaceId.Queue:
                ShowQueueWorkspace();
                break;
            case WorkspaceId.Publish:
                _ = ShowPublicationWorkspaceAsync();
                break;
            case WorkspaceId.Reader:
                ShowReaderWorkspace();
                break;
            case WorkspaceId.Resources:
                ShowResourcesWorkspace("Dictionary");
                break;
            case WorkspaceId.Settings:
                ShowSettingsWorkspace();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(id), id, null);
        }
    }

    private EditorActionContext CreateEditorActionContext(ChapterDocument? chapter)
    {
        TranslationSegment? selected = chapter?.Segments.SingleOrDefault(s => s.Id == _selectedId);
        IReadOnlyDictionary<SegmentStatus, int> counts = Enum.GetValues<SegmentStatus>()
            .ToDictionary(status => status, status => chapter?.Segments.Count(s => s.Status == status) ?? 0);
        return new()
        {
            ProjectOpen = _projectSession is not null,
            ProjectChapterCount = _projectSession?.Manifest.ChapterCount ?? 0,
            ChapterSegmented = chapter is not null,
            RawSourceTextPresent = !string.IsNullOrWhiteSpace(ChapterInput.Text),
            SourceEditMode = _sourceEditMode,
            SingleRequestBusy = _busy,
            LocalQueueStatus = chapter?.Queue.Status,
            LocalQueueRunning = _queue?.IsRunning == true && chapter?.Queue.Status == QueueStatus.Running,
            ProjectQueueRunning = _projectTranslationCoordinator?.IsRunning == true,
            PresetsReady = _presetsReady,
            UiAvailable = !_uiOperation && !_closing,
            SegmentCounts = counts,
            RequiresRetranslationCount = chapter?.Segments.Count(s => s.RequiresRetranslation) ?? 0,
            SelectedSegmentStatus = selected?.Status,
            SelectedSegmentHasEditableDraft = !string.IsNullOrWhiteSpace(selected?.EditableText),
            NextUnfinishedProjectChapterExists = HasNextUnfinishedProjectChapter()
        };
    }

    private bool HasNextUnfinishedProjectChapter() =>
        _projectSession?.Manifest.Chapters.Any(chapter => chapter.DeletedAt is null &&
            chapter.ChapterId != _projectSession.Manifest.CurrentChapterId &&
            chapter.State != ProjectChapterState.Approved) == true;

    private async Task OpenNextUnfinishedProjectChapterAsync()
    {
        if (_projectSession is null) return;
        ProjectChapterEntry? next = _projectSession.Manifest.Chapters
            .Where(chapter => chapter.DeletedAt is null &&
                chapter.ChapterId != _projectSession.Manifest.CurrentChapterId &&
                chapter.State != ProjectChapterState.Approved)
            .OrderBy(chapter => chapter.Number)
            .FirstOrDefault();
        if (next is not null) await LoadProjectChapterAsync(next.ChapterId);
    }

    private void ApplyEditorActionPlan(ChapterDocument? chapter)
    {
        EditorActionContext context = CreateEditorActionContext(chapter);
        EditorActionPlan plan = EditorActionPolicy.Decide(context);
        _currentEditorActionPlan = plan;

        EditorContextTitleText.Text = _projectSession is null
            ? chapter?.Title ?? "Chưa mở Project"
            : $"{_projectSession.Manifest.VietnameseTitle}  ›  {chapter?.Title ?? "Chưa mở chương"}";
        UpdateEditorStateLabels(chapter);

        PrimaryActionButton.Content = plan.Primary.Label;
        string? primaryReason = plan.Primary.DisabledReason ?? PrimaryBusinessReason(plan.Primary.Kind, chapter);
        bool primaryBusinessEnabled = PrimaryBusinessEnabled(plan.Primary.Kind, context);
        if (!primaryBusinessEnabled && primaryReason is null)
            primaryReason = "Hành động chưa khả dụng trong trạng thái hiện tại.";
        ApplyActionState(PrimaryActionButton, plan.Primary.IsEnabled && primaryBusinessEnabled && primaryReason is null, primaryReason);

        var dedicated = new Dictionary<EditorActionKind, Button>
        {
            [EditorActionKind.TranslateChapter] = TranslateAllButton,
            [EditorActionKind.PauseQueue] = PauseQueueButton,
            [EditorActionKind.ResumeQueue] = ResumeQueueButton,
            [EditorActionKind.RetryErrors] = RetryErrorsButton,
            [EditorActionKind.EditSource] = EditSourceButton,
            [EditorActionKind.SaveSourceEdit] = SaveSourceEditButton,
            [EditorActionKind.CancelSourceEdit] = CancelSourceEditButton,
            [EditorActionKind.ApproveSegment] = ApproveCurrentButton,
            [EditorActionKind.ApproveChapter] = ApproveAllChapterButton
        };
        foreach ((EditorActionKind kind, Button button) in dedicated)
        {
            button.Visibility = plan.Secondary.Contains(kind) ? Visibility.Visible : Visibility.Collapsed;
            if (button.Visibility == Visibility.Visible)
                ApplyActionState(button, button.IsEnabled && EditorActionPolicy.DisabledReason(kind, context) is null,
                    EditorActionPolicy.DisabledReason(kind, context) ?? PrimaryBusinessReason(kind, chapter));
        }

        CancelQueueButton.Visibility = plan.Overflow.Contains(EditorActionKind.CancelQueue)
            ? Visibility.Visible : Visibility.Collapsed;
        EditorActionOverflowToggle.Visibility = CancelQueueButton.Visibility;
        if (CancelQueueButton.Visibility == Visibility.Visible)
            ApplyActionState(CancelQueueButton, CancelQueueButton.IsEnabled,
                EditorActionPolicy.DisabledReason(EditorActionKind.CancelQueue, context));

        EditorActionKind[] remaining = plan.Secondary.Where(kind => !dedicated.ContainsKey(kind)).ToArray();
        Button[] slots = [SecondaryActionButton1, SecondaryActionButton2, SecondaryActionButton3];
        for (int i = 0; i < slots.Length; i++)
        {
            if (i >= remaining.Length)
            {
                slots[i].Visibility = Visibility.Collapsed;
                continue;
            }
            EditorActionKind kind = remaining[i];
            slots[i].Visibility = Visibility.Visible;
            slots[i].Tag = kind;
            slots[i].Content = ActionLabel(kind);
            string? reason = EditorActionPolicy.DisabledReason(kind, context) ?? PrimaryBusinessReason(kind, chapter);
            ApplyActionState(slots[i], reason is null, reason);
        }

        ApplyReviewActionStates(context, chapter);
    }

    private void UpdateEditorStateLabels(ChapterDocument? chapter)
    {
        if (chapter is null)
        {
            EditorContentStateText.Text = string.IsNullOrWhiteSpace(ChapterInput.Text)
                ? "Chưa có chương" : "Nguyên văn chưa chia đoạn";
        }
        else
        {
            int approved = chapter.Segments.Count(s => s.Status == SegmentStatus.Approved);
            int review = chapter.Segments.Count(s => s.Status == SegmentStatus.AwaitingReview);
            int issues = chapter.Segments.Count(s => s.Status == SegmentStatus.InspectionRequired || s.RequiresRetranslation);
            int errors = chapter.Segments.Count(s => s.Status == SegmentStatus.Error);
            int untranslated = chapter.Segments.Count(s => s.Status == SegmentStatus.Untranslated);
            EditorContentStateText.Text = $"{approved}/{chapter.Segments.Count} đã duyệt  ·  {review} chờ duyệt  ·  {issues} cần kiểm tra  ·  {errors} lỗi  ·  {untranslated} chưa dịch";
        }

        string? processing = _busy ? "Đang dịch đoạn"
            : _projectTranslationCoordinator?.IsRunning == true ? "Queue Project đang chạy"
            : chapter?.Queue.Status switch
            {
                QueueStatus.Running => "Queue chương đang chạy",
                QueueStatus.PauseRequested => "Đã yêu cầu tạm dừng",
                QueueStatus.Paused => "Đã tạm dừng",
                QueueStatus.Interrupted => "Bị gián đoạn",
                QueueStatus.Cancelled => "Đã hủy",
                _ => null
            };
        EditorProcessingStateText.Text = processing ?? "";
        EditorProcessingStateText.Visibility = processing is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private string? PrimaryBusinessReason(EditorActionKind kind, ChapterDocument? chapter) => kind switch
    {
        EditorActionKind.SaveSourceEdit when !HasPendingSourceEdit => "Nguyên văn chưa có thay đổi để lưu.",
        EditorActionKind.TranslateSegment when _selectedId is null => "Chọn một đoạn trước.",
        EditorActionKind.ShowHistory when _selectedId is null => "Chọn một đoạn trước.",
        _ => null
    };

    private bool PrimaryBusinessEnabled(EditorActionKind kind, EditorActionContext context) => kind switch
    {
        EditorActionKind.SplitChapter => SplitButton.IsEnabled,
        EditorActionKind.TranslateChapter => TranslateAllButton.IsEnabled,
        EditorActionKind.TranslateSegment => TranslateButton.IsEnabled,
        EditorActionKind.CancelRequest => CancelButton.IsEnabled,
        EditorActionKind.PauseQueue => PauseQueueButton.IsEnabled,
        EditorActionKind.ResumeQueue => ResumeQueueButton.IsEnabled,
        EditorActionKind.RetryErrors => RetryErrorsButton.IsEnabled,
        EditorActionKind.NextError => context.Count(SegmentStatus.Error) > 0,
        EditorActionKind.NextInspection => context.Count(SegmentStatus.InspectionRequired) + context.RequiresRetranslationCount > 0,
        EditorActionKind.ApproveAndNext => ApproveAndNextButton.IsEnabled,
        EditorActionKind.ApproveSegment => ApproveButton.IsEnabled,
        EditorActionKind.ApproveChapter => ApproveAllChapterButton.IsEnabled,
        EditorActionKind.SaveSourceEdit => SaveSourceEditButton.IsEnabled,
        EditorActionKind.CancelSourceEdit => CancelSourceEditButton.IsEnabled,
        _ => context.UiAvailable
    };

    private void ApplyReviewActionStates(EditorActionContext context, ChapterDocument? chapter)
    {
        ApplyActionState(TranslateButton, TranslateButton.IsEnabled,
            EditorActionPolicy.DisabledReason(EditorActionKind.TranslateSegment, context));
        ApplyActionState(CancelButton, CancelButton.IsEnabled,
            EditorActionPolicy.DisabledReason(EditorActionKind.CancelRequest, context));
        ApplyActionState(UseAiButton, UseAiButton.IsEnabled,
            _selectedId is null ? "Chọn một đoạn trước." : "Chưa có bản lịch sử phù hợp để dùng.");
        ApplyActionState(ApproveButton, ApproveButton.IsEnabled,
            EditorActionPolicy.DisabledReason(EditorActionKind.ApproveSegment, context));
        ApplyActionState(ApproveAndNextButton, ApproveAndNextButton.IsEnabled,
            EditorActionPolicy.DisabledReason(EditorActionKind.ApproveAndNext, context));
        ApplyActionState(NextIssueSegmentButton,
            context.Count(SegmentStatus.Error) + context.Count(SegmentStatus.InspectionRequired) + context.RequiresRetranslationCount > 0,
            "Chương không có đoạn lỗi hoặc cần kiểm tra.");

        string? selectedReason = _selectedId is null ? "Chọn một đoạn trước." : null;
        foreach (Button button in new[] { SplitSegmentButton, MergeSegmentButton, MoveBoundaryButton })
            ApplyActionState(button, button.IsEnabled, selectedReason);
        ApplyActionState(RestoreStructureButton, RestoreStructureButton.IsEnabled,
            "Chưa có lần thay đổi cấu trúc nào để hoàn tác.");
        ApplyActionState(ArchiveButton, ArchiveButton.IsEnabled, chapter is null ? "Chưa có chương." : null);

        ApplyActionState(ScanChineseButton, ScanChineseButton.IsEnabled, chapter is null ? "Chưa có chương để quét." : null);
        ApplyActionState(NormalizePronounsButton, NormalizePronounsButton.IsEnabled, chapter is null ? "Chưa có chương để quét." : null);
        ApplyActionState(ApplyPronounsButton, ApplyPronounsButton.IsEnabled,
            "Chưa có báo cáo quét xưng hô với đề xuất an toàn.");
        ApplyActionState(UndoPronounsButton, UndoPronounsButton.IsEnabled,
            "Chưa có lần chuẩn hóa nào để hoàn tác.");
        ApplyActionState(UndoBatchReplaceButton, UndoBatchReplaceButton.IsEnabled,
            "Chưa có lần thay thế hàng loạt nào để hoàn tác.");
        ApplyActionState(BatchReplaceReviewButton, BatchReplaceButton.IsEnabled,
            chapter is null ? "Chưa có chương để thay thế." : null);
    }

    internal void ApplyActionState(Button button, bool enabled, string? reason)
    {
        if (!_normalActionTooltips.ContainsKey(button)) _normalActionTooltips[button] = button.ToolTip;
        button.IsEnabled = enabled;
        object? tooltip = enabled ? _normalActionTooltips[button] : reason;
        if (!enabled && string.IsNullOrWhiteSpace(tooltip?.ToString())) tooltip = "Hành động chưa khả dụng trong trạng thái hiện tại.";
        button.ToolTip = tooltip;
        ToolTipService.SetShowOnDisabled(button, true);
        AutomationProperties.SetHelpText(button, tooltip?.ToString() ?? "");
    }

    private async void OnPrimaryAction(object sender, RoutedEventArgs e)
    {
        if (_currentEditorActionPlan is { } plan) await DispatchEditorActionAsync(plan.Primary.Kind);
    }

    private async void OnSecondaryAction(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EditorActionKind kind }) await DispatchEditorActionAsync(kind);
    }

    private Task DispatchEditorActionAsync(EditorActionKind kind)
    {
        var args = new RoutedEventArgs();
        switch (kind)
        {
            case EditorActionKind.OpenProject: OnOpenProject(this, args); break;
            case EditorActionKind.CreateProject: OnNewProject(this, args); break;
            case EditorActionKind.ImportTxt: OnAddChapterFromText(this, args); break;
            case EditorActionKind.CreateChapter: OnNewChapter(this, args); break;
            case EditorActionKind.SplitChapter: OnSplitChapter(this, args); break;
            case EditorActionKind.EditSource: OnBeginSourceEdit(this, args); break;
            case EditorActionKind.TranslateChapter: OnTranslateAll(this, args); break;
            case EditorActionKind.TranslateSegment: OnTranslate(this, args); break;
            case EditorActionKind.CancelRequest: OnCancelTranslation(this, args); break;
            case EditorActionKind.PauseQueue: OnPauseQueue(this, args); break;
            case EditorActionKind.ResumeQueue: OnResumeQueue(this, args); break;
            case EditorActionKind.CancelQueue: OnCancelQueue(this, args); break;
            case EditorActionKind.RetryErrors: OnRetryErrors(this, args); break;
            case EditorActionKind.NextError:
            case EditorActionKind.NextInspection: JumpToNextIssueSegment(); break;
            case EditorActionKind.ApproveAndNext: return ApproveSegmentCoreAsync(true);
            case EditorActionKind.ApproveSegment: return ApproveSegmentCoreAsync(false);
            case EditorActionKind.ApproveChapter: OnApproveAllSegments(this, args); break;
            case EditorActionKind.NextUnfinishedChapter: return OpenNextUnfinishedProjectChapterAsync();
            case EditorActionKind.SaveSourceEdit: OnSaveSourceEdit(this, args); break;
            case EditorActionKind.CancelSourceEdit: OnCancelSourceEdit(this, args); break;
            case EditorActionKind.ShowHistory: OnShowCurrentHistory(this, args); break;
        }
        return Task.CompletedTask;
    }

    private void OnOpenReviewChecks(object sender, RoutedEventArgs e)
    {
        CommandOverflowToggle.IsChecked = false;
        NavigateTo(WorkspaceId.Editor, EditorMode.Review);
        ReviewChecksPanel.IsExpanded = true;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, () =>
        {
            ReviewChecksPanel.Focus();
            System.Windows.Input.Keyboard.Focus(ReviewChecksPanel);
        });
    }

    private void OnBilingualEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (!_pendingEditorFocus || CurrentWorkspace != WorkspaceId.Editor || CurrentEditorMode != EditorMode.Translate ||
            sender is not TextBox { Tag: Guid id } editor || id != _selectedId) return;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
        {
            if (!_pendingEditorFocus || CurrentWorkspace != WorkspaceId.Editor ||
                CurrentEditorMode != EditorMode.Translate || editor.Tag is not Guid current || current != _selectedId) return;
            editor.Focus();
            System.Windows.Input.Keyboard.Focus(editor);
            if (editor.IsKeyboardFocusWithin) _pendingEditorFocus = false;
        });
    }

    private static string ActionLabel(EditorActionKind kind) => kind switch
    {
        EditorActionKind.CreateProject => "Tạo Project",
        EditorActionKind.CreateChapter => "Tạo chương",
        EditorActionKind.TranslateSegment => "Dịch đoạn",
        EditorActionKind.ApproveAndNext => "Duyệt & Tiếp",
        EditorActionKind.ShowHistory => "Xem lịch sử",
        _ => kind.ToString()
    };
}
