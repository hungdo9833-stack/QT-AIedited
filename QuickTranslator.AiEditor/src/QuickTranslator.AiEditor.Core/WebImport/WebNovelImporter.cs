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
        var warnings = new List<string>();
        if (numberingConflict)
            warnings.Add("Mục lục có chương ngoại truyện/số chương trùng hoặc lệch thứ tự; nên bật “Giữ đúng thứ tự mục lục”.");
        var sampleCheck = await CheckSamplesAsync(chapters, warnings, cancellationToken).ConfigureAwait(false);
        if (sampleCheck.Signature is string signature)
            chapters = chapters.Select(c => c with { ExpectedBlockSignature = signature }).ToArray();
        return new(url, title ?? ChapterListDetector.Title(page), chapters,
            $"Tìm thấy {chapters.Count} chương theo cấu trúc liên kết" + (followed ? " trong trang mục lục." : "."),
            warnings, numberingConflict, sampleCheck);
    }

    private async Task<WebSampleCheck> CheckSamplesAsync(IReadOnlyList<WebChapterLink> chapters,
        List<string> warnings, CancellationToken token)
    {
        var links = chapters.Count < 4 ? chapters.Take(2).ToArray() : new[] { chapters[0], chapters[chapters.Count / 2] };
        var samples = new List<WebChapterContent>();
        var reasons = new List<WebSampleCheckReason>();
        var indexes = links.Select(c => c.Index).ToArray();
        string label = string.Join(", ", indexes);
        void Warn(WebSampleCheckReason reason, string message)
        {
            reasons.Add(reason);
            warnings.Add($"Chương {label}: {message}");
        }
        foreach (var link in links)
        {
            try { samples.Add(await FetchChapterAsync(link, token).ConfigureAwait(false)); }
            catch (WebImportBlockedException) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException ||
                                       ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                // A failed probe is advisory. Do not put server messages, page text or URLs in warnings.
                Warn(WebSampleCheckReason.FetchFailed, $"không kiểm tra được mẫu chương {link.Index}.");
            }
        }
        token.ThrowIfCancellationRequested();
        string? agreedSignature = null;
        if (samples.Any(c => WebPage.CjkCount(c.Text) < 80))
            Warn(WebSampleCheckReason.InsufficientCjk, "mẫu có quá ít chữ Hán (dưới 80).");
        if (samples.Count == 2)
        {
            var first = samples[0];
            var second = samples[1];
            if (first.BlockSignature == second.BlockSignature) agreedSignature = first.BlockSignature;
            else Warn(WebSampleCheckReason.SignatureMismatch, "hai mẫu chọn khối nội dung khác nhau.");
            int smaller = Math.Min(first.Text.Length, second.Text.Length);
            int larger = Math.Max(first.Text.Length, second.Text.Length);
            if (larger > smaller * 8L)
                Warn(WebSampleCheckReason.LengthRatio, "độ dài hai mẫu chênh lệch hơn 8 lần.");
            if (WebImportQualityReport.NormalizeForComparison(first.PageTitle ?? first.Title) ==
                WebImportQualityReport.NormalizeForComparison(second.PageTitle ?? second.Title))
                Warn(WebSampleCheckReason.DuplicateTitles, "hai mẫu có tiêu đề trùng nhau.");
            if (WebImportQualityReport.ContentHash(first.Text) == WebImportQualityReport.ContentHash(second.Text))
                Warn(WebSampleCheckReason.DuplicateContent, "hai mẫu có nội dung trùng nhau.");
        }
        else if (links.Length < 2)
            Warn(WebSampleCheckReason.NotEnoughSamples, "chưa đủ hai chương để đối chiếu mẫu.");
        return new(reasons.Count == 0, agreedSignature, reasons, indexes);
    }

    public async Task<WebChapterContent> FetchChapterAsync(WebChapterLink link, CancellationToken cancellationToken = default)
    {
        var url = link.Url;
        var visited = new HashSet<Uri>();
        var parts = new List<string>();
        string? signature = null;
        string? pageTitle = null;
        int differingPages = 0, correctedPages = 0;
        while (visited.Add(url))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _fetcher.FetchAsync(url, cancellationToken).ConfigureAwait(false);
            var extraction = ChapterContentExtractor.ExtractWithSignature(page, link.ExpectedBlockSignature);            if (parts.Count == 0)
            {
                signature = extraction.Signature;
                pageTitle = page.Document.QuerySelectorAll("h1,h2,h3").FirstOrDefault(e => ChapterListDetector.HasChapterNumber(e.TextContent))?.TextContent.Trim()
                    ?? page.Document.QuerySelector("meta[property='og:title']")?.GetAttribute("content")?.Trim()
                    ?? (string.IsNullOrWhiteSpace(page.Document.Title) ? null : page.Document.Title.Trim())
                    ?? page.Document.QuerySelector("h1")?.TextContent.Trim();
            }
            if (extraction.SignatureDiffered) differingPages++;
            if (extraction.SignatureCorrected) correctedPages++;
            parts.Add(extraction.Text);
            var next = ChapterContentExtractor.NextPage(page, link.Url);
            if (next is null || visited.Contains(next)) break;
            url = next;
        }
        var joined = WebPage.Normalize(string.Join("\n\n", ChapterContentExtractor.RemoveRepeatedBoilerplate(parts)));
        return new(link, link.Title, joined, parts.Count)
        {
            BlockSignature = signature, PageTitle = pageTitle,
            BlockSignatureDiffered = differingPages > 0,
            BlockSignatureCorrected = differingPages > 0 && correctedPages == differingPages
        };
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
