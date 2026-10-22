using System.Windows;
using System.Windows.Input;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    internal Func<string?> GeminiApiKeyReader = GeminiApiKeyVault.Read;
    internal Action<string> GeminiApiKeySaver = GeminiApiKeyVault.Save;
    internal Action GeminiApiKeyDeleter = GeminiApiKeyVault.Delete;

    private bool _hasSavedGeminiApiKey;
    private bool _isEditingGeminiApiKey;

    private void OnGeminiCredentialStatusLoaded(object sender, RoutedEventArgs e) =>
        RefreshGeminiCredentialStatus();

    private void OnSaveGeminiApiKey(object sender, RoutedEventArgs e)
    {
        string apiKey = GeminiApiKeyInput.Password;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            GeminiCredentialStatus.Text = _hasSavedGeminiApiKey
                ? "Key đã lưu vẫn được giữ nguyên. Hãy nhập key mới hoặc bấm Hủy."
                : "Chưa nhập Gemini API key. Hãy nhập key rồi bấm Lưu.";
            return;
        }

        try
        {
            GeminiApiKeySaver(apiKey);
            GeminiApiKeyInput.Clear();
            _hasSavedGeminiApiKey = true;
            _isEditingGeminiApiKey = false;
            ApplyGeminiCredentialState();
            SetStatus("Đã lưu Gemini API key trong Windows Credential Manager. Không cần PowerShell.");
        }
        catch (Exception ex)
        {
            // Do not show, log, or retain entered API key in the UI on failure.
            GeminiApiKeyInput.Clear();
            ShowError(ex);
        }
    }

    private void OnEditGeminiApiKey(object sender, RoutedEventArgs e)
    {
        if (!_hasSavedGeminiApiKey) return;

        GeminiApiKeyInput.Clear();
        _isEditingGeminiApiKey = true;
        ApplyGeminiCredentialState();
        GeminiApiKeyInput.Focus();
    }

    private void OnCancelGeminiApiKeyEdit(object sender, RoutedEventArgs e) =>
        CancelGeminiApiKeyEdit();

    private void OnGeminiApiKeyInputPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_isEditingGeminiApiKey || !_hasSavedGeminiApiKey) return;

        CancelGeminiApiKeyEdit();
        e.Handled = true;
    }

    private void CancelGeminiApiKeyEdit()
    {
        if (!_isEditingGeminiApiKey || !_hasSavedGeminiApiKey) return;

        GeminiApiKeyInput.Clear();
        _isEditingGeminiApiKey = false;
        ApplyGeminiCredentialState();
        GeminiEditApiKeyButton.Focus();
    }

    private void OnRemoveGeminiApiKey(object sender, RoutedEventArgs e)
    {
        if (!_hasSavedGeminiApiKey) return;

        if (ThemedDialog.Show(this, "Xóa Gemini API key đã lưu trên Windows? Các Project truyện sẽ không bị ảnh hưởng.",
            "Xóa Gemini API key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            GeminiApiKeyDeleter();
            GeminiApiKeyInput.Clear();
            _hasSavedGeminiApiKey = false;
            _isEditingGeminiApiKey = false;
            ApplyGeminiCredentialState();
            SetStatus("Đã xóa Gemini API key khỏi Windows Credential Manager.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshGeminiCredentialStatus()
    {
        try
        {
            _hasSavedGeminiApiKey = !string.IsNullOrWhiteSpace(GeminiApiKeyReader());
            _isEditingGeminiApiKey = false;
            GeminiApiKeyInput.Clear();
            ApplyGeminiCredentialState();
        }
        catch (Exception)
        {
            _hasSavedGeminiApiKey = false;
            _isEditingGeminiApiKey = false;
            GeminiApiKeyInput.Clear();
            ApplyGeminiCredentialState("Không kiểm tra được Windows Credential Manager.");
        }
    }

    private void ApplyGeminiCredentialState(string? statusOverride = null)
    {
        bool showEditor = !_hasSavedGeminiApiKey || _isEditingGeminiApiKey;
        GeminiApiKeyInput.Visibility = showEditor ? Visibility.Visible : Visibility.Collapsed;
        GeminiSavedKeyPlaceholder.Visibility = showEditor ? Visibility.Collapsed : Visibility.Visible;
        GeminiSaveApiKeyButton.IsEnabled = showEditor;
        GeminiEditApiKeyButton.IsEnabled = _hasSavedGeminiApiKey && !_isEditingGeminiApiKey;
        GeminiCancelApiKeyEditButton.Visibility = _isEditingGeminiApiKey ? Visibility.Visible : Visibility.Collapsed;
        GeminiRemoveApiKeyButton.Visibility = _isEditingGeminiApiKey && _hasSavedGeminiApiKey
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (statusOverride is not null)
        {
            GeminiCredentialStatus.Text = statusOverride;
            return;
        }

        bool legacy = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY"));
        GeminiCredentialStatus.Text = _isEditingGeminiApiKey
            ? "Đang sửa API key. Nhập key mới rồi bấm Lưu, hoặc bấm Hủy để giữ key hiện tại."
            : _hasSavedGeminiApiKey
                ? "Đã lưu một API key trên Windows. Khảo sát Gemini sẽ tự sử dụng key này."
                : legacy
                    ? "Chưa có key lưu trong ứng dụng; hiện đang dùng key từ biến môi trường Windows."
                    : "Chưa lưu Gemini API key. Hãy nhập key rồi bấm Lưu.";
    }
}
