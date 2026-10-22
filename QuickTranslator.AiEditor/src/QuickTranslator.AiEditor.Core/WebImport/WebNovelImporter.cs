using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.WebImport;

/// <summary>Imports server-rendered novel pages without executing scripts or passing gates.</summary>
public sealed class WebNovelImporter : IWebNovelImporter, IDisposable
{
    private readonly PageFetcher _fetcher;

    public WebNovelImporter(WebImportOptions options, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _fetcher = new(options, handler);
    }

    public async Task<WebBookAnalysis> AnalyzeAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var page = await _fetcher.FetchAsync(url, cancellationToken, useCache: false).ConfigureAwait(false);
        var title = ChapterListDetector.Title(page);
        var chapters = ChapterListDetector.Detect(page);
        var followed = false;
        if (chapters.Count < 10 && ChapterListDetector.DirectoryLink(page) is Uri directory)
        {
            page = await _fetcher.FetchAsync(directory, cancellationToken, useCache: false).ConfigureAwait(false);
            var directoryChapters = ChapterListDetector.Detect(page);
            if (directoryChapters.Count > chapters.Count) { chapters = directoryChapters; followed = true; }
        }
        if (chapters.Count == 0)
        {
            BlockDetector.CheckBrowser(page, WebPage.CjkCount(ChapterContentExtractor.Extract(page)));
            throw BlockDetector.Block(page.Url, WebImportBlockReason.NoChapterList);
        }
        return new(url, title ?? ChapterListDetector.Title(page), chapters,
            $"Tìm thấy {chapters.Count} chương theo cấu trúc liên kết" + (followed ? " trong trang mục lục." : "."), []);
    }

    public async Task<WebChapterContent> FetchChapterAsync(WebChapterLink link, CancellationToken cancellationToken = default)
    {
        var url = link.Url;
        var visited = new HashSet<Uri>();
        var parts = new List<string>();
        while (visited.Add(url))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _fetcher.FetchAsync(url, cancellationToken).ConfigureAwait(false);
            var text = ChapterContentExtractor.Extract(page);
            if (string.IsNullOrWhiteSpace(text)) throw BlockDetector.Block(page.Url, WebImportBlockReason.RequiresBrowser);
            parts.Add(text);
            var next = ChapterContentExtractor.NextPage(page, link.Url);
            if (next is null || visited.Contains(next)) break;
            url = next;
        }
        var joined = WebPage.Normalize(string.Join("\n\n", ChapterContentExtractor.RemoveRepeatedBoilerplate(parts)));
        return new(link, link.Title, joined, parts.Count);
    }

    public async IAsyncEnumerable<WebChapterContent> FetchChaptersAsync(IReadOnlyList<WebChapterLink> links,
        IProgress<WebImportProgress>? progress = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        for (var i = 0; i < links.Count; i++)
        {
            var chapter = await FetchChapterAsync(links[i], cancellationToken).ConfigureAwait(false);
            progress?.Report(new(i + 1, links.Count, chapter.Title));
            yield return chapter;
        }
    }

    public string ComposeImportText(IReadOnlyList<WebChapterContent> chapters)
    {
        var texts = ChapterContentExtractor.RemoveRepeatedBoilerplate(chapters.Select(c => WebPage.Normalize(c.Text)).ToArray());
        return string.Join("\n\n", chapters.Select((chapter, i) =>
            IsHeading(texts[i]) ? texts[i] : WebPage.Normalize(chapter.Link.Title) + (texts[i].Length > 0 ? "\n" + texts[i] : "")));
    }

    private static bool IsHeading(string text) => Regex.IsMatch(text,
        @"^\s*第\s*[\d〇零一二三四五六七八九十百千万兩两]+\s*[章回节節卷]");

    public void Dispose() => _fetcher.Dispose();
}
