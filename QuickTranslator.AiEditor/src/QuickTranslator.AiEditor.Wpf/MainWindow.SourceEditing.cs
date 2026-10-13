using System.Text;
using System.Windows;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private bool _sourceEditMode;
    private string? _sourceBeforeEdit;

    private bool HasPendingSourceEdit => _sourceEditMode && _sourceBeforeEdit is not null &&
        !string.Equals(ChapterInput.Text, _sourceBeforeEdit, StringComparison.Ordinal);

    private void OnBeginSourceEdit(object sender, RoutedEventArgs e)
    {
        if (_service is null || WorkActive || _projectTranslationCoordinator?.IsRunning == true || _uiOperation) return;
        ChapterDocument chapter = _service.GetSnapshot();
        _sourceBeforeEdit = chapter.OriginalText;
        _sourceEditMode = true;
        UpdateButtons();
        ChapterInput.Focus();
        SetStatus("Đang sửa nguyên văn. Nội dung chỉ được ghi sau khi xem preview và xác nhận.");
    }

    private async void OnSaveSourceEdit(object sender, RoutedEventArgs e)
    {
        if (_service is null || !_sourceEditMode || WorkActive || _projectTranslationCoordinator?.IsRunning == true) return;
        if (!BeginUiOperation()) return;
        try
        {
            SourceEditPreview preview = _service.PreviewSourceEdit(ChapterInput.Text, ReadSegmenterOptions());
            if (!ShowSourceEditPreview(preview)) return;
            bool protectedConfirmation = false;
            if (preview.ProtectedAffectedSegmentCount > 0 || preview.ReplacesMostOfSource)
            {
                string warning = preview.ProtectedAffectedSegmentCount > 0
                    ? $"Thay đổi chạm {preview.ProtectedAffectedSegmentCount:N0} segment có bản dịch, nháp, duyệt hoặc lịch sử. Dữ liệu cũ sẽ được lưu nguyên trong Source Edit History; segment mới cần kiểm tra."
                    : "Thay đổi thay thế phần lớn nguyên văn. Bản nguồn và toàn bộ cấu trúc cũ sẽ được lưu trong Source Edit History.";
                protectedConfirmation = MessageBox.Show(this, warning + "\n\nTiếp tục lưu nguyên văn?",
                    "Xác nhận riêng dữ liệu được bảo vệ", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                    MessageBoxResult.No) == MessageBoxResult.Yes;
                if (!protectedConfirmation) return;
            }
            await _service.ApplySourceEditAsync(preview, protectedConfirmation);
            EndSourceEditMode(assignSnapshot: true);
            RefreshProjectChapterList();
            Refresh();
            SetStatus("Đã lưu nguyên văn bằng transaction an toàn. Segment không ánh xạ chắc chắn được đánh dấu cần kiểm tra.");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { EndUiOperation(); }
    }

    private void OnCancelSourceEdit(object sender, RoutedEventArgs e)
    {
        if (!_sourceEditMode) return;
        EndSourceEditMode(assignSnapshot: true);
        Refresh();
        SetStatus("Đã hủy sửa nguyên văn; dữ liệu chương không thay đổi.");
    }

    private void EndSourceEditMode(bool assignSnapshot)
    {
        _sourceEditMode = false;
        _sourceBeforeEdit = null;
        if (assignSnapshot && _service is not null) AssignOriginalText(_service.GetSnapshot().OriginalText);
        UpdateButtons();
    }

    private bool ShowSourceEditPreview(SourceEditPreview preview)
    {
        string details = new StringBuilder()
            .AppendLine($"Segment: {preview.OldSegmentCount:N0} → {preview.NewSegmentCount:N0}")
            .AppendLine($"Giữ nguyên/ánh xạ được: {preview.RetainedSegmentCount:N0}")
            .AppendLine($"Bị ảnh hưởng: {preview.AffectedSegmentCount:N0}")
            .AppendLine($"Có dữ liệu dịch/duyệt: {preview.ProtectedAffectedSegmentCount:N0}")
            .AppendLine().AppendLine("Vùng bị bỏ:").AppendLine(PreviewChange(preview.Change.RemovedText))
            .AppendLine().AppendLine("Vùng thêm vào:").AppendLine(PreviewChange(preview.Change.AddedText))
            .AppendLine().Append("Nguồn mới sẽ được tái tạo đúng từng ký tự trước khi commit.").ToString();
        return MessageBox.Show(this, details, "Preview sửa nguyên văn", MessageBoxButton.OKCancel,
            preview.ProtectedAffectedSegmentCount > 0 || preview.ReplacesMostOfSource
                ? MessageBoxImage.Warning : MessageBoxImage.Information,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private static string PreviewChange(string value)
    {
        const int limit = 700;
        if (value.Length <= limit) return string.IsNullOrEmpty(value) ? "(trống)" : value;
        return value[..limit] + $"\n… ({value.Length - limit:N0} ký tự nữa)";
    }
}
