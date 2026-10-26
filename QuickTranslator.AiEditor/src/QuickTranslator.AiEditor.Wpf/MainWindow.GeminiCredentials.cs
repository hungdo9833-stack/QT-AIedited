using System.Windows;
using System.Windows.Controls;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    internal Func<IReadOnlyList<string>> GeminiApiKeysReader = GeminiApiKeyVault.ReadAll;
    internal Action<IReadOnlyList<string>> GeminiApiKeysSaver = GeminiApiKeyVault.SaveAll;

    private void OnGeminiCredentialStatusLoaded(object sender, RoutedEventArgs e) =>
        RefreshGeminiCredentialStatus();

    private void OnSaveGeminiApiKey(object sender, RoutedEventArgs e)
    {
        string[] entered = GeminiApiKeyInput.Text.Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entered.Length == 0)
        {
            GeminiCredentialStatus.Text = "Chưa nhập Gemini API key. Hãy nhập ít nhất một key rồi bấm Lưu.";
            return;
        }
        try
        {
            string[] next = GeminiApiKeysReader().Concat(entered)
                .Distinct(StringComparer.Ordinal).ToArray();
            GeminiApiKeysSaver(next);
            GeminiApiKeyInput.Clear();
            RefreshGeminiCredentialStatus();
            UpdateGpuBackendStatus();
            SetStatus($"Đã lưu {next.Length} Gemini API key trong Windows Credential Manager.");
        }
        catch (Exception ex)
        {
            GeminiApiKeyInput.Clear();
            ShowError(ex);
        }
    }

    private void OnRemoveGeminiApiKey(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int index }) return;
        if (ThemedDialog.Show(this, "Xóa Gemini API key này khỏi Windows? Các Project truyện sẽ không bị ảnh hưởng.",
            "Xóa Gemini API key", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            List<string> keys = GeminiApiKeysReader().ToList();
            if ((uint)index >= (uint)keys.Count) return;
            keys.RemoveAt(index);
            GeminiApiKeysSaver(keys);
            RefreshGeminiCredentialStatus();
            UpdateGpuBackendStatus();
            SetStatus("Đã xóa Gemini API key khỏi Windows Credential Manager.");
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshGeminiCredentialStatus()
    {
        try
        {
            IReadOnlyList<string> keys = GeminiApiKeysReader();
            GeminiSavedKeyList.ItemsSource = keys.Select((key, index) =>
                new GeminiSavedKeyItem(index, GeminiApiKeyVault.Mask(key))).ToArray();
            GeminiApiKeyInput.Clear();
            bool legacy = !string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY"));
            GeminiCredentialStatus.Text = keys.Count > 0
                ? $"Đã lưu {keys.Count} key dùng chung; thứ tự trên là thứ tự xoay vòng."
                : legacy
                    ? "Chưa có key lưu trong ứng dụng; khảo sát hiện dùng key từ biến môi trường Windows."
                    : "Chưa lưu Gemini API key. Hãy nhập key rồi bấm Lưu key mới.";
        }
        catch (Exception)
        {
            GeminiSavedKeyList.ItemsSource = null;
            GeminiApiKeyInput.Clear();
            GeminiCredentialStatus.Text = "Không kiểm tra được Windows Credential Manager.";
        }
    }

    private sealed record GeminiSavedKeyItem(int Index, string Masked);
}
