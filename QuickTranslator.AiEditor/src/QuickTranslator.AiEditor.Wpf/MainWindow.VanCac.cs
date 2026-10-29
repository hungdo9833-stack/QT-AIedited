using System.Windows;

namespace QuickTranslator.AiEditor.Wpf;

public partial class MainWindow
{
    private VanCacAccountManager? _vanCacAccount;

    private void InitializeVanCacAccount(VanCacAccountManager account)
    {
        _vanCacAccount = account;
        VanCacAccountStatus.Text = account.StatusText;
        account.Changed += OnVanCacAccountChanged;
        Closed += (_, _) => account.Changed -= OnVanCacAccountChanged;
    }

    private void OnVanCacAccountChanged(object? sender, EventArgs e)
    {
        if (_vanCacAccount is not null) VanCacAccountStatus.Text = _vanCacAccount.StatusText;
    }

    private void OnOpenVanCacAccount(object sender, RoutedEventArgs e)
    {
        try { new VanCacAccountWindow(_vanCacAccount ?? VanCacAccountManager.Default) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void OnOpenVanCacNovels(object sender, RoutedEventArgs e)
    {
        try
        {
            VanCacAccountManager account = _vanCacAccount ?? VanCacAccountManager.Default;
            if (!account.IsSignedIn) new VanCacAccountWindow(account) { Owner = this }.ShowDialog();
            if (account.IsSignedIn) new VanCacNovelsWindow(account, _projectSession) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private async void OnOpenVanCacPublication(object sender, RoutedEventArgs e)
    {
        try { await OpenProjectPublicationAsync(); }
        catch (Exception ex) { ShowError(ex); }
    }
}
