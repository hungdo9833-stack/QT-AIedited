using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.WebImport;

internal static class BlockDetector
{
    internal static void Check(WebPage page)
    {
        if (HtmlMatches(page, @"cf-chl|challenge-platform|\bjust a moment\b|g-recaptcha|h-captcha|cf-turnstile|captcha-(?:container|widget)", RegexOptions.IgnoreCase))
            throw Block(page.Url, WebImportBlockReason.BotChallenge);

        var body = (AngleSharp.Dom.IElement?)page.Document.Body?.Clone(true);
        if (body is not null)
            foreach (var script in body.QuerySelectorAll("script,style,noscript")) script.Remove();
        var visible = body?.TextContent ?? "";
        // Guest read limits, member-only chapters and request throttling show a short preview plus a notice card. The
        // preview would otherwise be imported as a complete (silently truncated) chapter. Only short UI labels count, not
        // story lines. Neither notice is cached, so the chapter is fetched again once the site's limit has reset.
        var labels = body?.QuerySelectorAll("div,p,span,a,h2,h3,h4").Select(e => e.TextContent.Trim())
            .Where(label => label.Length is > 0 and <= 40).ToArray() ?? [];
        if (labels.Any(label => Regex.IsMatch(label, @"访问(?:异常|過快|过快|频繁|頻繁)|訪問(?:異常|過快|頻繁)|操作(?:过于|過於)频繁",
                RegexOptions.None, TimeSpan.FromSeconds(1))))
            throw Block(page.Url, WebImportBlockReason.RateLimited);
        if (labels.Any(label => Regex.IsMatch(label, @"登[录入](?:后|後)(?:继续|繼續)?(?:阅读|閱讀|查看)",
                RegexOptions.None, TimeSpan.FromSeconds(1))))
            throw Block(page.Url, WebImportBlockReason.RequiresLogin);
        if (visible.Length < 6000)
        {
            foreach (var form in page.Document.QuerySelectorAll("form"))
            {
                var controls = form.QuerySelectorAll("button,input[type=submit],input[type=button]");
                bool yes = controls.Any(c => Regex.IsMatch((c.GetAttribute("value") ?? "") + " " + c.TextContent,
                    @"\b(yes|confirm|agree|enter|continue|accept)\b|确认|確認|同意|进入|進入|成年", RegexOptions.IgnoreCase));
                bool no = controls.Any(c => Regex.IsMatch((c.GetAttribute("value") ?? "") + " " + c.TextContent,
                    @"\b(no|decline|cancel|leave|reject)\b|取消|不同意|离开|離開|未成年", RegexOptions.IgnoreCase));
                if (yes && no) throw Block(page.Url, WebImportBlockReason.RequiresUserConfirmation);
            }
        }
    }

    internal static void CheckBrowser(WebPage page, int extractedCjk)
    {
        // Client-side templates with only a title / menu worth of text (seen live: 60–90 CJK) mean the chapter is filled
        // in by JavaScript; importing that would create nearly empty chapters.
        if (extractedCjk < 500 && HtmlMatches(page, @"\{\{[^}]+\}\}"))
            throw Block(page.Url, WebImportBlockReason.RequiresBrowser);
        if (extractedCjk >= 30) return;
        if (HtmlMatches(page, @"\{\{[^}]+\}\}") ||
            page.Document.QuerySelectorAll("#app,#root,#__next").Any(e => WebPage.CjkCount(e.TextContent) < 10))
            throw Block(page.Url, WebImportBlockReason.RequiresBrowser);
    }

    // These patterns are linear; the timeout only bounds a pathological page. A page that cannot be scanned in time
    // counts as "not detected", and the CJK checks on the extracted text still apply.
    private static bool HtmlMatches(WebPage page, string pattern, RegexOptions options = RegexOptions.None)
    {
        try { return Regex.IsMatch(page.Html, pattern, options, WebPage.RegexTimeout); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    internal static WebImportBlockedException Block(Uri url, WebImportBlockReason reason) => new(url, reason, reason switch
    {
        WebImportBlockReason.BotChallenge => "Trang đang yêu cầu xác minh chống bot. Hãy mở trang trong trình duyệt; nhập bằng trình duyệt sẽ được hỗ trợ ở giai đoạn 2.",
        WebImportBlockReason.RequiresUserConfirmation => "Trang yêu cầu bạn xác nhận độ tuổi hoặc nội dung. Hãy xác nhận trực tiếp trong trình duyệt; giai đoạn 2 sẽ hỗ trợ cửa sổ trình duyệt.",
        WebImportBlockReason.RequiresBrowser => "Nội dung được tải bằng JavaScript. Hãy mở trang trong trình duyệt; giai đoạn 2 sẽ hỗ trợ nhập bằng trình duyệt.",
        WebImportBlockReason.NotFound => "Không tìm thấy trang (404/410). Hãy kiểm tra lại đường dẫn hoặc chọn trang sách khác.",
        WebImportBlockReason.RateLimited => "Web đang chặn vì tải quá nhanh (giới hạn tần suất). Hãy đợi một lúc rồi chạy lại với thời gian nghỉ giữa các yêu cầu lớn hơn (ví dụ 5 giây); các chương đã tải được giữ trong cache.",
        WebImportBlockReason.RequiresLogin =>"Web yêu cầu đăng nhập để đọc tiếp (giới hạn khách hoặc chương thành viên). QT Studio chưa hỗ trợ đăng nhập web truyện; hãy thử lại khi giới hạn được làm mới.",
        _ => "Không tìm thấy danh sách chương. Hãy thử đường dẫn mục lục hoặc trang sách khác."
    });
}
