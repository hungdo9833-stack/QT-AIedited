using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace QuickTranslator.AiEditor.Core.WebImport;

internal static class ChapterContentExtractor
{
    private const string Ignored = "script,style,noscript,nav,header,footer,aside,form,button,iframe,[hidden],[aria-hidden=true],.comments,.comment-list,#comments,.replies,.reply-list,.thread-list,.ad,.ads,.advertisement,.ad-container,.ad-banner";

    internal sealed record Extraction(string Text, string Signature, bool SignatureDiffered, bool SignatureCorrected);

    internal static string Extract(WebPage page) => ExtractWithSignature(page).Text;

    internal static Extraction ExtractWithSignature(WebPage page, string? expectedSignature = null,
        CancellationToken cancellationToken = default)
    {
        // Work on a clone: pagination and challenge detection still need the original document.
        var root = (IElement)(page.Document.Body ?? page.Document.DocumentElement).Clone(true);
        foreach (var ignored in root.QuerySelectorAll(Ignored)) ignored.Remove();
        foreach (var ignored in root.QuerySelectorAll("[class],[id]").Where(e =>
                     Regex.IsMatch(e.ClassName + " " + e.Id, @"(?:^|[\s_-])(?:advert|advertisement|ads|navigation|comment|replies)(?:$|[\s_-])", RegexOptions.IgnoreCase)).ToArray())
            ignored.Remove();

        var post = root.QuerySelector("div.content-section,.t_f,.post-body,.post-content");
        var candidates = root.QuerySelectorAll("article,main,section,div,td,pre,p").Prepend(root);
        var selected = post ?? candidates.OrderByDescending(Score).First();
        var signature = Signature(selected);
        var text = Clean(Lines(selected));
        var cjk = WebPage.CjkCount(text);
        if (page.Document.QuerySelectorAll("script").Any(s => s.TextContent.Contains("__next_f", StringComparison.Ordinal)))
        {
            var streamed = NextText(page, cancellationToken);
            var streamedCjk = WebPage.CjkCount(streamed);
            if (streamedCjk > cjk && (cjk < 50 || SentenceEnds(text) < 3 || streamedCjk >= cjk * 2))
            {
                text = streamed;
                signature = "next-flight:text";
            }
        }
        var differed = expectedSignature is not null && signature != expectedSignature;
        var corrected = false;
        if (differed)
        {
            // Compare signatures directly: identifiers are data, never interpolated into CSS selectors.
            var preferred = root.QuerySelectorAll("*").Prepend(root).Where(e => Signature(e) == expectedSignature)
                .Select(e => Clean(Lines(e))).Where(t => WebPage.CjkCount(t) >= 80)
                .OrderByDescending(WebPage.CjkCount).FirstOrDefault();
            if (preferred is not null)
            {
                text = preferred;
                signature = expectedSignature!;
                corrected = true;
            }
        }
        BlockDetector.CheckBrowser(page, WebPage.CjkCount(text));
        return new(text, signature, differed, corrected);
    }

    // Serialize tag, stable id and sorted class tokens for both the block and its immediate parent.
    // Discard digit-bearing, UUID/hash-like and long opaque ids rather than learning per-page selectors.
    private static string Signature(IElement element)
    {
        static bool Stable(string token) => token.Length <= 64 && !token.Any(char.IsDigit) &&
            !Regex.IsMatch(token, @"(?:^|[-_])[a-fA-F0-9]{8,}(?:$|[-_])|[a-zA-Z]{24,}");
        static string Part(IElement e) => JsonSerializer.Serialize(new[]
        {
            e.LocalName, e.Id is string id && Stable(id) ? id : "",
            string.Join(" ", e.ClassList.Order(StringComparer.Ordinal))
        });
        return (element.ParentElement is IElement parent ? Part(parent) : "[]") + ">" + Part(element);
    }

    private static double Score(IElement element)
    {
        var text = element.TextContent;
        var cjk = WebPage.CjkCount(text);
        var linkCjk = element.QuerySelectorAll("a").Sum(a => WebPage.CjkCount(a.TextContent));
        return Math.Max(0, cjk - linkCjk * 1.5) * (0.5 + (double)cjk / Math.Max(1, text.Length)) /
            (1 + element.QuerySelectorAll("div,section,article").Length * 0.02);
    }

