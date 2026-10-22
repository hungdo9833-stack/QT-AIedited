using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.WebImport;

/// <summary>Imports server-rendered novel pages without executing scripts or passing gates.</summary>
public sealed class WebNovelImporter : IWebNovelImporter, IDisposable
{
    private const string HeadingPattern = @"第\s*[\d〇零一二三四五六七八九十百千万兩两]+\s*[章回节節卷]";
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
        var numbers = chapters.Select(c => ChapterListDetector.ChapterNumber(c.Title)).OfType<long>().ToArray();
        bool numberingConflict = numbers.Distinct().Count() != numbers.Length || numbers.Zip(numbers.Skip(1)).Any(p => p.First > p.Second);
        return new(url, title ?? ChapterListDetector.Title(page), chapters,
            $"Tìm thấy {chapters.Count} chương theo cấu trúc liên kết" + (followed ? " trong trang mục lục." : "."),
            numberingConflict ? ["Mục lục có chương ngoại truyện/số chương trùng hoặc lệch thứ tự; nên bật “Giữ đúng thứ tự mục lục”."] : [],
            numberingConflict);
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

    public string ComposeImportText(IReadOnlyList<WebChapterContent> chapters, bool keepSiteOrder = false)
    {
        var texts = ChapterContentExtractor.RemoveRepeatedBoilerplate(chapters.Select(c => WebPage.Normalize(c.Text)).ToArray());
        return string.Join("\n\n", chapters.Select((chapter, i) =>
            keepSiteOrder ? SiteOrderText(chapter.Link, texts[i]) :
            IsHeading(texts[i]) ? texts[i] : Heading(chapter.Link) + (texts[i].Length > 0 ? "\n" + texts[i] : "")));
    }

    private static string SiteOrderText(WebChapterLink link, string text)
    {
        string title = WebPage.Normalize(link.Title);
        if (IsHeading(text))
        {
            int end = text.IndexOf('\n');
            title = end < 0 ? text : text[..end];
            text = end < 0 ? "" : text[(end + 1)..];
        }
        title = Regex.Replace(title, @"^(?:\s*" + HeadingPattern + @")+\s*", "").Trim();
        return $"第{link.Index}章 {title}".TrimEnd() + (text.Length > 0 ? "\n" + text : "");
    }

    // The TXT import splits on 第N章-style lines; a title without a number (e.g. "楔子", a site button) would leave the
    // chapter outside every chapter, so it gets its list position as the number.
    private static string Heading(WebChapterLink link)
    {
        string title = WebPage.Normalize(link.Title);
        return ChapterListDetector.HasChapterNumber(title) ? title : $"第{link.Index}章 {title}".TrimEnd();
    }

    private static bool IsHeading(string text) => Regex.IsMatch(text, @"^\s*" + HeadingPattern);

    public void Dispose() => _fetcher.Dispose();
}
