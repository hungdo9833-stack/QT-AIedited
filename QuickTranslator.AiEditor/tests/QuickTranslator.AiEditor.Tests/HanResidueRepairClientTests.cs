using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class HanResidueRepairClientTests
{
    private const string Prompt = "请将以下中文小说翻译成越南语，并且严格遵循所有约束要求。\n\n【源文】\n姜锦婳说。\n师姐来了。\n\n【约束要求】\n1. 【硬性要求】逐句完整翻译\n\n只输出译文，不要有任何额外说明。";
    private static readonly ModelOptions Options = new() { Model = "gemini-3.5-flash-lite" };

    private static Task<string> HanViet(string text, CancellationToken _) => Task.FromResult(text switch
    {
        "婳" => "họa",
        "姐" => "tỷ",
        "嘞" => "lặc",
        _ => "x"
    });

    [Fact]
    public async Task Asks_for_no_Chinese_and_leaves_a_clean_translation_alone()
    {
        var gemini = new FakeClient("Khương Cẩm Họa nói.\nSư tỷ đến rồi.");
        var client = new HanResidueRepairClient(gemini, () => null, HanViet);
        LlmCompletion result = await client.TranslateAsync(Prompt, Options);
        Assert.Equal("Khương Cẩm Họa nói.\nSư tỷ đến rồi.", result.Text);
        string sent = Assert.Single(gemini.Prompts);
        Assert.Contains(HanResidueRepairClient.NoHanConstraint, sent);
        Assert.True(sent.IndexOf(HanResidueRepairClient.NoHanConstraint, StringComparison.Ordinal) <
                    sent.IndexOf("只输出译文", StringComparison.Ordinal));
        Assert.Null(client.LastReport);
    }

    [Fact]
    public async Task A_name_gets_its_Han_Viet_reading_and_a_paragraph_is_translated_again_on_the_gpu_only()
    {
        var gemini = new FakeClient("Khương Cẩm婳 nói.\nSư姐 đến rồi.");
        var gpu = new FakeClient("Sư tỷ đến rồi.");
        var client = new HanResidueRepairClient(gemini, () => new HanRepairTranslator(gpu, new ModelOptions { Model = "local" }), HanViet);
        LlmCompletion result = await client.TranslateAsync(Prompt, Options);
        Assert.Equal("Khương Cẩm Họa nói.\nSư tỷ đến rồi.", result.Text.ReplaceLineEndings("\n"));
        Assert.Single(gemini.Prompts); // the chapter is never sent again
        string paragraphPrompt = Assert.Single(gpu.Prompts);
        Assert.Contains("师姐来了。", paragraphPrompt);
        Assert.DoesNotContain("姜锦婳说。", paragraphPrompt);
        Assert.Contains("【约束要求】", paragraphPrompt);
        Assert.Equal(new HanRepairReport(1, 1, 0, 0, null), client.LastReport);
    }

    [Fact]
    public async Task Without_a_gpu_the_paragraphs_go_back_in_one_small_request()
    {
        var gemini = new FakeClient("Khương Cẩm Họa nói.\nSư姐 đến rồi.", "Sư tỷ đến rồi.");
        var client = new HanResidueRepairClient(gemini, () => null, HanViet);
        LlmCompletion result = await client.TranslateAsync(Prompt, Options);
        Assert.Equal("Khương Cẩm Họa nói.\nSư tỷ đến rồi.", result.Text.ReplaceLineEndings("\n"));
        Assert.Equal(2, gemini.Prompts.Count);
        Assert.Contains("师姐来了。", gemini.Prompts[1]);
        Assert.DoesNotContain("姜锦婳说。", gemini.Prompts[1]);
        Assert.Equal(1, client.LastReport!.ApiParagraphs);
    }

    [Fact]
    public async Task What_no_translator_fixes_is_written_in_Han_Viet_and_particles_are_dropped()
    {
        var gemini = new FakeClient("Khương Cẩm Họa nói.\nĐược嘞, sư姐 đi thôi.");
        var gpu = new FakeClient("Được嘞 đi.");
        var client = new HanResidueRepairClient(gemini, () => new HanRepairTranslator(gpu, Options), HanViet);
        LlmCompletion result = await client.TranslateAsync(Prompt, Options);
        Assert.Equal("Khương Cẩm Họa nói.\nĐược, sư tỷ đi thôi.", result.Text.ReplaceLineEndings("\n"));
        Assert.False(HanResidueRepairClient.HasHan(result.Text));
        Assert.Equal(2, client.LastReport!.HanVietSpots);
    }

    [Fact]
    public async Task Copied_prompt_lines_are_dropped_and_a_long_phrase_gets_its_VietPhrase_meaning()
    {
        var gemini = new FakeClient("Khương Cẩm Họa nói.\n【约束提示】专名/术语对照: Thanh Mộng\nTôi帮你一起整理吧, đi thôi.");
        var client = new HanResidueRepairClient(gemini, () => null, HanViet, askForNoHan: false,
            vietPhrase: (text, _) => Task.FromResult(text == "帮你一起整理吧" ? "giúp ngươi cùng nhau thu dọn đi" : "x"));
        LlmCompletion result = await client.TranslateAsync(Prompt, Options);
        Assert.Equal("Khương Cẩm Họa nói.\nTôi giúp ngươi cùng nhau thu dọn đi, đi thôi.", result.Text.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task A_gpu_worker_keeps_its_prompt_and_repairs_on_itself()
    {
        var gpu = new FakeClient("Khương Cẩm Họa nói.\nSư姐 đến rồi.", "Sư tỷ đến rồi.");
        var client = new HanResidueRepairClient(gpu, () => null, HanViet, askForNoHan: false);
        LlmCompletion result = await client.TranslateAsync(Prompt, Options);
        Assert.Equal("Khương Cẩm Họa nói.\nSư tỷ đến rồi.", result.Text.ReplaceLineEndings("\n"));
        Assert.Equal(Prompt, gpu.Prompts[0]);
        Assert.DoesNotContain(HanResidueRepairClient.NoHanConstraint, gpu.Prompts[1]);
        Assert.Contains("师姐来了。", gpu.Prompts[1]);
    }

    [Fact]
    public async Task Cantonese_words_get_their_meaning_without_asking_the_gpu()
    {
        var gemini = new FakeClient("Khương Cẩm Họa nói.\nLeung: 【瞓 rồi】 dì氹 em nha㗎.");
        var gpu = new FakeClient("không dùng");
        var client = new HanResidueRepairClient(gemini, () => new HanRepairTranslator(gpu, Options), HanViet);
        LlmCompletion result = await client.TranslateAsync(Prompt, Options);
        Assert.Equal("Khương Cẩm Họa nói.\nLeung: 【Ngủ rồi】 dì dỗ em nha.", result.Text.ReplaceLineEndings("\n"));
        Assert.Empty(gpu.Prompts);
    }

    [Theory]
    [InlineData("\"Cường ca, anh倒是 nói đi chứ?\"", "“Anh Khương, anh nói đi chứ?”", false)] // a name lost
    [InlineData("Lạc Vân Trạch nhìn sư姐 rồi gật đầu.", "Đợi cửa đóng lại, cô ấy mới quay sang hỏi chuyện khác hẳn.", false)] // another paragraph
    [InlineData("Lạc Vân Trạch thấy sư姐 bảo vệ cô ấy.", "Lạc Vân Trạch thấy sư tỷ bảo vệ cô ấy như vậy.", true)]
    public void A_new_paragraph_replaces_the_old_only_when_it_is_the_same_paragraph(string old, string replacement, bool safe) =>
        Assert.Equal(safe, HanResidueRepairClient.SafeReplacement(old, replacement));

    [Fact]
    public async Task A_prompt_that_is_not_instTrans_is_passed_through()
    {
        var gemini = new FakeClient("Sư姐");
        var client = new HanResidueRepairClient(gemini, () => null, HanViet);
        LlmCompletion result = await client.TranslateAsync("Translate: 师姐", Options);
        Assert.Equal("Sư姐", result.Text);
        Assert.Equal("Translate: 师姐", Assert.Single(gemini.Prompts));
    }

    private sealed class FakeClient(params string[] answers) : ILlmClient
    {
        private int _next;
        public List<string> Prompts { get; } = [];

        public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            string text = answers[Math.Min(_next++, answers.Length - 1)];
            return Task.FromResult(new LlmCompletion(text, true, "stop", new GenerationMetrics()));
        }
    }
}
