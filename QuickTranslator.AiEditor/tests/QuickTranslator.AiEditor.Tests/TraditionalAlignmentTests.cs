using QuickTranslator.AiEditor.Core.Traditional;

namespace QuickTranslator.AiEditor.Tests;

public sealed class TraditionalAlignmentTests
{
    [Fact]
    public async Task ChinesePhraseSelectionProjectsToBothOutputs()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        var result = new TraditionalTranslator().Translate("片尾曲，不知道是不是。", snapshot);

        var projected = SelectionAlignment.Project(result, TraditionalPane.Chinese, new TextRange(4, 6));

        Assert.Equal("不知道是不是", result.SourceText.Substring(projected.Chinese.Start, projected.Chinese.Length));
        Assert.Equal("bất tri đạo thị bất thị", result.HanVietText.Substring(projected.HanViet.Start, projected.HanViet.Length), ignoreCase: true);
        Assert.Equal("không biết có phải là", result.VietPhraseText.Substring(projected.VietPhrase.Start, projected.VietPhrase.Length));
    }

    [Fact]
    public async Task TargetSelectionUsesAlignmentInsteadOfSearchingRepeatedText()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        var result = new TraditionalTranslator().Translate("房卒房卒", snapshot);
        int secondTarget = result.VietPhraseText.LastIndexOf("Phòng Tốt", StringComparison.Ordinal);

        var projected = SelectionAlignment.Project(result, TraditionalPane.VietPhrase,
            new TextRange(secondTarget, "Phòng Tốt".Length));

        Assert.Equal(new TextRange(2, 2), projected.Chinese);
        Assert.Equal("房卒", result.SourceText.Substring(projected.Chinese.Start, projected.Chinese.Length));
    }

    [Fact]
    public async Task PartialTargetPhraseExpandsToItsExactSourcePhrase()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        var result = new TraditionalTranslator().Translate("一个人形", snapshot);
        int targetStart = result.VietPhraseText.IndexOf("bóng", StringComparison.OrdinalIgnoreCase);

        var projected = SelectionAlignment.Project(result, TraditionalPane.VietPhrase, new TextRange(targetStart, 4));

        Assert.Equal(new TextRange(0, 4), projected.Chinese);
        Assert.Equal("Một bóng người", result.VietPhraseText.Substring(projected.VietPhrase.Start, projected.VietPhrase.Length));
    }

    [Fact]
    public async Task PunctuationNewlinesAndSurrogatePairsRemainPositionallyAligned()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        const string source = "片尾曲。\r\n𠀀房卒";
        var result = new TraditionalTranslator().Translate(source, snapshot);

        Assert.Equal(source, result.SourceText);
        Assert.Equal(source.Length, result.HanVietAlignment.Sum(span => span.Source.Length));
        Assert.Equal(source.Length, result.VietPhraseAlignment.Sum(span => span.Source.Length));
        Assert.Contains(result.VietPhraseAlignment, span => span.Source.Length == 2 && source.Substring(span.Source.Start, 2) == "𠀀");
        Assert.Contains("\r\n", result.VietPhraseText);
    }

    [Fact]
    public async Task TranslationServiceCachesBySourceAndDictionaryVersion()
    {
        using var data = TraditionalTestData.Create();
        var service = new TraditionalTranslationService(cacheCapacity: 2);
        await service.LoadDataAsync(data.Directory);

        var first = await service.TranslateAsync("片尾曲");
        var cached = await service.TranslateAsync("片尾曲");
        var different = await service.TranslateAsync("房卒");

        Assert.Same(first, cached);
        Assert.NotSame(first, different);
    }

    [Fact]
    public async Task LinYueVpSelectionMapsToChineseAndHanViet()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        var result = new TraditionalTranslator().Translate("林悦", snapshot);
        int selectedStart = result.VietPhraseText.IndexOf("rừng duyệt", StringComparison.OrdinalIgnoreCase);

        var projected = SelectionAlignment.Project(result, TraditionalPane.VietPhrase,
            new TextRange(selectedStart, "rừng duyệt".Length));

        Assert.Equal("林悦", Slice(result.SourceText, projected.Chinese));
        Assert.Equal("lâm duyệt", Slice(result.HanVietText, projected.HanViet), ignoreCase: true);
        Assert.Equal("rừng duyệt", Slice(result.VietPhraseText, projected.VietPhrase), ignoreCase: true);
    }

    [Fact]
    public async Task WangXiuLanHanVietSelectionMapsBothDirections()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        var result = new TraditionalTranslator().Translate("甲王秀兰。", snapshot);
        int selectedStart = result.HanVietText.IndexOf("vương tú lan", StringComparison.OrdinalIgnoreCase);

        var projected = SelectionAlignment.Project(result, TraditionalPane.HanViet,
            new TextRange(selectedStart, "vương tú lan".Length));

        Assert.Equal("王秀兰", Slice(result.SourceText, projected.Chinese));
        Assert.Equal("Vương Tú Lan", Slice(result.VietPhraseText, projected.VietPhrase));
    }

    [Fact]
    public async Task TripleOccurrenceMapsOnlyTheSelectedMiddleOccurrence()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        var result = new TraditionalTranslator().Translate("林悦林悦林悦", snapshot);
        int first = result.VietPhraseText.IndexOf("rừng duyệt", StringComparison.OrdinalIgnoreCase);
        int middle = result.VietPhraseText.IndexOf("rừng duyệt", first + 1, StringComparison.OrdinalIgnoreCase);

        var projected = SelectionAlignment.Project(result, TraditionalPane.VietPhrase,
            new TextRange(middle, "rừng duyệt".Length));

        Assert.Equal(new TextRange(2, 2), projected.Chinese);
    }

    [Fact]
    public async Task MultiSentenceSelectionAcrossCrlfPreservesExactBounds()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        const string source = "林悦。\r\n不知道是不是？王秀兰。";
        var result = new TraditionalTranslator().Translate(source, snapshot);
        int sourceEnd = source.IndexOf('？') + 1;

        var projected = SelectionAlignment.Project(result, TraditionalPane.Chinese, new TextRange(0, sourceEnd));

        Assert.Equal(source[..sourceEnd], Slice(result.SourceText, projected.Chinese));
        Assert.Contains("\r\n", Slice(result.HanVietText, projected.HanViet));
        Assert.Contains("Không biết có phải là", Slice(result.VietPhraseText, projected.VietPhrase), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectionNearEndOfSegmentDoesNotOverflow()
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        string source = new string('甲', 490) + "王秀兰。";
        var result = new TraditionalTranslator().Translate(source, snapshot);
        int selectedStart = result.VietPhraseText.LastIndexOf("Vương Tú Lan", StringComparison.Ordinal);

        var projected = SelectionAlignment.Project(result, TraditionalPane.VietPhrase,
            new TextRange(selectedStart, "Vương Tú Lan".Length));

        Assert.Equal(new TextRange(490, 3), projected.Chinese);
        Assert.Equal("王秀兰", Slice(source, projected.Chinese));
    }

    [Theory]
    [InlineData("林悦。", "林悦")]
    [InlineData("甲不知道是不是。", "不知道是不是")]
    [InlineData("甲甲王秀兰。", "王秀兰")]
    public async Task IndependentSegmentPositionsUseLocalAlignment(string source, string selected)
    {
        using var data = TraditionalTestData.Create();
        var snapshot = await new TraditionalDataLoader().LoadAsync(data.Directory);
        var result = new TraditionalTranslator().Translate(source, snapshot);
        int start = source.IndexOf(selected, StringComparison.Ordinal);

        var projected = SelectionAlignment.Project(result, TraditionalPane.Chinese, new TextRange(start, selected.Length));

        Assert.Equal(selected, Slice(source, projected.Chinese));
        Assert.True(projected.HanViet.Length > 0);
        Assert.True(projected.VietPhrase.Length > 0);
    }

    private static string Slice(string text, TextRange range) => text.Substring(range.Start, range.Length);
}
