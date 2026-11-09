using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Importing;

namespace QuickTranslator.AiEditor.Core.WebImport;

internal static class ChapterListDetector
{
    internal static IReadOnlyList<WebChapterLink> Detect(WebPage page)
    {
        var links = page.Document.QuerySelectorAll("a[href]")
            .Select(a => new { Title = LinkTitle(a), Url = WebPage.Resolve(page.Url, a.GetAttribute("href")) })
            .Where(a => a.Url is not null && WebPage.SameHost(page.Url, a.Url) && a.Title.Length > 0)
            .Select(a => new WebChapterLink(0, a.Title, a.Url!))
            // A "开始阅读" button and the real "第1回 …" entry share one URL: keep the first position but the title that
            // reads like a chapter, or chapter 1 is imported under the button's text and loses its heading.
            .GroupBy(a => a.Url)
            // A numbered page entry ("第1页", seen live on 52shuku) beats the button too, or page 1 drops out of the list.
            .Select(g => g.First() with
            {
                Title = g.OrderByDescending(a => HasChapterNumber(a.Title)).ThenByDescending(LooksLikeChapter).First().Title
            })
            .ToList();

        if (IsThread(page.Url))
        {
            var currentTitle = page.Document.QuerySelector("h1")?.TextContent.Trim();
            if (BracketedBook(currentTitle ?? "") is null)
                currentTitle = links.FirstOrDefault(a => a.Url == page.Url)?.Title ?? Title(page);
            var book = BracketedBook(currentTitle ?? "");
            if (book is not null)
            {
                var siblings = links.Where(a => IsThread(a.Url) && BracketedBook(a.Title) == book && RangeStart(a.Title) is not null).ToList();
                if (RangeStart(currentTitle ?? "") is not null && siblings.All(a => a.Url != page.Url))
                    siblings.Add(new(0, currentTitle!, page.Url));
                if (siblings.Count >= 3) return Index(siblings.OrderBy(a => RangeStart(a.Title)));
            }
        }

        var group = links.Where(a => a.Url != page.Url && LooksLikeChapter(a))
            .GroupBy(a => UrlShape(a.Url))
            .Where(g => PlausibleSequence(g))
            .OrderByDescending(g => g.Count()).FirstOrDefault();
        if (group is null) return [];
        var ordered = group.ToList();
        var key = SequenceKey(ordered);
        var numbers = ordered.Select(key).ToArray();
        if (Descending(numbers) || Regex.IsMatch(page.Url.Query, @"(?:\?|&)sort=desc(?:&|$)", RegexOptions.IgnoreCase))
            ordered = ordered.OrderBy(key).ToList();
        return Index(ordered);
    }

    // Links of one table of contents differ only in their ids. Besides numbers, an opaque hex id (/book/55315/9556b73c60aec.html,
    // seen live) is an id too: replacing only its digit runs would give every chapter its own shape.
    internal static string UrlShape(Uri url) =>
        Regex.Replace(Regex.Replace(url.AbsolutePath + url.Query,
            @"(?<![0-9A-Za-z])(?=[0-9a-fA-F]*\d)(?=[0-9a-fA-F]*[a-fA-F])[0-9a-fA-F]{8,}(?![0-9A-Za-z])", "#"), @"\d+", "#");

    // Order by whichever numbering is more consistently sorted: titles usually (第一章…, even behind opaque URL ids), but
    // URL numbers when a title carries an unrelated number or an extra chapter (番外) out of place. Ties keep the titles.
    private static Func<WebChapterLink, long> SequenceKey(IReadOnlyCollection<WebChapterLink> links)
    {
        if (links.Select(UrlNumber).Distinct().Count() != links.Count) return Number;
        return Sortedness(links.Select(UrlNumber).ToArray()) > Sortedness(links.Select(Number).ToArray()) ? UrlNumber : Number;
    }

    private static double Sortedness(long[] numbers)
    {
        var pairs = numbers.Zip(numbers.Skip(1)).ToArray();
        if (pairs.Length == 0) return 1;
        return Math.Max(pairs.Count(p => p.First < p.Second), pairs.Count(p => p.First > p.Second)) / (double)pairs.Length;
    }

    private static long UrlNumber(WebChapterLink link)
    {
        var numbers = Regex.Matches(link.Url.AbsolutePath + link.Url.Query, @"\d+");
        return numbers.Count > 0 && long.TryParse(numbers[^1].Value, out var value) ? value : 0;
    }

    private static bool Descending(long[] numbers)
    {
        var pairs = numbers.Zip(numbers.Skip(1)).ToArray();
        return pairs.Length > 0 && pairs.Count(p => p.First >= p.Second) > pairs.Length / 2;
    }

    internal static Uri? DirectoryLink(WebPage page) => page.Document.QuerySelectorAll("a[href]")
        .Select(a => new { Url = WebPage.Resolve(page.Url, a.GetAttribute("href")), Text = a.TextContent.Trim() })
        .Where(a => a.Url is not null && WebPage.SameHost(page.Url, a.Url) && a.Url != page.Url)
        .FirstOrDefault(a => !a.Url!.Query.Contains("sort=desc", StringComparison.OrdinalIgnoreCase) &&
            (Regex.IsMatch(a.Url.AbsolutePath, @"(?:^|/)(?:dir\.html?|catalog|toc)(?:/|$)", RegexOptions.IgnoreCase) ||
             Regex.IsMatch(a.Text, @"^(?:目录|目錄|章节目录|章節目錄|全部章节|全部章節|查看目录|查看目錄|查看所有章节|查看所有章節|所有章节|所有章節|查看全部章节|查看全部章節)$")))?.Url;

