using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed partial class TranslationServiceTests
{
    // Shape of a real Index-Translate-9B output: the translation, then the dialogue map, itself translated.
    private const string Translation = "Tô Bạch gật đầu.\n“Được thôi, hẹn lần sau.”";
    private const string EchoedMap = """

        “Tiểu Bạch đạo trưởng đúng là biết nói chuyện…”: Lưu Phú → Tô Bạch (có lẽ) (tôi = tôi; xưng hô = Tô đạo trưởng)
        “Thôi bỏ đi, cậu cũng biết mà, tôi không uống rượu.”: Tô Bạch → (người nghe không rõ)
        “Mẹ, trên ghế phụ của mẹ có gì vậy?”: Lâm Thu Dao → (người nghe không rõ)
        """;

    [Fact]
    public async Task Dialogue_map_echoed_after_the_translation_is_cut_and_reported()
    {
        var (service, client, _, chapter) = Create();
        client.Respond = (_, _) => Task.FromResult(FakeClient.Completion(Translation + EchoedMap));
        var attempt = await service.TranslateAsync(chapter.Segments[0].Id);

        Assert.Equal(AttemptOutcome.Completed, attempt.Outcome);
        Assert.Equal(Translation, attempt.AiText);
        Assert.Contains(attempt.Issues, i => i.Code == IssueCode.EchoedPromptNotes && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public async Task Output_that_is_only_the_echoed_map_is_rejected_not_saved()
    {
        // Seen live: a 1,198-character segment came back as two map lines and nothing else.
        var (service, client, _, chapter) = Create();
        string onlyMap = "“Mẹ, con đói rồi, đút cho con ăn đi.”: Lâm Thu Dao → Tô Bạch (có khả năng) (tự xưng: con; xưng hô: mẹ)\n" +
            "“Ừm....”: Tô Bạch → Thu Ngọc (có khả năng) (tự xưng: ta; xưng hô: Thu Ngọc)";
        client.Respond = (_, _) => Task.FromResult(FakeClient.Completion(onlyMap));
        var attempt = await service.TranslateAsync(chapter.Segments[0].Id);
        Assert.Equal(AttemptOutcome.InvalidOutput, attempt.Outcome);
        Assert.Contains(attempt.Issues, i => i.Code == IssueCode.EmptyOutput);
    }

    [Fact]
    public async Task Notes_mixed_into_the_prose_reject_the_attempt()
    {
        var (service, client, _, chapter) = Create();
        string mixed = "Tô Bạch gật đầu.\n“A”: Lưu Phú → Tô Bạch (tôi = tôi; xưng hô = anh)\n“B”: Tô Bạch → (người nghe không rõ)\nAnh ta bước đi.";
        client.Respond = (_, _) => Task.FromResult(FakeClient.Completion(mixed));
        var attempt = await service.TranslateAsync(chapter.Segments[0].Id);
        Assert.Equal(AttemptOutcome.InvalidOutput, attempt.Outcome);
    }
}

public sealed class PromptEchoFilterTests
{
    [Fact]
    public void A_single_note_line_with_no_translation_is_removed()
    {
        PromptEchoFilter.Result result = PromptEchoFilter.Strip("“Ừm....”: Tô Bạch → Thu Ngọc (có khả năng) (tự xưng: ta; xưng hô: Thu Ngọc)");
        Assert.Equal("", result.Text);
        Assert.Equal(1, result.RemovedLines);
    }

    [Fact]
    public void Ordinary_prose_with_quotes_colons_and_one_arrow_is_left_untouched()
    {
        string prose = "“Đi thôi”: hắn nói xong liền quay lưng.\nCon đường từ làng → thị trấn rất dài.\n“Được.”";
        PromptEchoFilter.Result result = PromptEchoFilter.Strip(prose);
        Assert.Equal(prose, result.Text);
        Assert.Equal(0, result.RemovedLines);
    }

    [Fact]
    public void Default_instTrans_prompt_turns_character_notes_into_numbered_constraints_after_the_source()
    {
        string notes = "CHARACTER CONSISTENCY (confirmed project rules):\n- CHARACTER 苏白 = Tô Bạch\n" +
            "- DIALOGUE Tô Bạch -> Lâm Phong: self = ta; address = huynh (LOCKED)\n" +
            "Use these rules only when the character and dialogue roles are explicit in the source.";
        string prompt = new TranslationPromptBuilder().Build("你好。", [new GlossaryEntry("林风", "Lâm Phong")], "", PromptPreset.Default, notes)
            .ReplaceLineEndings("\n");
        int source = prompt.IndexOf("【源文】\n你好。\n", StringComparison.Ordinal);
        int constraints = prompt.IndexOf("【约束要求】", StringComparison.Ordinal);
        Assert.True(source >= 0 && constraints > source);
        Assert.Contains("\n1. 【硬性要求】专名/术语对照（必须使用箭头右侧的越南语译名）: 林风→Lâm Phong\n", prompt);
        Assert.Contains("\n2. 【硬性要求】人物 苏白 译为 Tô Bạch\n", prompt);
        Assert.Contains("\n3. 【硬性要求】Tô Bạch对Lâm Phong说话时自称“ta”，称呼对方“huynh”\n", prompt);
        Assert.Contains("\n4. 【注意】以上人物与称呼规则只在原文明确说话人和听话人时使用", prompt);
        Assert.DoesNotContain("CHARACTER CONSISTENCY", prompt);
        Assert.DoesNotContain("Use these rules", prompt);
        Assert.EndsWith("内容\n6. 【注意】译文使用自然流畅的越南语文学语言，保留原文的语气与情感\n\n只输出译文，不要有任何额外说明。", prompt);
    }

    [Fact]
    public void InstTrans_dialogue_map_keeps_only_quotes_with_a_known_listener_and_addressing()
    {
        string map = "DIALOGUE MAP (quotes of this passage in order; speaker → listener):\n" +
            "- “你好…”: Lan Khê → (listener unclear)\n" +
            "- “走吧”: Lan Khê → Ứng Hoa (likely) (self=em / tôi; address=anh / Ứng Hoa)\n" +
            "Translate each listed quote with that speaker's self-reference and form of address for that listener.";
        string prompt = new TranslationPromptBuilder().Build("“走吧”", [], "STORY SETTING: modern. Dialogue must use modern Vietnamese addressing. Never use archaic addressing: ta, ngươi.",
            PromptPreset.Default, map).ReplaceLineEndings("\n");
        Assert.Contains("1. 【注意】“走吧”这句话是Lan Khê对Ứng Hoa说的：说话人自称“em / tôi”，称呼对方“anh / Ứng Hoa”\n", prompt);
        Assert.Contains("2. 【注意】故事背景为现代", prompt);
        Assert.Contains("禁止使用古风称呼：ta, ngươi\n", prompt);
        Assert.DoesNotContain("你好…", prompt);
        Assert.DoesNotContain("DIALOGUE MAP", prompt);
    }

    [Fact]
    public void Legacy_template_still_labels_character_notes_never_to_be_output()
    {
        var legacy = PromptPreset.Default with
        {
            Template = "You are a professional translator.\n{{glossary}}\n{{context}}\n<source_text>\n{{source_text}}\n</source_text>"
        };
        string notes = "CHARACTER CONSISTENCY (confirmed project rules):\n- CHARACTER 苏白 = Tô Bạch";
        string prompt = new TranslationPromptBuilder().Build("你好。", [], "", legacy, notes);
        int instructions = prompt.IndexOf("You are a professional", StringComparison.Ordinal);
        int characters = prompt.IndexOf("CHARACTER CONSISTENCY", StringComparison.Ordinal);
        Assert.True(instructions >= 0 && characters > instructions);
        Assert.True(characters < prompt.IndexOf("<source_text>", StringComparison.Ordinal));
        Assert.Contains("never translate, repeat or output these notes", prompt);
    }
}
