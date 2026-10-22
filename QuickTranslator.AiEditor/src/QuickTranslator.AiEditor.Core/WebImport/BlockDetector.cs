using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.WebImport;

internal static class BlockDetector
{
    internal static void Check(WebPage page)
    {
        if (Regex.IsMatch(page.Html, @"cf-chl|challenge-platform|\bjust a moment\b|g-recaptcha|h-captcha|cf-turnstile|captcha-(?:container|widget)", RegexOptions.IgnoreCase))
            throw Block(page.Url, WebImportBlockReason.BotChallenge);

        var body = (AngleSharp.Dom.IElement?)page.Document.Body?.Clone(true);
        if (body is not null)
            foreach (var script in body.QuerySelectorAll("script,style,noscript")) script.Remove();
        var visible = body?.TextContent ?? "";
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
        if (extractedCjk >= 30) return;
        if (Regex.IsMatch(page.Html, @"\{\{[^}]+\}\}") ||
            page.Document.QuerySelectorAll("#app,#root,#__next").Any(e => WebPage.CjkCount(e.TextContent) < 10))
            throw Block(page.Url, WebImportBlockReason.RequiresBrowser);
    }

    internal static WebImportBlockedException Block(Uri url, WebImportBlockReason reason) => new(url, reason, reason switch
    {
        WebImportBlockReason.BotChallenge => "Trang đang yêu cầu xác minh chống bot. Hãy mở trang trong trình duyệt; nhập bằng trình duyệt sẽ được hỗ trợ ở giai đoạn 2.",
        WebImportBlockReason.RequiresUserConfirmation => "Trang yêu cầu bạn xác nhận độ tuổi hoặc nội dung. Hãy xác nhận trực tiếp trong trình duyệt; giai đoạn 2 sẽ hỗ trợ cửa sổ trình duyệt.",
        WebImportBlockReason.RequiresBrowser => "Nội dung được tải bằng JavaScript. Hãy mở trang trong trình duyệt; giai đoạn 2 sẽ hỗ trợ nhập bằng trình duyệt.",
        WebImportBlockReason.NotFound => "Không tìm thấy trang (404/410). Hãy kiểm tra lại đường dẫn hoặc chọn trang sách khác.",
        _ => "Không tìm thấy danh sách chương. Hãy thử đường dẫn mục lục hoặc trang sách khác."
    });
}
