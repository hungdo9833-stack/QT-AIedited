using System.IO;
using System.Windows;
using QuickTranslator.AiEditor.Core.Projects;

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

    private async void OnOpenVanCacBatchPublish(object sender, RoutedEventArgs e)
    {
        try
        {
            VanCacAccountManager account = _vanCacAccount ?? VanCacAccountManager.Default;
            if (!account.IsSignedIn) new VanCacAccountWindow(account) { Owner = this }.ShowDialog();
            if (!account.IsSignedIn) return;
            IReadOnlyList<ProjectLibraryCard> cards = await BuildProjectLibraryCardsAsync();
            VanCacBatchBook[] books = cards.Where(card => !card.Missing && card.Error is null)
                .Select(card => new VanCacBatchBook(card.ProjectId, card.Title, card.ManifestPath, card.ChapterCount,
                    card.CompletedChapterCount, card.WebLabel, card.WebLinked))
                .ToArray();
            var source = new VanCacBatchSource(new ProjectPublicationService(_projectRepository, _projectCatalog),
                BatchPublishBusyReason, OpenBatchPublishSessionAsync);
            new VanCacBatchPublishWindow(account, books, source) { Owner = this }.ShowDialog();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    // A Project whose queue is writing chapters would change under the upload (and a second session could race its
    // manifest writes), so it waits until translation stops.
    private string? BatchPublishBusyReason(VanCacBatchBook book)
    {
        if (IsTranslatingInBackground(book.ManifestPath)) return "đang dịch nền trong hàng chờ tự động";
        if (IsOpenInMainWindow(book) && _projectTranslationCoordinator?.IsRunning == true) return "hàng đợi dịch đang chạy";
        return null;
    }

    private bool IsOpenInMainWindow(VanCacBatchBook book) => _projectSession is not null &&
        string.Equals(Path.GetFullPath(_projectSession.ManifestPath), Path.GetFullPath(book.ManifestPath), StringComparison.OrdinalIgnoreCase);

    private async Task<ProjectSession> OpenBatchPublishSessionAsync(VanCacBatchBook book, CancellationToken cancellationToken) =>
        IsOpenInMainWindow(book) ? _projectSession! : (await _projectRepository.OpenAsync(book.ManifestPath, cancellationToken)).Session;
}
