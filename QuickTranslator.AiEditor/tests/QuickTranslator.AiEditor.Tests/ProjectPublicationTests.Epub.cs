using System.IO.Compression;
using System.Xml.Linq;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed partial class ProjectPublicationTests
{
    [Fact]
    public async Task Chinese_epub_uses_zh_metadata_original_text_and_chinese_navigation_without_draft_permission()
    {
        var session = await _repository.CreateAsync(Path.Combine(_root, "ChineseEpub"), "Tên tiếng Việt");
        session.Manifest.ChineseTitle = "中文小说";
        await _repository.SaveManifestAsync(session);
        await AddChineseChapterAsync(session, 2, "第二章 相逢", "第二章原文。");
        await AddChineseChapterAsync(session, 1, "第一章 开始", "第一章原文。");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        var inspection = await service.InspectAsync(session); // Chinese export also accepts a Vietnamese inspection.
        string path = Path.Combine(_root, "book-zh.epub");

        var report = await service.ExportEpubAsync(session, inspection, path, new(Language: PublicationLanguage.Chinese));

        Assert.Equal(2, report.ExportedChapterCount);
        Assert.Equal(0, report.IncompleteChapterCount);
        Assert.Equal(0, report.InspectionChapterCount);
        using var zip = ZipFile.OpenRead(path);
        XNamespace dc = "http://purl.org/dc/elements/1.1/", html = "http://www.w3.org/1999/xhtml";
        var package = LoadEpubXml(zip, "EPUB/package.opf");
        Assert.Equal("zh", Assert.Single(package.Descendants(dc + "language")).Value);
        Assert.Equal("中文小说", Assert.Single(package.Descendants(dc + "title")).Value);
        Assert.Equal(new[] { "第一章 开始", "第二章 相逢" },
            LoadEpubXml(zip, "EPUB/nav.xhtml").Descendants(html + "a").Select(element => element.Value));
        Assert.Equal("第一章原文。", LoadEpubXml(zip, "EPUB/chapter-000001.xhtml").Descendants(html + "p").First().Value);
        Assert.Equal("第二章 相逢", Assert.Single(LoadEpubXml(zip, "EPUB/chapter-000002.xhtml").Descendants(html + "h1")).Value);
        foreach (var entry in zip.Entries.Where(entry => entry.FullName.EndsWith(".xhtml")))
        {
            var page = LoadEpubXml(zip, entry.FullName);
            Assert.Equal("zh", (string?)page.Root!.Attribute("lang"));
            Assert.Equal("zh", (string?)page.Root.Attribute(XNamespace.Xml + "lang"));
            Assert.DoesNotContain("tiếng Việt", page.Root.Value);
            Assert.DoesNotContain("Mục lục", page.Root.Value);
        }
    }

    [Fact]
    public async Task Epub_has_valid_package_clickable_navigation_unicode_and_no_project_changes()
    {
        var session = await _repository.CreateAsync(Path.Combine(_root, "Epub"), "Truyện <Việt> & bạn");
        await AddChapterAsync(session, 2, "Hai & <ba>", "Nàng nói: “Đi thôi.”\nTiếng Việt & <script>không phải HTML</script>.");
        await AddChapterAsync(session, 1, "Một", "Đầu truyện.");
        var deleted = await AddChapterAsync(session, 3, "Đã xóa", "Không xuất.");
        await _repository.SoftDeleteChapterAsync(session, deleted.Id);
        var before = HashProjectFiles(session.RootDirectory);
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        var inspection = await service.InspectAsync(session);
        string path = Path.Combine(_root, "book.epub");
        var report = await service.ExportEpubAsync(session, inspection, path, new());
        Assert.Equal(2, report.ExportedChapterCount);
        Assert.Equal(before, HashProjectFiles(session.RootDirectory));
        byte[] bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 8)); // first ZIP entry uses STORE
        Assert.Equal(8, BitConverter.ToUInt16(bytes, 26));
        Assert.Equal(0, BitConverter.ToUInt16(bytes, 28)); // mimetype has no extra fields
        Assert.Equal("mimetype", System.Text.Encoding.ASCII.GetString(bytes, 30, 8));
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        using (var reader = new StreamReader(zip.Entries[0].Open())) Assert.Equal("application/epub+zip", await reader.ReadToEndAsync());
        foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml") || e.FullName.EndsWith(".xhtml") || e.FullName.EndsWith(".opf") || e.FullName.EndsWith(".ncx")))
        {
            using var stream = entry.Open();
            var xml = XDocument.Load(stream); // every document is well-formed XML
            foreach (var link in xml.Descendants().Attributes("href").Concat(xml.Descendants().Attributes("src")))
            {
                string target = link.Value.Split('#')[0];
                Assert.NotNull(zip.GetEntry("EPUB/" + target));
            }
        }
        XNamespace html = "http://www.w3.org/1999/xhtml", opf = "http://www.idpf.org/2007/opf";
        var nav = LoadEpubXml(zip, "EPUB/nav.xhtml");
        Assert.Equal(new[] { "Một", "Hai & <ba>" }, nav.Descendants(html + "a").Select(e => e.Value));
        var package = LoadEpubXml(zip, "EPUB/package.opf");
        Assert.Single(package.Descendants(opf + "item"), e => (string?)e.Attribute("properties") == "nav");
        var ids = package.Descendants(opf + "item").Select(e => (string?)e.Attribute("id")).ToHashSet();
        Assert.All(package.Descendants(opf + "itemref"), e => Assert.Contains((string?)e.Attribute("idref"), ids));
        var chapter = LoadEpubXml(zip, "EPUB/chapter-000002.xhtml");
        Assert.Empty(chapter.Descendants(html + "script"));
        Assert.Contains("Tiếng Việt & <script>không phải HTML</script>.", chapter.Root!.Value);
        Assert.Contains(chapter.Descendants(html + "a"), e => (string?)e.Attribute("href") == "nav.xhtml#toc");
        Assert.Equal(2, LoadEpubXml(zip, "EPUB/toc.ncx").Descendants(XName.Get("navPoint", "http://www.daisy.org/z3986/2005/ncx/")).Count());
    }

    [Fact]
    public async Task Epub_range_and_hidden_titles_keep_navigation()
    {
        var session = await _repository.CreateAsync(Path.Combine(_root, "RangeEpub"), "Truyện");
        for (int i = 1; i <= 3; i++) await AddChapterAsync(session, i, $"Chương {i}", $"Nội dung {i}.");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        var inspection = (await service.InspectAsync(session)).WithRange(2, 2);
        string path = Path.Combine(_root, "range.epub");
        var report = await service.ExportEpubAsync(session, inspection, path, new(IncludeProjectTitle: false, IncludeChapterTitles: false));
        Assert.Equal(1, report.ExportedChapterCount);
        using var zip = ZipFile.OpenRead(path);
        Assert.Null(zip.GetEntry("EPUB/title.xhtml"));
        XNamespace html = "http://www.w3.org/1999/xhtml";
        Assert.Equal("Chương 2", Assert.Single(LoadEpubXml(zip, "EPUB/nav.xhtml").Descendants(html + "a")).Value);
        var chapter = LoadEpubXml(zip, "EPUB/chapter-000001.xhtml");
        Assert.Empty(chapter.Descendants(html + "h1"));
        Assert.Equal("Nội dung 2.", chapter.Descendants(html + "p").First().Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Epub_failure_or_cancellation_keeps_old_file_and_cleans_temp(bool cancel)
    {
        var session = await _repository.CreateAsync(Path.Combine(_root, "FailEpub"), "Truyện");
        await AddChapterAsync(session, 1, "Một", "Nội dung.");
        await AddChapterAsync(session, 2, "Hai", "Nội dung.");
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        var inspection = await service.InspectAsync(session);
        string path = Path.Combine(_root, "old.epub");
        await File.WriteAllTextAsync(path, "Giữ nguyên");
        using var cts = new CancellationTokenSource();
        int reads = 0;
        _repository.FaultPoint = point =>
        {
            if (point != "BeforeChapterRead" || ++reads != 2) return;
            if (cancel) cts.Cancel(); else throw new IOException("Mock failure");
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportEpubAsync(session, inspection, path, new(), cancellationToken: cts.Token));
        else await Assert.ThrowsAsync<IOException>(() => service.ExportEpubAsync(session, inspection, path, new()));
        Assert.Equal("Giữ nguyên", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.publication-*.tmp"));
    }

    [Fact]
    public async Task Epub_rejects_drafts_stale_inspection_and_project_destinations()
    {
        var session = await _repository.CreateAsync(Path.Combine(_root, "SafeEpub"), "Truyện");
        var chapter = TranslationService.CreateChapter("Một", "原文。");
        await _repository.AddChapterAsync(session, chapter, 1);
        var service = new ProjectPublicationService(_repository, new ProjectCatalog(_repository));
        var inspection = await service.InspectAsync(session);
        string path = Path.Combine(_root, "draft.epub");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportEpubAsync(session, inspection, path, new()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportEpubAsync(session, inspection, Path.Combine(session.RootDirectory, "book.epub"), new(AllowIncomplete: true)));
        await service.ExportEpubAsync(session, inspection, path, new(AllowIncomplete: true));
        byte[] before = await File.ReadAllBytesAsync(path);
        chapter.Segments[0].UserDraft = "Bản mới.";
        await _repository.UpdateChapterAsync(session, chapter);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportEpubAsync(session, inspection, path, new(AllowIncomplete: true)));
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    private static XDocument LoadEpubXml(ZipArchive zip, string path)
    {
        using var stream = zip.GetEntry(path)!.Open();
        return XDocument.Load(stream);
    }
}
