using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class SegmentBoundaryContextTests
{
    [Fact]
    public void Translated_excerpt_uses_tail_and_moves_to_a_sentence_boundary()
    {
        string text = new string('H', 100) + ". " + new string('T', 180) + " END";

        string excerpt = SegmentBoundaryContext.TranslatedTail(text);

        Assert.Equal("… " + new string('T', 180) + " END", excerpt);
        Assert.DoesNotContain('H', excerpt);
    }

    [Fact]
    public void Preceding_source_uses_the_validated_offset_and_a_sentence_boundary()
    {
        string discarded = new('甲', 80);
        string retained = new string('乙', 150) + "。张三说道：";
        string current = "“我知道。”";
        var chapter = Chapter(discarded + "。" + retained, current);

        string excerpt = SegmentBoundaryContext.PrecedingSource(chapter, chapter.Segments[1]);

        Assert.Equal(retained, excerpt);
        Assert.DoesNotContain('甲', excerpt);
    }

    [Fact]
    public void Preceding_source_is_skipped_when_the_offset_is_stale()
    {
        var chapter = Chapter("张三说道：", "“我知道。”");
        chapter.Segments[1].StartUtf16--;

        Assert.Empty(SegmentBoundaryContext.PrecedingSource(chapter, chapter.Segments[1]));
    }

    [Fact]
    public void Preceding_source_clamps_its_window_at_chapter_start()
    {
        var chapter = Chapter("序章。张三说道：", "“我知道。”");

        Assert.Equal("序章。张三说道：", SegmentBoundaryContext.PrecedingSource(chapter, chapter.Segments[1]));
    }

    [Theory]
    [InlineData("“你好。”")]
    [InlineData("  ‘你好。’")]
    [InlineData("\r\n「你好。」")]
    [InlineData("\t\"你好。\"")]
    public void Opening_quote_is_detected_after_whitespace(string source) =>
        Assert.True(SegmentBoundaryContext.OpensWithQuote(source));

    [Fact]
    public void Narration_is_not_treated_as_an_opening_quote() =>
        Assert.False(SegmentBoundaryContext.OpensWithQuote("张三说道：“你好。”"));

    [Fact]
    public void Quote_opening_prompt_contains_both_boundary_references_and_continuation_rule()
    {
        var chapter = Chapter("张三对李四说道：“我先走。”\r\n", "“等等，”李四喊道。");
        chapter.Segments[0].UserDraft = "Trương Tam nói với Lý Tứ: “Tôi đi trước.”";
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());

        string prompt = service.PreviewPrompt(chapter.Segments[1].Id, PromptPreset.Default);

        Assert.Contains("Previous user draft, not approved excerpt", prompt, StringComparison.Ordinal);
        Assert.Contains("… Trương Tam nói với Lý Tứ", prompt, StringComparison.Ordinal);
        Assert.Contains("Preceding source (reference only, do not translate):", prompt, StringComparison.Ordinal);
        Assert.Contains("张三对李四说道", prompt, StringComparison.Ordinal);
        Assert.Contains("The passage opens mid-conversation", prompt, StringComparison.Ordinal);
        Assert.Contains("same speaker/listener pronouns and forms of address", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Small_prompt_budget_trims_or_drops_boundary_blocks_without_throwing()
    {
        var chapter = Chapter(new string('前', 220) + "。张三说道：", "“等等。”");
        chapter.Segments[0].UserDraft = new string('ữ', 220) + " END";
        chapter.DefaultModelOptions = new() { ContextSize = 512, MaximumOutputTokens = 128 };
        var service = new TranslationService(chapter, new FakeClient(), new MemoryStore());
        var preset = new PromptPreset { Template = "{{context}}\n{{source_text}}" };

        string prompt = service.PreviewPrompt(chapter.Segments[1].Id, preset);

        Assert.Contains(chapter.Segments[1].SourceText, prompt, StringComparison.Ordinal);
        Assert.True(PromptTokenBudget.Estimate(prompt) <= 256);
    }

    private static ChapterDocument Chapter(string preceding, string current)
    {
        string original = preceding + current;
        var chapter = new ChapterDocument
        {
            OriginalText = original,
            Segments =
            [
                new() { Order = 1, StartUtf16 = 0, SourceText = preceding,
                    HanCharacterCount = TextSegmenter.CountHanCharacters(preceding) },
                new() { Order = 2, StartUtf16 = preceding.Length, SourceText = current,
                    HanCharacterCount = TextSegmenter.CountHanCharacters(current) }
            ]
        };
        foreach (TranslationSegment segment in chapter.Segments)
            segment.Id = TextSegmenter.CreateStableId(chapter.Id, segment.StartUtf16, segment.SourceText);
        return chapter;
    }
}
