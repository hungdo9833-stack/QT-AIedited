using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Wpf;

internal enum EditorActionKind
{
    OpenProject,
    CreateProject,
    ImportTxt,
    CreateChapter,
    SplitChapter,
    EditSource,
    TranslateChapter,
    TranslateSegment,
    CancelRequest,
    PauseQueue,
    ResumeQueue,
    CancelQueue,
    RetryErrors,
    NextError,
    NextInspection,
    ApproveAndNext,
    ApproveSegment,
    ApproveChapter,
    NextUnfinishedChapter,
    SaveSourceEdit,
    CancelSourceEdit,
    ShowHistory
}

internal sealed record EditorActionContext
{
    public bool ProjectOpen { get; init; }
    public int ProjectChapterCount { get; init; }
    public bool ChapterSegmented { get; init; }
    public bool RawSourceTextPresent { get; init; }
    public bool SourceEditMode { get; init; }
    public bool SingleRequestBusy { get; init; }
    public QueueStatus? LocalQueueStatus { get; init; }
    public bool LocalQueueRunning { get; init; }
    public bool ProjectQueueRunning { get; init; }
    public bool PresetsReady { get; init; }
    public bool UiAvailable { get; init; }
    public IReadOnlyDictionary<SegmentStatus, int> SegmentCounts { get; init; } =
        new Dictionary<SegmentStatus, int>();
    public int RequiresRetranslationCount { get; init; }
    public SegmentStatus? SelectedSegmentStatus { get; init; }
    public bool SelectedSegmentHasEditableDraft { get; init; }
    public bool NextUnfinishedProjectChapterExists { get; init; }

    public int Count(SegmentStatus status) => SegmentCounts.TryGetValue(status, out int value) ? value : 0;
    public int TotalSegments => SegmentCounts.Values.Sum();
}

internal sealed record PrimaryAction(EditorActionKind Kind, string Label, bool IsEnabled, string? DisabledReason);

internal sealed record EditorActionPlan(
    PrimaryAction Primary,
    IReadOnlyList<EditorActionKind> Secondary,
    IReadOnlyList<EditorActionKind> Overflow);

internal static class EditorActionPolicy
{
    internal static EditorActionPlan Decide(EditorActionContext context)
    {
        EditorActionKind primaryKind;
        string label;
        EditorActionKind[] secondary = [];
        EditorActionKind[] overflow = [];

        int errors = context.Count(SegmentStatus.Error);
        int issues = context.Count(SegmentStatus.InspectionRequired) + context.RequiresRetranslationCount;
        int untranslated = context.Count(SegmentStatus.Untranslated);
        int awaitingReview = context.Count(SegmentStatus.AwaitingReview);

        if (context.SourceEditMode)
        {
            primaryKind = EditorActionKind.SaveSourceEdit;
            label = "Lưu nguyên văn";
            secondary = [EditorActionKind.CancelSourceEdit];
        }
        else if (context.SingleRequestBusy)
        {
            primaryKind = EditorActionKind.CancelRequest;
            label = "Hủy request";
        }
        else if (context.LocalQueueRunning)
        {
            primaryKind = EditorActionKind.PauseQueue;
            label = "Tạm dừng";
            overflow = [EditorActionKind.CancelQueue];
        }
        else if ((context.LocalQueueStatus is QueueStatus.Paused or QueueStatus.Interrupted or
                  QueueStatus.Cancelled or QueueStatus.Faulted) && (untranslated > 0 || errors > 0))
        {
            primaryKind = EditorActionKind.ResumeQueue;
            label = "Tiếp tục";
        }
        else if (!context.ProjectOpen && !context.ChapterSegmented)
        {
            if (context.RawSourceTextPresent)
            {
                primaryKind = EditorActionKind.SplitChapter;
                label = "Chia đoạn";
            }
            else
            {
                primaryKind = EditorActionKind.OpenProject;
                label = "Mở Project";
                secondary = [EditorActionKind.CreateProject];
            }
        }
        else if (context.ProjectOpen && context.ProjectChapterCount == 0)
        {
            primaryKind = EditorActionKind.ImportTxt;
            label = "Nhập TXT";
            secondary = [EditorActionKind.CreateChapter];
        }
        else if (!context.ChapterSegmented)
        {
            primaryKind = EditorActionKind.SplitChapter;
            label = "Chia đoạn";
            secondary = [EditorActionKind.EditSource];
        }
        else if (errors > 0)
        {
            primaryKind = EditorActionKind.NextError;
            label = "Mở lỗi kế tiếp";
            secondary = [EditorActionKind.RetryErrors];
        }
        else if (issues > 0)
        {
            primaryKind = EditorActionKind.NextInspection;
            label = "Kiểm tra vấn đề kế tiếp";
        }
        else if (untranslated > 0)
        {
            primaryKind = EditorActionKind.TranslateChapter;
            label = "Dịch chương";
            secondary = [EditorActionKind.TranslateSegment];
        }
        else if (context.SelectedSegmentStatus == SegmentStatus.AwaitingReview &&
                 context.SelectedSegmentHasEditableDraft)
        {
            primaryKind = EditorActionKind.ApproveAndNext;
            label = "Duyệt & Tiếp";
            secondary = [EditorActionKind.ApproveSegment, EditorActionKind.ShowHistory];
        }
        else if (awaitingReview > 0)
        {
            primaryKind = EditorActionKind.ApproveChapter;
            label = "Duyệt chương";
            if (context.SelectedSegmentHasEditableDraft)
                secondary = [EditorActionKind.ApproveAndNext];
        }
        else
        {
            primaryKind = EditorActionKind.NextUnfinishedChapter;
            label = "Chương chưa hoàn tất kế tiếp";
        }

        string? reason = DisabledReason(primaryKind, context);
        return new(new(primaryKind, label, reason is null, reason), secondary, overflow);
    }

