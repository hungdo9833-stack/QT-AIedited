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
            bool protectedConfirmation = RequiresSourceEditConfirmation(preview);
            if (protectedConfirmation && !ShowSourceEditPreview(preview)) return;
            await _service.ApplySourceEditAsync(preview, protectedConfirmation);
            EndSourceEditMode(assignSnapshot: true);
            RefreshProjectChapterList();
            Refresh();
            SetStatus(preview.AffectedSegmentCount == 0
                ? "Đã lưu nguyên văn."
                : "Đã lưu nguyên văn; các đoạn bị ảnh hưởng đã được đánh dấu để kiểm tra.");
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
            .AppendLine($"Thay đổi này ảnh hưởng {preview.AffectedSegmentCount:N0} đoạn; " +
                $"{preview.ProtectedAffectedSegmentCount:N0} đoạn đã có bản dịch, bản nháp hoặc phê duyệt.")
            .AppendLine("Bản cũ vẫn được giữ trong lịch sử và các đoạn liên quan sẽ cần kiểm tra lại.")
            .AppendLine().AppendLine("Nội dung bỏ đi:").AppendLine(PreviewChange(preview.Change.RemovedText))
            .AppendLine().AppendLine("Nội dung thêm vào:").AppendLine(PreviewChange(preview.Change.AddedText))
            .AppendLine().Append("Bạn có muốn lưu thay đổi này?").ToString();
        return MessageBox.Show(this, details, "Sửa nguyên văn ảnh hưởng bản dịch", MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    internal static bool RequiresSourceEditConfirmation(SourceEditPreview preview) =>
        preview.ProtectedAffectedSegmentCount > 0 || preview.ReplacesMostOfSource;

    private static string PreviewChange(string value)
    {
        const int limit = 700;
        if (value.Length <= limit) return string.IsNullOrEmpty(value) ? "(trống)" : value;
        return value[..limit] + $"\n… ({value.Length - limit:N0} ký tự nữa)";
    }
}
