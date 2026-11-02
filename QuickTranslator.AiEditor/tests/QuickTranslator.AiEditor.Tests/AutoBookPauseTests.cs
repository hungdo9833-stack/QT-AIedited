using System.Runtime.CompilerServices;
using QuickTranslator.AiEditor.Core.AutoTranslate;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Tests;

public sealed class AutoBookPauseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-autobook-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(WebImportBlockReason.RequiresLogin, 2)]
    [InlineData(WebImportBlockReason.RateLimited, 0)]
    public async Task A_walled_off_book_pauses_without_a_project_or_a_survey(WebImportBlockReason reason, int downloaded)
    {
        var importer = new WalledImporter(total: 5, wallAt: downloaded, reason);
        bool surveyed = false;
        var preparer = new AutoBookPreparer(new ProjectRepository(), _ => importer,
            options => { surveyed = true; return new CodexSurveyRunner(options); }, null!)
        {
            WorkRoot = Path.Combine(_root, "work")
        };
        string projects = Path.Combine(_root, "projects");

        var paused = await Assert.ThrowsAsync<AutoBookPausedException>(() => preparer.PrepareAsync(
            new Uri("https://example.com/book"), projects, TimeSpan.Zero, new CodexSurveyOptions()));

        Assert.Equal((downloaded, 5, reason, "书名"), (paused.Downloaded, paused.Total, paused.Reason, paused.Title));
        Assert.Contains($"第{downloaded + 1}章", paused.Message);
        Assert.False(surveyed);
        Assert.False(Directory.Exists(projects) && Directory.EnumerateFileSystemEntries(projects).Any());
    }

    private sealed class WalledImporter(int total, int wallAt, WebImportBlockReason reason) : IWebNovelImporter
    {
        private readonly WebChapterLink[] _links = Enumerable.Range(1, total)
            .Select(i => new WebChapterLink(i, $"第{i}章 标题", new Uri($"https://example.com/book/{i}"))).ToArray();

        public Task<WebBookAnalysis> AnalyzeAsync(Uri url, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebBookAnalysis(url, "书名", _links, "", []));

        public Task<WebChapterContent> FetchChapterAsync(WebChapterLink link, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebChapterContent(link, link.Title, "正文。", 1));

        public async IAsyncEnumerable<WebChapterContent> FetchChaptersAsync(IReadOnlyList<WebChapterLink> links,
            IProgress<WebImportProgress>? progress = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (WebChapterLink link in links)
            {
                if (link.Index > wallAt) throw new WebImportBlockedException(link.Url, reason, "wall");
                yield return await FetchChapterAsync(link, cancellationToken);
            }
        }

        public string ComposeImportText(IReadOnlyList<WebChapterContent> chapters, bool keepSiteOrder = false) =>
            string.Join("\n", chapters.Select(c => c.Title + "\n" + c.Text));
    }
}
