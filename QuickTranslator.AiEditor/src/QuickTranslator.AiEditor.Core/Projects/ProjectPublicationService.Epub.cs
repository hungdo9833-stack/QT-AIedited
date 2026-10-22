using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed partial class ProjectPublicationService
{
    private static readonly XNamespace Html = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Epub = "http://www.idpf.org/2007/ops";
    private static readonly XNamespace Opf = "http://www.idpf.org/2007/opf";
    private static readonly XNamespace Dc = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace Ncx = "http://www.daisy.org/z3986/2005/ncx/";

    // Relative sizes and no forced colors/fonts let readers choose their own font, zoom and night mode.
    private const string ReadingCss = """
        @charset "UTF-8";
        body { margin: 5%; font-size: 1em; line-height: 1.6; }
        main { max-width: 38em; margin: 0 auto; }
        h1 { font-size: 1.5em; line-height: 1.3; margin: 1em 0; text-align: center; break-after: avoid; }
        p { margin: 0 0 0.75em; text-align: left; orphans: 2; widows: 2; overflow-wrap: break-word; }
        nav li { margin-bottom: 0.65em; }
        .toc-link { margin-top: 2em; text-indent: 0; }
        """;

    public async Task<ProjectPublicationReport> ExportEpubAsync(ProjectSession session,
        ProjectPublicationInspection inspection, string path, ProjectPublicationOptions options,
        IProgress<ProjectPublicationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(options);
        EnsureAllowed(inspection, options);
        PublicationSnapshot snapshot = Capture(session, inspection.ProjectRevision, inspection.FromNumber, inspection.ToNumber);
        string outputPath = ValidateOutputPath(session, path);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporaryPath = outputPath + ".publication-" + Guid.NewGuid().ToString("N") + ".tmp";
        string title = string.IsNullOrWhiteSpace(snapshot.ProjectTitle) ? "Truyện" : snapshot.ProjectTitle;
        string identifier = "urn:uuid:" + Guid.NewGuid();
        var entries = new List<(string Id, string Href, string Title)>();
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    // OCF requires this to be the first entry, stored without compression or a BOM.
                    await WriteEpubEntryAsync(zip, "mimetype", "application/epub+zip", cancellationToken, CompressionLevel.NoCompression);
                    XNamespace container = "urn:oasis:names:tc:opendocument:xmlns:container";
                    await WriteEpubXmlAsync(zip, "META-INF/container.xml", new XElement(container + "container",
                        new XAttribute("version", "1.0"), new XElement(container + "rootfiles",
                            new XElement(container + "rootfile", new XAttribute("full-path", "EPUB/package.opf"),
                                new XAttribute("media-type", "application/oebps-package+xml")))), cancellationToken);
                    await WriteEpubEntryAsync(zip, "EPUB/book.css", ReadingCss, cancellationToken);
                    if (options.IncludeProjectTitle)
                        await WriteEpubXmlAsync(zip, "EPUB/title.xhtml", EpubPage(title,
                            new XElement(Html + "h1", title)), cancellationToken);

                    await foreach (ProjectPublicationChapter chapter in ReadSnapshotAsync(session, snapshot, progress,
                                       cancellationToken).ConfigureAwait(false))
                    {
                        string id = "chapter-" + (entries.Count + 1).ToString("D6", CultureInfo.InvariantCulture);
                        string chapterTitle = string.IsNullOrWhiteSpace(chapter.Title) ? $"Chương {chapter.Number}" : chapter.Title;
                        var main = new XElement(Html + "main", new XAttribute("id", "chapter"));
                        if (options.IncludeChapterTitles) main.Add(new XElement(Html + "h1", chapterTitle));
                        // Text is always escaped as XML, never interpreted as markup or external resources.
                        string[] lines = chapter.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                        foreach (string line in lines)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!string.IsNullOrWhiteSpace(line)) main.Add(new XElement(Html + "p", line));
                        }
                        main.Add(new XElement(Html + "p", new XAttribute("class", "toc-link"),
                            new XElement(Html + "a", new XAttribute("href", "nav.xhtml#toc"), "Mục lục")));
                        await WriteEpubXmlAsync(zip, "EPUB/" + id + ".xhtml", EpubPage(chapterTitle, main), cancellationToken);
                        entries.Add((id, id + ".xhtml", chapterTitle));
                    }

                    var nav = new XElement(Html + "nav", new XAttribute(Epub + "type", "toc"), new XAttribute("id", "toc"),
                        new XElement(Html + "h1", "Mục lục"), new XElement(Html + "ol", entries.Select(entry =>
                            new XElement(Html + "li", new XElement(Html + "a", new XAttribute("href", entry.Href), entry.Title)))));
                    await WriteEpubXmlAsync(zip, "EPUB/nav.xhtml", EpubPage("Mục lục", nav), cancellationToken);

                    // NCX also makes the chapter list available to older EPUB readers.
                    await WriteEpubXmlAsync(zip, "EPUB/toc.ncx", new XElement(Ncx + "ncx", new XAttribute("version", "2005-1"),
                        new XElement(Ncx + "head", new XElement(Ncx + "meta", new XAttribute("name", "dtb:uid"), new XAttribute("content", identifier))),
                        new XElement(Ncx + "docTitle", new XElement(Ncx + "text", title)),
                        new XElement(Ncx + "navMap", entries.Select((entry, index) => new XElement(Ncx + "navPoint",
                            new XAttribute("id", entry.Id), new XAttribute("playOrder", index + 1),
                            new XElement(Ncx + "navLabel", new XElement(Ncx + "text", entry.Title)),
                            new XElement(Ncx + "content", new XAttribute("src", entry.Href)))))), cancellationToken);

                    var manifest = new XElement(Opf + "manifest",
                        EpubItem("nav", "nav.xhtml", "application/xhtml+xml", "nav"),
                        EpubItem("css", "book.css", "text/css"), EpubItem("ncx", "toc.ncx", "application/x-dtbncx+xml"));
                    var spine = new XElement(Opf + "spine", new XAttribute("toc", "ncx"));
                    if (options.IncludeProjectTitle)
                    {
                        manifest.Add(EpubItem("title", "title.xhtml", "application/xhtml+xml"));
                        spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", "title")));
                    }
                    spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", "nav")));
                    foreach (var entry in entries)
                    {
                        manifest.Add(EpubItem(entry.Id, entry.Href, "application/xhtml+xml"));
                        spine.Add(new XElement(Opf + "itemref", new XAttribute("idref", entry.Id)));
                    }
                    await WriteEpubXmlAsync(zip, "EPUB/package.opf", new XElement(Opf + "package",
                        new XAttribute("version", "3.0"), new XAttribute("unique-identifier", "book-id"),
                        new XElement(Opf + "metadata", new XAttribute(XNamespace.Xmlns + "dc", Dc),
                            new XElement(Dc + "identifier", new XAttribute("id", "book-id"), identifier),
                            new XElement(Dc + "title", title), new XElement(Dc + "language", "vi"),
                            new XElement(Opf + "meta", new XAttribute("property", "dcterms:modified"),
                                DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                            new XElement(Opf + "meta", new XAttribute("property", "rendition:layout"), "reflowable")),
                        manifest, spine), cancellationToken);
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRevision(session, snapshot.Revision);
            CommitTemporaryFile(temporaryPath, outputPath);
            return new(outputPath, entries.Count, inspection.IncompleteChapterCount, inspection.InspectionChapterCount);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceError($"Không dọn được file EPUB tạm {temporaryPath}: {ex}"); }
        }
    }

    private static XElement EpubPage(string title, XElement content) => new(Html + "html",
        new XAttribute(XNamespace.Xmlns + "epub", Epub), new XAttribute("lang", "vi"), new XAttribute(XNamespace.Xml + "lang", "vi"),
        new XElement(Html + "head", new XElement(Html + "title", title),
            new XElement(Html + "link", new XAttribute("rel", "stylesheet"), new XAttribute("type", "text/css"), new XAttribute("href", "book.css"))),
        new XElement(Html + "body", content));

    private static XElement EpubItem(string id, string href, string mediaType, string? properties = null) => new(Opf + "item",
        new XAttribute("id", id), new XAttribute("href", href), new XAttribute("media-type", mediaType),
        properties is null ? null : new XAttribute("properties", properties));

    private static Task WriteEpubXmlAsync(ZipArchive zip, string path, XElement element, CancellationToken token) =>
        WriteEpubEntryAsync(zip, path, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + element.ToString(SaveOptions.DisableFormatting), token);

    private static async Task WriteEpubEntryAsync(ZipArchive zip, string path, string text, CancellationToken token,
        CompressionLevel compression = CompressionLevel.Optimal)
    {
        token.ThrowIfCancellationRequested();
        var entry = zip.CreateEntry(path, compression);
        await using var target = entry.Open();
        await target.WriteAsync(Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
    }
}
