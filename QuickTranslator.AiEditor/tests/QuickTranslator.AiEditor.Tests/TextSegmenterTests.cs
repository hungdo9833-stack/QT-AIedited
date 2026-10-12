using System.Globalization;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Segmentation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class TextSegmenterTests
{
    private readonly TextSegmenter _segmenter = new();
    private static readonly Guid ChapterId = Guid.Parse("7d411326-f1dd-4775-946a-63d861f85f34");

    [Theory]
    [InlineData(1000)]
    [InlineData(5000)]
    public void LongChapter_RoundTrips_AndRespectsHanSize(int count)
    {
        string source = string.Concat(Enumerable.Repeat(new string('中', 100) + "。\r\n", count / 100));
        var segments = _segmenter.Segment(source, ChapterId);
        Assert.Equal(source, TextSegmenter.Reconstruct(segments));
        Assert.All(segments, s => Assert.InRange(s.HanCharacterCount, 1, 500));
        Assert.All(segments.SkipLast(1), s => Assert.InRange(s.HanCharacterCount, 300, 500));
        Assert.Equal(Enumerable.Range(1, segments.Count), segments.Select(s => s.Order));
        int offset = 0;
        foreach (var segment in segments) { Assert.Equal(offset, segment.StartUtf16); offset += segment.SourceText.Length; }
    }

    [Fact]
    public void ParagraphBoundary_IsPreferred_WithoutLosingCrLfOrIndentation()
    {
        string source = "甲乙丙丁。\r\n  戊己庚辛壬癸子丑";
        var segments = _segmenter.Segment(source, ChapterId, SmallOptions());
        Assert.StartsWith("甲乙丙丁。\r\n", segments[0].SourceText);
        Assert.DoesNotContain("戊", segments[0].SourceText);
        Assert.Equal(source, TextSegmenter.Reconstruct(segments));
    }

    [Fact]
    public void DialogueBoundary_KeepsPunctuationAndClosingQuoteTogether()
    {
        string source = "“甲乙丙丁。”戊己庚辛壬癸子丑";
        var segments = _segmenter.Segment(source, ChapterId, SmallOptions());
        Assert.Equal("“甲乙丙丁。”", segments[0].SourceText);
        Assert.Equal(source, TextSegmenter.Reconstruct(segments));
    }

    [Fact]
    public void VeryLongParagraph_WithoutPunctuation_UsesSafeFallback()
    {
        var segments = _segmenter.Segment(new string('汉', 1601), ChapterId);
        Assert.True(segments.Count > 1);
        Assert.All(segments, s => Assert.InRange(s.HanCharacterCount, 1, 500));
        Assert.Equal(new string('汉', 1601), TextSegmenter.Reconstruct(segments));
    }

    [Fact]
    public void SupplementaryHan_CombiningMarks_EmojiSequences_AreNotSplit()
    {
        string source = string.Concat(Enumerable.Repeat("\U00020000👩🏽‍💻e\u0301🇻🇳中。\r\n", 80));
        var boundaries = StringInfo.ParseCombiningCharacters(source).Append(source.Length).ToHashSet();
        var segments = _segmenter.Segment(source, ChapterId, new()
        {
            MinimumHanCharacters = 3, TargetHanCharacters = 4, MaximumHanCharacters = 5, MaximumTextElements = 30
        });
        Assert.Equal(160, TextSegmenter.CountHanCharacters(source));
        Assert.Equal(source, TextSegmenter.Reconstruct(segments));
        foreach (var segment in segments)
        {
            Assert.Contains(segment.StartUtf16, boundaries);
            Assert.Contains(segment.StartUtf16 + segment.SourceText.Length, boundaries);
        }
    }

    [Fact]
    public void MostlyLatinPassage_IsStillBoundedAndLossless()
    {
        string source = new string('a', 9000) + "\t\r\n　中\n";
        var segments = _segmenter.Segment(source, ChapterId);
        Assert.Equal(source, TextSegmenter.Reconstruct(segments));
        Assert.All(segments, s => Assert.InRange(new StringInfo(s.SourceText).LengthInTextElements, 1, 2000));
    }

    [Fact]
    public void Ids_AreStableForSameChapterSourceAndConfiguration()
    {
        string source = new string('中', 1300);
        var first = _segmenter.Segment(source, ChapterId);
        var second = _segmenter.Segment(source, ChapterId);
        Assert.Equal(first.Select(s => s.Id), second.Select(s => s.Id));
        Assert.Equal(first.Count, first.Select(s => s.Id).Distinct().Count());
        Assert.NotEqual(first[0].Id, _segmenter.Segment(source, Guid.NewGuid())[0].Id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n\r　\n")]
    [InlineData("甲\r乙\n丙\r\n丁　。")]
    public void EmptyAndWhitespace_StayExactlyAsReceived(string source)
    {
        Assert.Equal(source, TextSegmenter.Reconstruct(_segmenter.Segment(source, ChapterId)));
    }

    [Fact]
    public void RandomMixedInputs_AlwaysRoundTrip_OnGraphemeBoundaries()
    {
        var random = new Random(20261011);
        string[] tokens = ["中", "文", "。", "“", "”", "\r\n", "\t", " ", "\U00020000", "e\u0301", "👨‍👩‍👧‍👦"];
        for (int run = 0; run < 100; run++)
        {
            string source = string.Concat(Enumerable.Range(0, 100 + random.Next(500)).Select(_ => tokens[random.Next(tokens.Length)]));
            var segments = _segmenter.Segment(source, ChapterId, new()
            {
                MinimumHanCharacters = 5, TargetHanCharacters = 10, MaximumHanCharacters = 15, MaximumTextElements = 60
            });
            Assert.Equal(source, TextSegmenter.Reconstruct(segments));
            var boundaries = StringInfo.ParseCombiningCharacters(source).Append(source.Length).ToHashSet();
            Assert.All(segments, s =>
            {
                Assert.NotEmpty(s.SourceText);
                Assert.InRange(s.HanCharacterCount, 0, 15);
                Assert.Contains(s.StartUtf16, boundaries);
                Assert.Contains(s.StartUtf16 + s.SourceText.Length, boundaries);
            });
        }
    }

    [Fact]
    public void InvalidConfiguration_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => _segmenter.Segment("中", ChapterId, new() { MinimumHanCharacters = 501 }));
    }

    [Fact]
    public void UnpairedSurrogates_AreRejectedRatherThanSilentlyReplacedInStorage()
    {
        Assert.Throws<ArgumentException>(() => _segmenter.Segment("中" + (char)0xD800, ChapterId));
        Assert.Throws<ArgumentException>(() => _segmenter.Segment(((char)0xDC00) + "文", ChapterId));
    }

    private static SegmenterOptions SmallOptions() => new()
    {
        MinimumHanCharacters = 3, TargetHanCharacters = 4, MaximumHanCharacters = 6, MaximumTextElements = 30
    };
}
