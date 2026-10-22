using System.Collections.Concurrent;
using System.IO;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Wpf;

internal static class ProjectPdfExporter
{
    static ProjectPdfExporter()
    {
        // PDFsharp requires registration before the first font, including a Vietnamese export.
        GlobalFontSettings.FontResolver = new PublicationFontResolver();
    }

    public static async Task<ProjectPublicationReport> ExportAsync(ProjectPublicationService service,
        ProjectSession session, ProjectPublicationInspection inspection, string path,
        ProjectPublicationOptions options, IProgress<ProjectPublicationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(options);
        if (inspection.TotalChapterCount == 0)
            throw new InvalidOperationException(inspection.IsRange ? "Không có chương nào trong phạm vi đã chọn."
                : "Project chưa có chương để xuất bản.");
        if (inspection.RequiresConfirmation && !options.AllowIncomplete)
            throw new InvalidOperationException((inspection.IsRange ? "Phạm vi đã chọn" : "Project") +
                " còn chương chưa hoàn tất hoặc cần kiểm tra. Hãy xác nhận xuất bản nháp trước khi tiếp tục.");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Đường dẫn xuất bản không được để trống.", nameof(path));

        string outputPath = ProjectPublicationService.ValidateOutputPath(session, path);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporaryPath = outputPath + ".publication-" + Guid.NewGuid().ToString("N") + ".tmp";
        int exported = 0;
        try
        {
            Document document = CreateDocument(inspection.ProjectTitle, options.Language);
            Section section = document.AddSection();
            ConfigurePage(section);
            bool pageHasContent = false;
            if (options.IncludeProjectTitle)
            {
                Paragraph title = section.AddParagraph(inspection.ProjectTitle);
                title.Style = "BookTitle";
                title.Format.Alignment = ParagraphAlignment.Center;
                title.Format.SpaceBefore = Unit.FromCentimeter(8);
                pageHasContent = true;
            }

            await foreach (ProjectPublicationChapter chapter in service.ReadChaptersAsync(session,
                               inspection.ProjectRevision, progress, cancellationToken, inspection.FromNumber, inspection.ToNumber,
                               options.Language))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pageHasContent) section.AddPageBreak();
                if (options.IncludeChapterTitles)
                {
                    Paragraph heading = section.AddParagraph(chapter.Title);
                    heading.Style = StyleNames.Heading1;
                }
                AddChapterBody(section, chapter.Text);
                pageHasContent = true;
                exported++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var renderer = new PdfDocumentRenderer { Document = document };
            renderer.RenderDocument();
            cancellationToken.ThrowIfCancellationRequested();
            renderer.PdfDocument.Save(temporaryPath);
            service.EnsureRevision(session, inspection.ProjectRevision);
            ProjectPublicationService.CommitTemporaryFile(temporaryPath, outputPath);
            return new(outputPath, exported, inspection.IncompleteChapterCount, inspection.InspectionChapterCount);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceError($"Không dọn được PDF tạm {temporaryPath}: {ex}"); }
        }
    }

    private static Document CreateDocument(string title, PublicationLanguage language)
    {
        string? chineseFont = language == PublicationLanguage.Chinese ? ResolveChineseFont() : null;
        var document = new Document();
        document.Info.Title = title;
        document.Info.Author = "QT Studio";
        Style normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = chineseFont ?? "Segoe UI";
        normal.Font.Size = Unit.FromPoint(11.5);
        normal.ParagraphFormat.LineSpacingRule = LineSpacingRule.Multiple;
        normal.ParagraphFormat.LineSpacing = 1.35;
        normal.ParagraphFormat.SpaceAfter = Unit.FromPoint(7);
        Style heading = document.Styles[StyleNames.Heading1]!;
        heading.Font.Name = chineseFont ?? "Segoe UI Semibold";
        heading.Font.Size = Unit.FromPoint(18);
        heading.Font.Bold = true;
        heading.ParagraphFormat.SpaceAfter = Unit.FromPoint(18);
        heading.ParagraphFormat.KeepWithNext = true;
        Style titleStyle = document.Styles.AddStyle("BookTitle", StyleNames.Normal);
        titleStyle.Font.Name = chineseFont ?? "Segoe UI Semibold";
        titleStyle.Font.Size = Unit.FromPoint(25);
        titleStyle.Font.Bold = true;
        return document;
    }

    private static string ResolveChineseFont()
    {
        List<Exception> errors = [];
        foreach (string family in new[] { "Microsoft YaHei", "SimSun" })
        {
            // Check installation explicitly: WPF can silently substitute a non-CJK font.
            if (!System.Windows.Media.Fonts.SystemFontFamilies.Any(font =>
                    string.Equals(font.Source, family, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                // Resolve both faces through PDFsharp, including fonts supplied as TTC collections.
                _ = new XFont(family, 11.5, XFontStyleEx.Regular);
                _ = new XFont(family, 18, XFontStyleEx.Bold);
                return family;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or NotSupportedException or IOException)
            {
                errors.Add(ex);
                System.Diagnostics.Trace.TraceWarning($"Cannot resolve PDF font {family}: {ex.Message}");
            }
        }
        throw new InvalidOperationException("Chinese PDF export requires Microsoft YaHei or SimSun.", new AggregateException(errors));
    }

    private sealed class PublicationFontResolver : IFontResolver
    {
        private readonly ConcurrentDictionary<string, byte[]> _faces = new();

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            if (!string.Equals(familyName, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(familyName, "SimSun", StringComparison.OrdinalIgnoreCase))
                return PlatformFontResolver.ResolveTypeface(familyName, isBold, isItalic);

            var typeface = new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily(familyName),
                isItalic ? System.Windows.FontStyles.Italic : System.Windows.FontStyles.Normal,
                isBold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal);
            if (!typeface.TryGetGlyphTypeface(out var glyph)) return null;

            string faceName = $"{familyName}:{isBold}:{isItalic}";
            // PDFsharp 6.2.4 cannot read TTC bytes directly. WPF extracts the selected face
            // as standalone TrueType data; retain all glyphs for PDFsharp's later subsetting.
            _faces.GetOrAdd(faceName, _ => glyph.ComputeSubset(
                Enumerable.Range(0, glyph.GlyphCount).Select(index => (ushort)index).ToArray()));
            return new FontResolverInfo(faceName,
                (glyph.StyleSimulations & System.Windows.Media.StyleSimulations.BoldSimulation) != 0,
                (glyph.StyleSimulations & System.Windows.Media.StyleSimulations.ItalicSimulation) != 0);
        }

        public byte[] GetFont(string faceName) => _faces[faceName];
    }

    private static void ConfigurePage(Section section)
    {
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.BottomMargin = Unit.FromCentimeter(2.2);
        section.PageSetup.LeftMargin = Unit.FromCentimeter(2.3);
        section.PageSetup.RightMargin = Unit.FromCentimeter(2.3);
        Paragraph footer = section.Footers.Primary.AddParagraph();
        footer.Format.Alignment = ParagraphAlignment.Center;
        footer.Format.Font.Size = Unit.FromPoint(9);
        footer.AddPageField();
    }

    private static void AddChapterBody(Section section, string text)
    {
        string normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        foreach (string block in normalized.Split("\n\n", StringSplitOptions.None))
        {
            Paragraph paragraph = section.AddParagraph();
            string[] lines = block.Split('\n');
            for (int index = 0; index < lines.Length; index++)
            {
                if (index > 0) paragraph.AddLineBreak();
                paragraph.AddText(lines[index]);
            }
        }
    }
}
