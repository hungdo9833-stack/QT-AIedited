using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.WebImport;

internal static class ChapterListDetector
{
    internal static IReadOnlyList<WebChapterLink> Detect(WebPage page)
    {
        var links = page.Document.QuerySelectorAll("a[href]")
            .Select(a => new { Title = a.TextContent.Trim(), Url = WebPage.Resolve(page.Url, a.GetAttribute("href")) })
            .Where(a => a.Url is not null && WebPage.SameHost(page.Url, a.Url) && a.Title.Length > 0)
            .Select(a => new WebChapterLink(0, a.Title, a.Url!)).DistinctBy(a => a.Url).ToList();

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
            .GroupBy(a => Regex.Replace(a.Url.AbsolutePath + a.Url.Query, @"\d+", "#"))
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
             Regex.IsMatch(a.Text, @"^(?:目录|目錄|章节目录|章節目錄|全部章节|全部章節|查看目录|查看目錄)$")))?.Url;

    internal static string? Title(WebPage page)
    {
        var title = page.Document.QuerySelector("h1")?.TextContent.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = page.Document.QuerySelector("meta[property='og:title']")?.GetAttribute("content")?.Trim();
        if (string.IsNullOrWhiteSpace(title)) title = page.Document.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title)) return null;
        if (IsThread(page.Url) && BracketedBook(title) is string book) return book;
        return Regex.Split(title, @"\s*[|｜_—]\s*|\s+-\s+")[0].Trim();
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
        var chinese = Regex.Match(link.Title, @"第\s*([〇零一二三四五六七八九十百千万兩两]+)\s*[章回节節卷]");
        if (chinese.Success) return ChineseNumber(chinese.Groups[1].Value);
        var titleNumber = Regex.Match(link.Title, @"\d+");
        if (titleNumber.Success && long.TryParse(titleNumber.Value, out var value)) return value;
        var urlNumbers = Regex.Matches(link.Url.AbsolutePath + link.Url.Query, @"\d+");
        return urlNumbers.Count > 0 && long.TryParse(urlNumbers[^1].Value, out value) ? value : 0;
    }

    private static long ChineseNumber(string text)
    {
        long total = 0, section = 0, digit = 0;
        foreach (var c in text)
        {
            var value = c switch { '一' => 1, '二' or '两' or '兩' => 2, '三' => 3, '四' => 4, '五' => 5, '六' => 6, '七' => 7, '八' => 8, '九' => 9, '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, _ => 0 };
            if (value < 10) digit = digit * 10 + value;
            else if (value == 10000) { total += (section + digit) * value; section = digit = 0; }
            else { section += (digit == 0 ? 1 : digit) * value; digit = 0; }
        }
        return total + section + digit;
    }

    private static WebChapterLink[] Index(IEnumerable<WebChapterLink> links) => links.Select((a, i) => a with { Index = i + 1 }).ToArray();
}
