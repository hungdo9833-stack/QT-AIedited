using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Reading;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ReaderModeTests
{
    [Fact]
    public void Compose_JoinsSegmentsWithoutInventingWhitespace_AndMapsOffsets()
    {
        var first = new TranslationSegment { Order = 1, UserDraft = "Nàng bước ra" };
        var second = new TranslationSegment { Order = 2, UserDraft = " khỏi cửa.\n\n“Đi thôi.”" };
        var chapter = new ChapterDocument { Segments = [first, second] };

        var reader = ReaderDocumentComposer.Compose(chapter);

        Assert.Equal("Nàng bước ra khỏi cửa.\n\n“Đi thôi.”", reader.Text);
        Assert.Equal(first.Id, reader.FindSegmentAt(3));
        Assert.Equal(second.Id, reader.FindSegmentAt("Nàng bước ra".Length));
        Assert.Equal("Nàng bước ra".Length, reader.Spans[1].Start);
    }

    [Fact]
    public void Compose_ReportsIncompleteErrorAndInspectionSeparately()
    {
        var missing = new TranslationSegment { Order = 1, UserDraft = "" };
        var error = new TranslationSegment { Order = 2, UserDraft = "nháp", Status = SegmentStatus.Error };
        var inspect = new TranslationSegment { Order = 3, UserDraft = "cần xem", Status = SegmentStatus.InspectionRequired };

        var reader = ReaderDocumentComposer.Compose(new ChapterDocument { Segments = [missing, error, inspect] });

        Assert.Contains(missing.Id, reader.IncompleteSegmentIds);
        Assert.Contains(error.Id, reader.ErrorSegmentIds);
        Assert.Contains(inspect.Id, reader.InspectionSegmentIds);
        Assert.False(reader.IsComplete);
    }

    [Fact]
    public async Task Preferences_RoundTripPositionAndAppearance()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qtaie-reader-" + Guid.NewGuid());
        string path = Path.Combine(directory, "reader.json");
        try
        {
            Guid chapterId = Guid.NewGuid();
            var expected = new ReaderPreferences
            {
                Theme = ReaderTheme.Dark,
                Density = ReaderDensity.Compact,
                FontFamily = "Cambria",
                FontSize = 24,
                LineHeight = 1.8,
                ParagraphSpacing = 3,
                PageMargin = 22,
                CollapseExtraBlankLinesForDisplay = false,
                PageWidth = ReaderPageWidth.Wide,
                TextAlignment = ReaderTextAlignment.Justify,
                ChapterPositions = { [chapterId] = new() { ScrollOffset = 321.5, CharacterOffset = 99, BookmarkOffset = 80 } }
            };
            var store = new ReaderPreferencesStore(path);

            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();

            Assert.Equal(ReaderTheme.Dark, actual.Theme);
            Assert.Equal(24, actual.FontSize);
            Assert.Equal(ReaderDensity.Compact, actual.Density);
            Assert.Equal(22, actual.PageMargin);
            Assert.False(actual.CollapseExtraBlankLinesForDisplay);
            Assert.Equal(321.5, actual.ChapterPositions[chapterId].ScrollOffset);
            Assert.Equal(80, actual.ChapterPositions[chapterId].BookmarkOffset);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(ReaderDensity.Compact, 1.35, 2, 20)]
    [InlineData(ReaderDensity.Standard, 1.55, 6, 32)]
    [InlineData(ReaderDensity.Spacious, 1.75, 14, 48)]
    public void DensityPresets_OnlyChangeLayoutValues(ReaderDensity density, double lineHeight, double paragraphSpacing, double margin)
    {
        const string storedTranslation = "Câu một.\n\n\nCâu hai.";
        var settings = ReaderDensitySettings.For(density);

        Assert.Equal(lineHeight, settings.LineHeight);
        Assert.Equal(paragraphSpacing, settings.ParagraphSpacing);
        Assert.Equal(margin, settings.PageMargin);
        Assert.Equal("Câu một.\n\n\nCâu hai.", storedTranslation);
    }

    [Fact]
    public void BlankLineNormalization_IsDisplayOnly_AndCanBeDisabled()
    {
        const string text = "Đoạn một.\r\n\r\n\r\nĐoạn hai.";

        var compactDisplay = ReaderDisplayLayout.Create(text, collapseExtraBlankLines: true);
        var exactDisplay = ReaderDisplayLayout.Create(text, collapseExtraBlankLines: false);

        Assert.Equal(2, compactDisplay.Count);
        Assert.Equal(4, exactDisplay.Count);
        Assert.Equal("Đoạn một.\r\n\r\n\r\nĐoạn hai.", text);
        Assert.Equal("Đoạn một.", text.Substring(compactDisplay[0].Start, compactDisplay[0].Length));
        Assert.Equal("Đoạn hai.", text.Substring(compactDisplay[1].Start, compactDisplay[1].Length));
    }

    [Fact]
    public void DarkPalette_HasNoWhiteReaderOrChromeSurface()
    {
        var dark = ReaderThemePalette.For(ReaderTheme.Dark);

        Assert.Equal("#1D2128", dark.PageBackground);
        Assert.Equal(dark.PageBackground, dark.ChromeBackground);
        Assert.All(new[] { dark.PageBackground, dark.ChromeBackground, dark.ControlBackground }, color => Assert.DoesNotContain("FFFFFF", color));
    }
}