    internal static string? DisabledReason(EditorActionKind kind, EditorActionContext context)
    {
        if (!context.UiAvailable)
            return "Đang đọc hoặc lưu tệp…";

        if ((kind is EditorActionKind.TranslateChapter or EditorActionKind.ResumeQueue or
             EditorActionKind.SplitChapter) && context.ProjectQueueRunning)
            return "Hàng đợi Project đang chạy; tạm dừng hàng đợi trước.";

        if ((kind is EditorActionKind.TranslateChapter or EditorActionKind.TranslateSegment or
             EditorActionKind.RetryErrors or EditorActionKind.SplitChapter) && !context.PresetsReady)
            return "Preset prompt chưa sẵn sàng.";

        return kind switch
        {
            EditorActionKind.OpenProject or EditorActionKind.CreateProject => null,
            EditorActionKind.ImportTxt or EditorActionKind.CreateChapter when !context.ProjectOpen =>
                "Hãy mở Project trước.",
            EditorActionKind.SplitChapter when !context.RawSourceTextPresent =>
                "Chưa có nguyên văn để chia đoạn.",
            EditorActionKind.EditSource when !context.ChapterSegmented => "Chưa có chương để sửa nguyên văn.",
            EditorActionKind.SaveSourceEdit when !context.SourceEditMode => "Chưa ở chế độ sửa nguyên văn.",
            EditorActionKind.CancelSourceEdit when !context.SourceEditMode => "Chưa ở chế độ sửa nguyên văn.",
            EditorActionKind.CancelRequest when !context.SingleRequestBusy => "Không có request đang chạy.",
            EditorActionKind.PauseQueue when !context.LocalQueueRunning => "Queue chương chưa chạy.",
            EditorActionKind.ResumeQueue when context.LocalQueueStatus is not (QueueStatus.Paused or
                QueueStatus.Interrupted or QueueStatus.Cancelled or QueueStatus.Faulted) =>
                "Queue chương không ở trạng thái có thể tiếp tục.",
            EditorActionKind.CancelQueue when !context.LocalQueueRunning => "Queue chương chưa chạy.",
            EditorActionKind.RetryErrors when context.Count(SegmentStatus.Error) == 0 => "Chương không có đoạn lỗi.",
            EditorActionKind.NextError when context.Count(SegmentStatus.Error) == 0 => "Chương không có đoạn lỗi.",
            EditorActionKind.NextInspection when context.Count(SegmentStatus.InspectionRequired) +
                context.RequiresRetranslationCount == 0 => "Chương không có đoạn cần kiểm tra.",
            EditorActionKind.TranslateChapter when !context.ChapterSegmented => "Chương chưa được chia đoạn.",
            EditorActionKind.TranslateChapter when context.Count(SegmentStatus.Untranslated) == 0 =>
                "Chương không còn đoạn chưa dịch.",
            EditorActionKind.TranslateSegment when context.SelectedSegmentStatus is null => "Chọn một đoạn trước.",
            EditorActionKind.ApproveAndNext or EditorActionKind.ApproveSegment
                when context.SelectedSegmentStatus is null => "Chọn một đoạn trước.",
            EditorActionKind.ApproveAndNext or EditorActionKind.ApproveSegment
                when !context.SelectedSegmentHasEditableDraft => "Đoạn đang chọn chưa có bản nháp để duyệt.",
            EditorActionKind.ApproveChapter when !context.ChapterSegmented => "Chưa có chương để duyệt.",
            EditorActionKind.ApproveChapter when context.Count(SegmentStatus.AwaitingReview) == 0 =>
                "Chương không có đoạn chờ duyệt.",
            EditorActionKind.ShowHistory when context.SelectedSegmentStatus is null => "Chọn một đoạn trước.",
            EditorActionKind.NextUnfinishedChapter when !context.ProjectOpen => "Chương đã duyệt hoàn toàn.",
            EditorActionKind.NextUnfinishedChapter when !context.NextUnfinishedProjectChapterExists =>
                "Mọi chương trong Project đã duyệt.",
            _ => null
        };
    }
}