    internal static string? Title(WebPage page)
    {
        var title = page.Document.QuerySelector("h1")?.TextContent.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = page.Document.QuerySelector("meta[property='og:title']")?.GetAttribute("content")?.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = page.Document.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (IsThread(page.Url) && BracketedBook(title) is string book) return book;
        title = Regex.Split(title, @"\s*[|｜_—]\s*|\s+-\s+")[0].Trim();
        // A status badge set inside the heading ("將門棄婦又震懾邊關了！熱文") is not part of the book's name.
        string clean = Regex.Replace(title, @"\s*(?:熱文|热文|熱門|热门|連載中?|连载中?|已完結|已完结|完結|完结|完本|全本|新書|新书)$", "").Trim();
        return clean.Length >= 2 ? clean : title;
    }

    private static bool IsThread(Uri uri) => Regex.IsMatch(uri.Query, @"(?:\?|&)act=threadview(?:&|$)", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(uri.Query, @"(?:\?|&)tid=\d+", RegexOptions.IgnoreCase);

    private static string? BracketedBook(string title) => Regex.Matches(title, @"[《【\[（(]([^》】\]）)]+)[》】\]）)]")
        .Select(m => m.Groups[1].Value.Trim()).FirstOrDefault(s => WebPage.CjkCount(s) > 0 && !Regex.IsMatch(s, @"^第?[\d一二三四五六七八九十百千\s-]+[章节章節回卷]?$"));

    private static long? RangeStart(string title)
    {
        var match = Regex.Match(title, @"(\d+)\s*[-－~～—至]\s*\d+");
        return match.Success && long.TryParse(match.Groups[1].Value, out var n) ? n : null;
    }

    // A table-of-contents row often packs several cells into one link (seen live: "第 2 章" + "第2回 标题" + "10-03" posting
    // date). Join the cells with spaces, drop a trailing date and a leading counter that only repeats the chapter's own 第N.
    internal static string LinkTitle(AngleSharp.Dom.IElement anchor)
    {
        var cells = new List<string>();
        void Collect(AngleSharp.Dom.INode node)
        {
            if (node is AngleSharp.Dom.IText text) { if (text.Data.Trim() is { Length: > 0 } cell) cells.Add(cell); return; }
            foreach (var child in node.ChildNodes) Collect(child);
        }
        Collect(anchor);
        string title = Regex.Replace(string.Join(" ", cells), @"\s+", " ").Trim();
        title = Regex.Replace(title, @"\s*(?:\d{4}[-/.])?\d{1,2}[-/.]\d{1,2}(?:\s+\d{1,2}:\d{2}(?::\d{2})?)?$", "").Trim();
        // A price cell ("0金幣", "免費", "VIP") is not part of the chapter's name.
        title = Regex.Replace(title, @"\s+(?:\d+\s*(?:金幣|金币|書幣|书币|幣|币|點|点)|免費|免费|VIP|vip)$", "").Trim();
        var counter = Regex.Match(title, @"^第\s*\d+\s*[章回节節]\s*(?=第\s*[\d〇零一二三四五六七八九十百千万兩两]+\s*[章回节節])");
        return counter.Success ? title[counter.Length..].Trim() : title;
    }

    /// <summary>
    /// The list holds numbered pages of one continuous text ("第1页", "第2页"…, seen live on 52shuku): chapters run across
    /// page breaks and their 第N章 headings are inside the text, so the pages must be joined, not imported one per chapter.
    /// </summary>
    internal static bool IsPageList(IReadOnlyCollection<WebChapterLink> links) => links.Count > 0 &&
        links.Count(l => Regex.IsMatch(l.Title.Trim(), @"^第\s*[0-9０-９]+\s*[页頁]$")) >= links.Count * 0.9;

    internal static bool HasChapterNumber(string title) =>
        ChapterNumber(title) is not null;

    internal static long? ChapterNumber(string title)
    {
        var match = Regex.Match(title, @"第\s*([0-9０-９〇○零一二三四五六七八九十百千万萬亿億廿卅兩两\s]+?)\s*[章回节節卷]");
        return match.Success && ChineseNumberParser.TryParse(match.Groups[1].Value, out int number) ? number : null;
    }

    private static bool LooksLikeChapter(WebChapterLink link) =>
        Regex.IsMatch(link.Title, @"第\s*[\d〇零一二三四五六七八九十百千万兩两]+\s*[章回节節卷]|\d+") ||
        Regex.IsMatch(link.Url.AbsolutePath, @"(?:chapter|read)[/_-]?\d", RegexOptions.IgnoreCase);

    private static bool PlausibleSequence(IEnumerable<WebChapterLink> group)
    {
        var links = group.ToArray();
        var numbers = links.Select(SequenceKey(links)).ToArray();
        if (numbers.Distinct().Count() < 3) return false;
        // Mostly ordered one way: a few out-of-place entries (extras, re-uploads) must not discard a whole table of contents.
        var pairs = numbers.Zip(numbers.Skip(1)).ToArray();
        return pairs.Count(p => p.First <= p.Second) >= pairs.Length * 0.9 ||
               pairs.Count(p => p.First >= p.Second) >= pairs.Length * 0.9;
    }

    private static long Number(WebChapterLink link)
    {
        if (ChapterNumber(link.Title) is long number) return number;
        var titleNumber = Regex.Match(link.Title, @"\d+");
        if (titleNumber.Success && long.TryParse(titleNumber.Value, out var value)) return value;
        var urlNumbers = Regex.Matches(link.Url.AbsolutePath + link.Url.Query, @"\d+");
        return urlNumbers.Count > 0 && long.TryParse(urlNumbers[^1].Value, out value) ? value : 0;
    }

    private static WebChapterLink[] Index(IEnumerable<WebChapterLink> links) => links.Select((a, i) => a with { Index = i + 1 }).ToArray();
}
