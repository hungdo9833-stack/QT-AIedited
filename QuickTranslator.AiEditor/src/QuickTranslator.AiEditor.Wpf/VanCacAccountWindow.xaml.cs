using System.Windows;
using QuickTranslator.AiEditor.Core.Publishing;

namespace QuickTranslator.AiEditor.Wpf;

public partial class VanCacAccountWindow : Window
{
    private readonly VanCacAccountManager _account;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy, _closed;

    internal VanCacAccountWindow(VanCacAccountManager account)
    {
        _account = account;
        InitializeComponent();
        MainWindow.ClampWindowToWorkArea(this, 500, 600);
        WebsiteInput.Text = string.IsNullOrWhiteSpace(account.Preferences.BaseUrl) ? "https://sacvien.com" : account.Preferences.BaseUrl;
        RefreshAccount();
        StatusText.Text = account.LocalError ?? (account.Token is not null && !account.IsSignedIn ? VanCacAccountManager.SessionExpiredMessage : "");
        Loaded += (_, _) => { if (!_account.IsSignedIn) EmailInput.Focus(); };
    }

    private void RefreshAccount()
    {
        bool signedIn = _account.IsSignedIn;
        SignInPanel.Visibility = SignInButton.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInPanel.Visibility = SignOutButton.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        DisplayNameText.Text = _account.AccountName;
        RolesText.Text = "Vai trò: " + string.Join(", ", _account.Preferences.Roles);
        WebsiteText.Text = "Website: " + _account.Preferences.BaseUrl;
        ExpiryText.Text = _account.Preferences.ExpiresAt is { } expiry
            ? "Hết hạn: " + expiry.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "Thời hạn: chưa có thông tin (mã kết nối thủ công).";
    }

    private void SetBusy(bool busy, string message)
    {
        _busy = busy;
        SignInPanel.IsEnabled = SignInButton.IsEnabled = SignOutButton.IsEnabled = !busy;
        BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = message;
    }

    private async void OnSignIn(object sender, RoutedEventArgs e) => await SignInAsync();

    internal async Task SignInAsync()
    {
        if (_busy || _account.IsSignedIn) return;
        if (string.IsNullOrWhiteSpace(EmailInput.Text) || PasswordInput.Password.Length == 0)
        { StatusText.Text = "Nhập email và mật khẩu để đăng nhập."; return; }
        SetBusy(true, "Đang đăng nhập…");
        try
        {
            await _account.LoginAsync(WebsiteInput.Text, EmailInput.Text, PasswordInput.Password, _lifetime.Token);
            PasswordInput.Clear();
            RefreshAccount();
            StatusText.Text = "Đã đăng nhập Sắc Viện.";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is VanCacApiException or ArgumentException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        { RefreshAccount(); StatusText.Text = ex.Message; }
        finally
        {
            PasswordInput.Clear();
            if (!_closed) SetBusy(false, StatusText.Text);
        }
    }

    private async void OnSignOut(object sender, RoutedEventArgs e) => await SignOutAsync();

    internal async Task SignOutAsync()
    {
        if (_busy) return;
        SetBusy(true, "Đang đăng xuất…");
        try
        {
            await _account.LogoutAsync(_lifetime.Token);
            RefreshAccount();
            StatusText.Text = "Đã đăng xuất và xóa mã khỏi máy này.";
            EmailInput.Focus();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
        { RefreshAccount(); StatusText.Text = ex.Message; }
        finally { if (!_closed) SetBusy(false, StatusText.Text); }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        PasswordInput.Clear();
        _lifetime.Cancel();
        // In-flight operations still inspect the source; dispose once they unwind with the window.
    }
}