    private static string Lines(INode node)
    {
        var output = new StringBuilder();
        void Visit(INode current)
        {
            if (current is IText text) { output.Append(text.Data); return; }
            var block = current is IElement e && Regex.IsMatch(e.LocalName, @"^(?:p|div|br|section|article|h[1-6]|li|pre|blockquote|tr)$");
            if (block) output.Append('\n');
            foreach (var child in current.ChildNodes) Visit(child);
            if (block) output.Append('\n');
        }
        Visit(node);
        return output.ToString();
    }

    internal static bool IsBoilerplate(string line) => Regex.IsMatch(line.Trim(),
        @"^(?:(?:https?://|www\.)\S+|(?:上一[章页頁]|下一[章页頁]|下页|下頁|返回目录|返回目錄|章节目录|章節目錄|目录|目錄|最新网址|最新網址|手机阅读|手機閱讀|加入书签|加入書籤|收藏本书|收藏本書|请收藏本站|請收藏本站|本站地址|本章未完|未完待续|未完待續).{0,70})$", RegexOptions.IgnoreCase);

    private static string Clean(string text) => WebPage.Normalize(string.Join("\n", WebPage.Normalize(text).Split('\n')
        .Select(l => l.Trim()).Where(l => l.Length > 0 && !IsBoilerplate(l))));

    internal static string[] RemoveRepeatedBoilerplate(IReadOnlyList<string> texts)
    {
        // Learn short labels at the edges of substantial bodies. Repetition alone is not enough:
        // headings, sentences and ordinary body paragraphs must survive, including repeated prose.
        var candidates = texts.Select(text =>
        {
            var lines = WebPage.Normalize(text).Split('\n').Where(l => l.Length > 0).ToArray();
            var longest = lines.Select(WebPage.CjkCount).DefaultIfEmpty().Max();
            return lines.Where((line, i) => (i < 2 || i >= lines.Length - 2) && line.Length <= 40 &&
                WebPage.CjkCount(text) >= 80 && WebPage.CjkCount(line) * 3 < longest &&
                !Regex.IsMatch(line, @"^\s*第[\d〇零一二三四五六七八九十百千万兩两]+[章回节節卷]|[。！？!?，,：:；;“”「」]")
            ).Distinct().ToArray();
        }).ToArray();
        var repeated = candidates.SelectMany(c => c).GroupBy(line => line).Where(g => g.Count() >= 2)
            .Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        return texts.Select((text, i) => WebPage.Normalize(string.Join("\n", text.Split('\n')
            .Where(line => !repeated.Contains(line) || !candidates[i].Contains(line))))).ToArray();
    }

    // Script payloads are untrusted and can be megabytes: every regex has a timeout and the loops honour cancellation.
    // A payload that cannot be read in time yields no streamed text, so the DOM text is used instead.
    private static string NextText(WebPage page, CancellationToken cancellationToken)
    {
        try { return NextTextCore(page, cancellationToken); }
        catch (RegexMatchTimeoutException) { return ""; }
    }

