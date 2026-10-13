using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Reading;
using QuickTranslator.AiEditor.Core.Translation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace QuickTranslator.AiEditor.Tests;

public sealed class ReaderModeTests
{
    [Fact]
    public void Txt_export_existing_format_is_characterized_before_reader_unification()
    {
        var first = new TranslationSegment { Order = 1, UserDraft = "Dòng một.\nDòng hai." };
        var second = new TranslationSegment { Order = 2, UserDraft = "“Đoạn kế tiếp.”" };

        ComposedChapter export = ChapterTranslationComposer.Compose(
            new ChapterDocument { Segments = [first, second] });

        Assert.Equal("Dòng một.\nDòng hai.\n\n“Đoạn kế tiếp.”", export.Text);
        Assert.Equal(0, export.Spans[0].Start);
        Assert.Equal("Dòng một.\nDòng hai.".Length + 2, export.Spans[1].Start);
    }

    [Fact]
    public async Task Txt_export_file_keeps_the_characterized_utf8_text_after_reader_unification()
    {
        string path = Path.Combine(Path.GetTempPath(), "qtaie-m6-export-" + Guid.NewGuid() + ".txt");
        try
        {
            var chapter = new ChapterDocument { Segments =
            [
                new() { Order = 1, UserDraft = "Dòng một.\nDòng hai." },
                new() { Order = 2, UserDraft = "“Đoạn kế tiếp.”" }
            ] };

            await ChapterTranslationComposer.ExportAsync(chapter, path);

            Assert.Equal("Dòng một.\nDòng hai.\n\n“Đoạn kế tiếp.”", await File.ReadAllTextAsync(path));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Reader_and_txt_export_use_the_same_canonical_text_and_segment_offsets()
    {
        var first = new TranslationSegment { Order = 1, UserDraft = "Nàng bước ra" };
        var second = new TranslationSegment { Order = 2, UserDraft = "khỏi cửa.\n\n“Đi thôi.”" };
        var chapter = new ChapterDocument { Segments = [first, second] };

        ReaderDocument reader = ReaderDocumentComposer.Compose(chapter);
        ComposedChapter export = ChapterTranslationComposer.Compose(chapter);

        Assert.Equal(export.Text, reader.Text);
        Assert.Equal(export.Spans.Select(span => (span.SegmentId, span.Order, span.Start, span.Length)),
            reader.Spans.Select(span => (span.SegmentId, span.Order, span.Start, span.Length)));
    }

    [Fact]
    public void Compose_UsesCanonicalParagraphSeparator_AndMapsOffsets()
    {
        var first = new TranslationSegment { Order = 1, UserDraft = "Nàng bước ra" };
        var second = new TranslationSegment { Order = 2, UserDraft = " khỏi cửa.\n\n“Đi thôi.”" };
        var chapter = new ChapterDocument { Segments = [first, second] };

        var reader = ReaderDocumentComposer.Compose(chapter);

        Assert.Equal("Nàng bước ra\n\n khỏi cửa.\n\n“Đi thôi.”", reader.Text);
        Assert.Equal(first.Id, reader.FindSegmentAt(3));
        Assert.Equal(second.Id, reader.FindSegmentAt("Nàng bước ra\n\n".Length));
        Assert.Equal("Nàng bước ra".Length + 2, reader.Spans[1].Start);
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
                TableOfContentsVisible = false,
                CollapseExtraBlankLinesForDisplay = false,
                PageWidth = ReaderPageWidth.Wide,
                TextAlignment = ReaderTextAlignment.Justify,
                ChapterPositions = { [chapterId] = new() { ScrollOffset = 321.5, CharacterOffset = 99,
                    SegmentId = Guid.NewGuid(), SegmentOffset = 7, BookmarkOffset = 80 } }
            };
            var store = new ReaderPreferencesStore(path);

            await store.SaveAsync(expected);
            var actual = await store.LoadAsync();

            Assert.Equal(ReaderTheme.Dark, actual.Theme);
            Assert.Equal(24, actual.FontSize);
            Assert.Equal(ReaderDensity.Compact, actual.Density);
            Assert.Equal(22, actual.PageMargin);
        Assert.False(actual.TableOfContentsVisible);
            Assert.False(actual.CollapseExtraBlankLinesForDisplay);
            Assert.Equal(321.5, actual.ChapterPositions[chapterId].ScrollOffset);
        Assert.Equal(7, actual.ChapterPositions[chapterId].SegmentOffset);
            Assert.Equal(80, actual.ChapterPositions[chapterId].BookmarkOffset);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Legacy_reader_preferences_without_m6_fields_remain_compatible()
    {
        string directory = Path.Combine(Path.GetTempPath(), "qtaie-reader-legacy-" + Guid.NewGuid());
        string path = Path.Combine(directory, "reader.json");
        Directory.CreateDirectory(directory);
        try
        {
            Guid chapterId = Guid.NewGuid();
            var legacy = new ReaderPreferences
            {
                ChapterPositions = { [chapterId] = new() { CharacterOffset = 41, SegmentId = Guid.NewGuid() } }
            };
            JsonObject json = JsonNode.Parse(JsonSerializer.Serialize(legacy, DocumentJson.Options))!.AsObject();
            json.Remove("tableOfContentsVisible");
            json["chapterPositions"]![chapterId.ToString()]!.AsObject().Remove("segmentOffset");
            await File.WriteAllTextAsync(path, json.ToJsonString(DocumentJson.Options));

            ReaderPreferences loaded = await new ReaderPreferencesStore(path).LoadAsync();

            Assert.True(loaded.TableOfContentsVisible);
            Assert.Null(loaded.ChapterPositions[chapterId].SegmentOffset);
            Assert.Equal(41, loaded.ChapterPositions[chapterId].CharacterOffset);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Stable_segment_offset_survives_text_inserted_before_the_segment()
    {
        Guid firstId = Guid.NewGuid(), secondId = Guid.NewGuid();
        var changed = ReaderDocumentComposer.Compose(new ChapterDocument { Segments =
        [
            new() { Id = firstId, Order = 1, UserDraft = "Đoạn đầu đã dài hơn nhiều." },
            new() { Id = secondId, Order = 2, UserDraft = "Đoạn cần trở lại." }
        ] });

        int resolved = changed.ResolveCharacterOffset(secondId, 5, fallbackOffset: 2);

        Assert.Equal(changed.Spans[1].Start + 5, resolved);
        Assert.Equal(secondId, changed.FindSegmentAt(resolved));
    }

    [Fact]
    public void Preferences_accept_reading_positions_for_ten_thousand_chapters()
    {
        var preferences = new ReaderPreferences();
        for (int index = 0; index < 10_000; index++)
            preferences.ChapterPositions[Guid.NewGuid()] = new ReaderChapterPosition
            {
                CharacterOffset = index,
                SegmentOffset = index % 20,
                UpdatedAt = DateTimeOffset.UtcNow
            };

        preferences.Validate();

        Assert.Equal(10_000, preferences.ChapterPositions.Count);
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
