using System.IO;
using System.Windows;
using System.Windows.Controls;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ProjectPublicationUiTests
{
    [Theory]
    [InlineData("Txt", ".txt")]
    [InlineData("Epub", ".epub")]
    public Task Publication_chinese_choice_clears_draft_issues_and_exports_original_language(string format, string extension) => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Project.Manifest.ChineseTitle = "中文小说";
        await fixture.Repository.SaveManifestAsync(fixture.Project);
        ChapterDocument chapter = await fixture.AddChapterAsync("Tên tiếng Việt", 1);
        chapter.ChineseTitle = "第一章 开始";
        await fixture.Repository.UpdateChapterAsync(fixture.Project, chapter);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        var window = new ProjectPublicationWindow(fixture.Project, service);
        try
        {
            await window.LoadInspectionAsync();
            var draft = (CheckBox)window.FindName("AllowIncompleteInput");
            draft.IsChecked = true;
            Assert.Single(((ListBox)window.FindName("IssueChaptersList")).Items);
            var language = (ComboBox)window.FindName("LanguageInput");
            Assert.Equal("Bản dịch tiếng Việt", ((ComboBoxItem)language.SelectedItem).Content);
            language.SelectedItem = language.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "Chinese");
            Assert.Equal("Bản gốc tiếng Trung", ((ComboBoxItem)language.SelectedItem).Content);
            await window.LoadInspectionAsync();

            Assert.Equal(PublicationLanguage.Chinese, window.CurrentInspection?.Language);
            Assert.Equal(0, window.CurrentInspection?.IncompleteChapterCount);
            Assert.Equal(0, window.CurrentInspection?.InspectionChapterCount);
            Assert.Equal("中文小说", ((TextBlock)window.FindName("ProjectTitleText")).Text);
            Assert.Contains("bản gốc tiếng Trung", ((TextBlock)window.FindName("InspectionText")).Text);
            Assert.Empty(((ListBox)window.FindName("IssueChaptersList")).Items);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("IssueChaptersPanel")).Visibility);
            Assert.Equal(Visibility.Collapsed, draft.Visibility);
            Assert.False(draft.IsEnabled);
            Assert.False(draft.IsChecked);
            Assert.True(((Button)window.FindName("ExportButton")).IsEnabled);
            Assert.Equal("中文小说-zh" + extension, window.View.SuggestedFileName(extension));

            var formatInput = (ComboBox)window.FindName("FormatInput");
            formatInput.SelectedItem = formatInput.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == format);
            string path = Path.Combine(fixture.Root, "original" + extension);
            Task export = window.View.ExportToPathAsync(path);
            Assert.False(language.IsEnabled);
            await export;
            Assert.True(language.IsEnabled);
            Assert.Equal(path, window.LastExportPath);
            if (format == "Txt")
                Assert.Equal("中文小说\n\n\n第一章 开始\n\n" + chapter.OriginalText, await File.ReadAllTextAsync(path));
            else
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(path);
                using var stream = zip.GetEntry("EPUB/package.opf")!.Open();
                var package = System.Xml.Linq.XDocument.Load(stream);
                Assert.Equal("zh", Assert.Single(package.Descendants(System.Xml.Linq.XName.Get("language", "http://purl.org/dc/elements/1.1/"))).Value);
            }

            language.SelectedIndex = 0;
            await window.LoadInspectionAsync();
            Assert.Equal(PublicationLanguage.Vietnamese, window.CurrentInspection?.Language);
            Assert.Single(((ListBox)window.FindName("IssueChaptersList")).Items);
            Assert.True(draft.IsEnabled);
            Assert.False(((Button)window.FindName("ExportButton")).IsEnabled);
            Assert.DoesNotContain("-zh", window.View.SuggestedFileName(extension));
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Publication_epub_choice_explains_reading_and_clickable_toc() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        await fixture.AddChapterAsync("Một", 1);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        var window = new ProjectPublicationWindow(fixture.Project, service);
        try
        {
            await window.LoadInspectionAsync();
            var input = (ComboBox)window.FindName("FormatInput");
            input.SelectedItem = input.Items.Cast<ComboBoxItem>().Single(item => (string)item.Tag == "Epub");
            string description = ((TextBlock)window.FindName("FormatDescriptionText")).Text;
            Assert.Contains("mục lục nhấn", description);
            Assert.Contains("cỡ chữ", description);
            Assert.False(((Button)window.FindName("ExportButton")).IsEnabled);
            ((CheckBox)window.FindName("AllowIncompleteInput")).IsChecked = true;
            Assert.True(((Button)window.FindName("ExportButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Workspace_sidebar_exposes_the_publication_command() => StaHost.Run(() =>
    {
        using var fixture = new WorkspaceFixture();

        var button = Assert.IsType<Button>(fixture.Window.FindName("PublishNavButton"));

        Assert.Equal("Publish", button.Tag);
        Assert.Contains("Xuất bản", button.Content?.ToString());
        return Task.CompletedTask;
    });

    [Fact]
    public Task Publication_window_inspects_active_chapters_and_requires_confirmation_for_drafts() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument translated = await fixture.AddChapterAsync("Đã dịch", 1);
        translated.Segments[0].UserDraft = "Bản dịch.";
        translated.Segments[0].Status = SegmentStatus.AwaitingReview;
        await fixture.Repository.UpdateChapterAsync(fixture.Project, translated);
        await fixture.AddChapterAsync("Chưa dịch", 2);
        var service = new ProjectPublicationService(fixture.Repository,
            fixture.Get<ProjectCatalog>("_projectCatalog"));
        var window = new ProjectPublicationWindow(fixture.Project, service);
        try
        {
            await window.LoadInspectionAsync();

            Assert.Equal(2, window.CurrentInspection?.TotalChapterCount);
            Assert.Equal(1, window.CurrentInspection?.IncompleteChapterCount);
            Assert.Equal(Visibility.Visible, ((CheckBox)window.FindName("AllowIncompleteInput")).Visibility);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("IssueChaptersPanel")).Visibility);
            Assert.Single(((ListBox)window.FindName("IssueChaptersList")).Items);
            Assert.NotNull(window.FindName("OpenExportedFileButton"));
            Assert.NotNull(window.FindName("OpenExportFolderButton"));
            Assert.False(((Button)window.FindName("ExportButton")).IsEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Pdf_chinese_export_embeds_cjk_regular_and_bold_fonts_and_original_text() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        fixture.Project.Manifest.ChineseTitle = "中文小说";
        await fixture.Repository.SaveManifestAsync(fixture.Project);
        ChapterDocument chapter = await fixture.AddChapterAsync("Tên tiếng Việt", 1);
        chapter.ChineseTitle = "第一章 开始";
        chapter.Segments[0].UserDraft = "Bản dịch khác với nguyên tác.";
        chapter.Segments[0].Status = SegmentStatus.Approved;
        await fixture.Repository.UpdateChapterAsync(fixture.Project, chapter);
        var service = new ProjectPublicationService(fixture.Repository, fixture.Get<ProjectCatalog>("_projectCatalog"));
        ProjectPublicationInspection inspection = await service.InspectAsync(fixture.Project,
            language: PublicationLanguage.Chinese);
        string path = Path.Combine(fixture.Root, "chinese.pdf");

        ProjectPublicationReport report = await ProjectPdfExporter.ExportAsync(service, fixture.Project,
            inspection, path, new ProjectPublicationOptions(Language: PublicationLanguage.Chinese));

        Assert.Equal(1, report.ExportedChapterCount);
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 2);
        Assert.Equal("中文小说", pdf.Info.Title);
        string expectedFamily = System.Windows.Media.Fonts.SystemFontFamilies.Any(font => font.Source == "Microsoft YaHei")
            ? "YaHei" : "SimSun";
        AssertEmbeddedPdfFonts(pdf, expectedFamily);
        string bytes = System.Text.Encoding.ASCII.GetString(await File.ReadAllBytesAsync(path));
        Assert.Matches(@"/BaseFont\s*/[^\s/]*" + expectedFamily, bytes);
        // 王秀兰 occurs only in the original body, not in the titles or Vietnamese translation.
        string unicodeMaps = string.Join("\n", pdf.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(item => item.Stream is not null && item.Stream.UnfilteredValue.Length > 0)
            .Select(item => System.Text.Encoding.ASCII.GetString(item.Stream.UnfilteredValue)));
        foreach (char character in "王秀兰")
            Assert.Contains($"<{(int)character:X4}>", unicodeMaps, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task Pdf_export_creates_a_readable_unicode_book_without_changing_project_data() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture();
        await fixture.CreateProjectAsync();
        ChapterDocument first = await fixture.AddChapterAsync("Chương 1: Khởi đầu", 1);
        first.Segments[0].UserDraft = "Vương Tú Lan bước vào. Tiếng Việt có dấu.";
        first.Segments[0].Status = SegmentStatus.Approved;
        await fixture.Repository.UpdateChapterAsync(fixture.Project, first);
        ChapterDocument second = await fixture.AddChapterAsync("Chương 2: Gặp lại", 2);
        second.Segments[0].UserDraft = "Nàng nói: “Chúng ta đi thôi.”";
        second.Segments[0].Status = SegmentStatus.AwaitingReview;
        await fixture.Repository.UpdateChapterAsync(fixture.Project, second);
        var service = new ProjectPublicationService(fixture.Repository,
            fixture.Get<ProjectCatalog>("_projectCatalog"));
        ProjectPublicationInspection inspection = await service.InspectAsync(fixture.Project);
        string path = Path.Combine(fixture.Root, "book.pdf");

        ProjectPublicationReport report = await ProjectPdfExporter.ExportAsync(service, fixture.Project,
            inspection, path, new ProjectPublicationOptions());

        Assert.Equal(2, report.ExportedChapterCount);
        Assert.True(new FileInfo(path).Length > 1_000);
        byte[] header = new byte[5];
        await using (FileStream stream = File.OpenRead(path))
            Assert.Equal(5, await stream.ReadAsync(header));
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(header));
        using var pdf = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount >= 3);
        Assert.Equal("Truyện thử tổng hợp", pdf.Info.Title);
        AssertEmbeddedPdfFonts(pdf, "Segoe UI");
    });

    private static void AssertEmbeddedPdfFonts(PdfDocument pdf, string expectedFamily)
    {
        PdfDictionary[] fonts = pdf.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(item => item.Elements.GetName("/Subtype") == "/CIDFontType2").ToArray();
        Assert.True(fonts.Length >= 2, "Expected regular body and bold heading fonts.");
        Assert.All(fonts, font =>
        {
            Assert.Contains(expectedFamily, font.Elements.GetName("/BaseFont"));
            PdfDictionary descriptor = Assert.IsType<PdfDictionary>(font.Elements.GetDictionary("/FontDescriptor"));
            PdfDictionary fontFile = Assert.IsType<PdfDictionary>(descriptor.Elements.GetDictionary("/FontFile2"));
            Assert.NotEmpty(fontFile.Stream.Value);
        });
    }
}