    private static string NextTextCore(WebPage page, CancellationToken cancellationToken)
    {
        var chunks = new List<string>();
        foreach (var script in page.Document.QuerySelectorAll("script"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (Match match in Regex.Matches(script.TextContent,
                         "self\\.__next_f\\.push\\(\\s*\\[\\s*1\\s*,\\s*(\"(?:\\\\.|[^\"\\\\])*\")\\s*\\]\\s*\\)", RegexOptions.Singleline,
                         WebPage.RegexTimeout))
            {
                try { chunks.Add(JsonSerializer.Deserialize<string>(match.Groups[1].Value) ?? ""); }
                catch (JsonException) { }
            }
        }
        // Flight text records use a UTF-8 byte length, so commas/newlines inside a chapter are safe.
        var payload = string.Concat(chunks);
        var runs = new List<string>();
        foreach (Match record in Regex.Matches(payload, @"(?:^|\n)[0-9a-f]+:T([0-9a-f]+),", RegexOptions.IgnoreCase, WebPage.RegexTimeout))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!int.TryParse(record.Groups[1].Value, System.Globalization.NumberStyles.HexNumber, null, out var byteLength)) continue;
            var rest = payload.AsSpan(record.Index + record.Length);
            var length = 0;
            var consumed = 0;
            foreach (var rune in rest.EnumerateRunes())
            {
                if (consumed + rune.Utf8SequenceLength > byteLength) break;
                consumed += rune.Utf8SequenceLength;
                length += rune.Utf16SequenceLength;
                if (consumed == byteLength) { runs.Add(rest[..length].ToString()); break; }
            }
        }
        // Some streams carry JSON strings in model records rather than Flight text records.
        foreach (Match quoted in Regex.Matches(payload, "\"(?:\\\\.|[^\"\\\\])*\"", RegexOptions.Singleline, WebPage.RegexTimeout))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { runs.Add(JsonSerializer.Deserialize<string>(quoted.Value) ?? ""); }
            catch (JsonException) { }
        }
        if (runs.All(s => WebPage.CjkCount(s) < 10))
            runs.AddRange(Regex.Split(payload, @"\n(?=[0-9a-f]+:)", RegexOptions.IgnoreCase, WebPage.RegexTimeout)
                .Select(s => Regex.Replace(s, @"^[0-9a-f]+:(?:T[0-9a-f]+,)?", "", RegexOptions.IgnoreCase, WebPage.RegexTimeout)));
        // Runs may carry the chapter as HTML (<p> paragraphs); read those as HTML so paragraphs stay lines.
        // Prefer prose: a chapter-title list can hold as many CJK characters as the chapter but almost no sentence ends.
        return runs.Select(HtmlRunToText).Select(Clean).Where(s => s.Length > 0 && (double)WebPage.CjkCount(s) / s.Length > 0.4)
            .OrderByDescending(s => SentenceEnds(s) >= 3 ? WebPage.CjkCount(s) : WebPage.CjkCount(s) / 10)
            .FirstOrDefault() ?? "";
    }

    private static string HtmlRunToText(string run)
    {
        if (!Regex.IsMatch(run, @"<(?:p|br|div)\b", RegexOptions.IgnoreCase)) return run;
        var document = new AngleSharp.Html.Parser.HtmlParser().ParseDocument("<body>" + run + "</body>");
        return Lines(document.Body!);
    }

    private static int SentenceEnds(string text) => text.Count(c => c is '。' or '！' or '？' or '!' or '?' or '…');

    internal static Uri? NextPage(WebPage page, Uri chapterUrl)
    {
        var chapterStem = Regex.Replace(chapterUrl.AbsolutePath, @"\.html?$", "", RegexOptions.IgnoreCase);
        var currentNumber = Regex.Match(page.Url.AbsolutePath, "^" + Regex.Escape(chapterStem) + @"_(\d+)\.html?$", RegexOptions.IgnoreCase);
        var currentPage = currentNumber.Success && int.TryParse(currentNumber.Groups[1].Value, out var n) ? n : 1;
        foreach (var anchor in page.Document.QuerySelectorAll("a[href]"))
        {
            var label = anchor.TextContent.Trim();
            if (Regex.IsMatch(label, @"下一[章回节節]")) continue;
            var url = WebPage.Resolve(page.Url, anchor.GetAttribute("href"));
            if (url is null || url == page.Url || !WebPage.SameHost(chapterUrl, url)) continue;
            var stemMatch = Regex.Match(url.AbsolutePath, @"^(.*)_(\d+)\.html?$", RegexOptions.IgnoreCase);
            var numbered = stemMatch.Success && stemMatch.Groups[1].Value == chapterStem &&
                           int.TryParse(stemMatch.Groups[2].Value, out var nextNumber) && nextNumber > currentPage;
            if (Regex.IsMatch(label, @"上一[页頁]|前一[页頁]")) continue;
            var nextLabel = Regex.IsMatch(label, @"^(?:下一页|下一頁|下页|下頁|下一分页|下一分頁|Next\s+page)[\s>»]*$", RegexOptions.IgnoreCase);
            // A labelled next-page link may use a query parameter; a numbered link must share the chapter stem.
            if (numbered || (nextLabel && url.AbsolutePath == chapterUrl.AbsolutePath && url.Query != chapterUrl.Query)) return url;
        }
        return null;
    }
}
