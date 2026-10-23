using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace QuickTranslator.AiEditor.Core.WebImport;

internal sealed record WebPage(Uri Url, string Html, IDocument Document)
{
    static WebPage() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Upper bound for any regex run over untrusted page HTML or script payloads.</summary>
    internal static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);

    internal static WebPage Parse(Uri url, byte[] bytes, string? charset)
    {
        Encoding? encoding = null;
        var skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe, 0, 0 })) { encoding = Encoding.UTF32; skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xfe, 0xff })) { encoding = new UTF32Encoding(true, true); skip = 4; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) { encoding = Encoding.UTF8; skip = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { encoding = Encoding.Unicode; skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { encoding = Encoding.BigEndianUnicode; skip = 2; }
        encoding ??= GetEncoding(charset);
        if (encoding is null)
        {
            var prefix = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 16384));
            foreach (Match meta in Regex.Matches(prefix, @"<meta\b[^>]*>", RegexOptions.IgnoreCase))
            {
                var match = Regex.Match(meta.Value, "charset\\s*=\\s*['\"\\s]*([a-zA-Z0-9_-]+)", RegexOptions.IgnoreCase);
                if (match.Success && (encoding = GetEncoding(match.Groups[1].Value)) is not null) break;
            }
        }
        var html = (encoding ?? Encoding.UTF8).GetString(bytes, skip, bytes.Length - skip);
        return new(url, html, new HtmlParser().ParseDocument(html));
    }

    private static Encoding? GetEncoding(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try { return Encoding.GetEncoding(name.Trim().Trim('\'', '"')); }
        catch (ArgumentException) { return null; }
    }

    internal static int CjkCount(string text) => text.EnumerateRunes().Count(r =>
        r.Value is >= 0x3400 and <= 0x9fff or >= 0xf900 and <= 0xfaff or >= 0x20000 and <= 0x323af);

    internal static string Normalize(string text) => string.Join("\n", text.Replace("\r\n", "\n")
        .Replace('\r', '\n').Replace('\u00a0', ' ').Split('\n').Select(line => line.TrimEnd())).Trim();

    internal static Uri? Resolve(Uri page, string? href)
    {
        if (!Uri.TryCreate(page, href, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        return new UriBuilder(uri) { Fragment = "" }.Uri;
    }

    internal static bool SameHost(Uri first, Uri second) =>
        string.Equals(first.IdnHost, second.IdnHost, StringComparison.OrdinalIgnoreCase) && first.Port == second.Port;
}
