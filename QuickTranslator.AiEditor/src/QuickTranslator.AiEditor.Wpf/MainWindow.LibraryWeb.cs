using System.IO;
using System.Net.Http;
using QuickTranslator.AiEditor.Core.Publishing;

namespace QuickTranslator.AiEditor.Wpf;

// Library cards show how far each linked novel is on Sắc Viện ("Đã có trên web đến chương xx"), so many similar
// Projects are not confused. The last known state is cached in the publisher preferences and refreshed in the
// background whenever the library is shown while signed in.
public partial class MainWindow
{
    private static readonly TimeSpan LibraryWebRefreshInterval = TimeSpan.FromMinutes(2);
    private CancellationTokenSource? _startScreenWebRefresh;

    private IReadOnlyList<ProjectLibraryCard> WithWebStatus(IReadOnlyList<ProjectLibraryCard> cards)
    {
        VanCacPublisherPreferences? preferences = _vanCacAccount?.Preferences;
        if (preferences is null) return cards;
        return cards.Select(card => card with
        {
            WebLinked = preferences.NovelByProject.ContainsKey(card.ProjectId),
            Web = preferences.WebProgressByProject.GetValueOrDefault(card.ProjectId),
        }).ToArray();
    }

    /// <summary>Asks Sắc Viện for the chapters of every linked novel shown, then redraws the cards. Offline or
    /// signed out it does nothing: the cached line stays, with its check time in the tooltip.</summary>
    private async Task RefreshLibraryWebStatusAsync(ProjectLibraryView view, IReadOnlyList<ProjectLibraryCard> cards,
        Guid? activeProjectId, CancellationToken cancellationToken)
    {
        VanCacAccountManager? account = _vanCacAccount;
        if (account is not { IsSignedIn: true }) return;
        VanCacPublisherPreferences preferences = account.Preferences;
        DateTimeOffset now = DateTimeOffset.Now;
        ProjectLibraryCard[] stale = cards.Where(card => !card.Missing
            && preferences.NovelByProject.ContainsKey(card.ProjectId)
            && !(preferences.WebProgressByProject.GetValueOrDefault(card.ProjectId) is { } known
                 && now - known.CheckedAt < LibraryWebRefreshInterval)).ToArray();
        if (stale.Length == 0) return;

        bool changed = false;
        try
        {
            using VanCacPublisherClient client = account.CreateClient();
            foreach (ProjectLibraryCard card in stale)
            {
                try
                {
                    IReadOnlyList<VanCacRemoteChapter> chapters =
                        await client.ListChaptersAsync(preferences.NovelByProject[card.ProjectId], cancellationToken);
                    preferences.WebProgressByProject[card.ProjectId] = VanCacWebProgress.From(chapters, DateTimeOffset.Now);
                    changed = true;
                }
                catch (VanCacApiException ex) when (!ex.IsAuthFailure) { }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (VanCacApiException ex) when (ex.IsAuthFailure) { account.ExpireSession(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException) { }
        if (!changed) return;
        try { account.SavePreferences(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        if (cancellationToken.IsCancellationRequested) return;
        // Redraw from what the view shows now: projects deleted while the check ran must not come back.
        Guid? keep = (view.ProjectList.SelectedItem as ProjectLibraryCard)?.ProjectId ?? activeProjectId;
        view.SetProjects(WithWebStatus(view.Projects), keep);
    }

    private void StartScreenWebRefresh(ProjectLibraryView view, IReadOnlyList<ProjectLibraryCard> cards)
    {
        _startScreenWebRefresh?.Cancel();
        _startScreenWebRefresh = new CancellationTokenSource();
        _ = RefreshLibraryWebStatusAsync(view, cards, null, _startScreenWebRefresh.Token);
    }
}
