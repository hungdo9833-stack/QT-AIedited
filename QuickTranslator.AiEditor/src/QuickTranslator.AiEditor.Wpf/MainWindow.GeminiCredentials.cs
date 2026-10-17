using System.Windows;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private void OnGeminiCredentialStatusLoaded(object sender, RoutedEventArgs e) =>
        RefreshGeminiCredentialStatus();

    private void OnSaveGeminiApiKey(object sender, RoutedEventArgs e)
    {
        try
        {
            GeminiApiKeyVault.Save(GeminiApiKeyInput.Password);
            GeminiApiKeyInput.Clear();
            RefreshGeminiCredentialStatus();
            SetStatus("Đã lưu Gemini API key trong Windows Credential Manager. Không cần PowerShell.");
        }
        catch (Exception ex)
        {
            // Do not show, log, or retain entered API key in the UI on failure.
            GeminiApiKeyInput.Clear();
            ShowError(ex);
        }
    }

    private void OnRemoveGeminiApiKey(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Xóa Gemini API key đã lưu trên Windows? Các Project truyện sẽ không bị ảnh hưởng.",
            "Xóa Gemini API key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            GeminiApiKeyVault.Delete();
            GeminiApiKeyInput.Clear();
            RefreshGeminiCredentialStatus();
            SetStatus("Đã xóa Gemini API key khỏi Windows Credential Manager.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshGeminiCredentialStatus()
    {
        try
        {
            bool saved = !string.IsNullOrWhiteSpace(GeminiApiKeyVault.Read());
            bool legacy = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY"));
            GeminiCredentialStatus.Text = saved
                ? "Đã lưu một API key trên Windows. Khảo sát Gemini sẽ tự sử dụng key này."
                : legacy
                    ? "Chưa có key lưu trong ứng dụng; hiện đang dùng key từ biến môi trường Windows."
                    : "Chưa lưu Gemini API key. Hãy nhập key rồi bấm Lưu.";
        }
        catch (Exception)
        {
            GeminiCredentialStatus.Text = "Không kiểm tra được Windows Credential Manager.";
        }
    }
}
