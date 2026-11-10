using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class AdvancedEditingTests
{
    private const string Draft = "Hắn khách khí gọi nàng là biểu di mẫu.\nThẩm Duy Trinh là con trai ruột thịt của phụ thân sinh thành.";
    private const string Edited = "Hắn khách sáo gọi nàng là dì họ.\nThẩm Duy Trinh là con trai ruột của người đã khuất.";

    [Fact]
    public async Task Off_by_default_sends_one_request()
    {
        var chapter = TranslationService.CreateChapter("Thử", "他客气地称她为表姨母。");
        var client = new FakeClient { Respond = (_, _) => Task.FromResult(FakeClient.Completion(Draft)) };
        var service = new TranslationService(chapter, client, new MemoryStore());

        await service.TranslateAsync(chapter.Segments[0].Id);

        Assert.Equal(1, client.Calls);
        TranslationAttempt attempt = service.GetSnapshot().Segments[0].Attempts.Single();
        Assert.Null(attempt.PreEditText);
        Assert.Null(attempt.EditingNote);
    }

    [Fact]
    public async Task On_sends_the_draft_with_the_source_and_keeps_the_draft_in_the_attempt()
    {
        var chapter = TranslationService.CreateChapter("Thử", "他客气地称她为表姨母。");
        chapter.CharacterProject.AdvancedEditing = true;
        chapter.CharacterProject.Setting = StorySetting.Historical;
        var client = new FakeClient
        {
            Respond = (call, _) => Task.FromResult(FakeClient.Completion(call == 1 ? Draft : "```\n" + Edited + "\n```"))
        };
        var service = new TranslationService(chapter, client, new MemoryStore());

        await service.TranslateAsync(chapter.Segments[0].Id);

        Assert.Equal(2, client.Calls);
        string editing = client.Prompts[1];
        Assert.StartsWith(AdvancedEditing.Marker, editing);
        Assert.Contains("他客气地称她为表姨母。", editing);
        Assert.Contains(Draft.Split('\n')[0], editing);
        Assert.Contains("cổ trang", editing);
        Assert.Contains("Giữ đúng 2 đoạn", editing);
        TranslationSegment segment = service.GetSnapshot().Segments[0];
        TranslationAttempt attempt = segment.Attempts.Single();
        Assert.Equal(Edited.Replace("\n", Environment.NewLine), attempt.AiText);
        Assert.Equal(Draft, attempt.PreEditText);
        Assert.Equal("Đã biên tập nâng cao.", attempt.EditingNote);
        Assert.Contains("dì họ", segment.EditableText);
    }

    [Fact]
    public async Task Edited_text_that_lost_a_paragraph_is_dropped_and_the_draft_kept()
    {
        var chapter = TranslationService.CreateChapter("Thử", "他客气地称她为表姨母。");
        chapter.CharacterProject.AdvancedEditing = true;
        var client = new FakeClient
        {
            Respond = (call, _) => Task.FromResult(FakeClient.Completion(call == 1 ? Draft : "Hắn khách sáo gọi nàng là dì họ."))
        };
        var service = new TranslationService(chapter, client, new MemoryStore());

        await service.TranslateAsync(chapter.Segments[0].Id);

        TranslationAttempt attempt = service.GetSnapshot().Segments[0].Attempts.Single();
        Assert.Equal(Draft, attempt.AiText);
        Assert.Null(attempt.PreEditText);
        Assert.Contains("1 đoạn, bản nháp có 2 đoạn", attempt.EditingNote);
        Assert.Equal(AttemptOutcome.Completed, attempt.Outcome);
    }

    [Fact]
    public async Task A_failed_editing_request_keeps_the_translation()
    {
        var chapter = TranslationService.CreateChapter("Thử", "他客气地称她为表姨母。");
        chapter.CharacterProject.AdvancedEditing = true;
        var client = new FakeClient
        {
            Respond = (call, _) => call == 1 ? Task.FromResult(FakeClient.Completion(Draft))
                : Task.FromException<LlmCompletion>(new LlmRequestException(LlmErrorKind.Timeout, "hết giờ"))
        };
        var service = new TranslationService(chapter, client, new MemoryStore());

        await service.TranslateAsync(chapter.Segments[0].Id);

        TranslationAttempt attempt = service.GetSnapshot().Segments[0].Attempts.Single();
        Assert.Equal(Draft, attempt.AiText);
        Assert.Contains("hết giờ", attempt.EditingNote);
        Assert.Equal(AttemptOutcome.Completed, attempt.Outcome);
    }

    [Theory]
    [InlineData("Một đoạn rất ngắn.\nHai.", "độ dài")]
    [InlineData("Hắn khách sáo gọi nàng là 表姨母 dì họ ạ.\nThẩm Duy Trinh là con trai ruột của người đã khuất.", "chữ Hán")]
    [InlineData("   ", "rỗng")]
    public void Unsafe_edits_are_rejected(string edited, string reason)
    {
        Assert.Null(AdvancedEditing.Accept(Draft, edited, out string? why));
        Assert.Contains(reason, why);
    }

    [Fact]
    public void A_paragraph_that_brings_in_new_addressing_keeps_the_draft_wording()
    {
        // Real edits from 花中娇客: "ngài" became "ông", a young maid became "tỷ", narration turned into "huynh ấy".
        string draft = "A Xuân chắp hai tay trước ngực, nhìn chằm chằm Trương đại phu: \"Cầu xin ngài đấy, cứu nàng ấy đi mà.\"\n" +
            "Nàng sợ bị Thẩm Duy Trinh nhìn thấy sự yếu đuối của mình.\n" +
            "Chỉ là lần này, Chương Giản đối xử với Thẩm Duy Trinh, lại mang thêm vài phần kính trọng.";
        string edited = "A Xuân chắp tay, nhìn Trương đại phu: “Cầu xin ông, cứu tỷ ấy với.”\n" +
            "Nàng sợ huynh ấy nhìn thấy vẻ yếu đuối của mình.\n" +
            "Chỉ là lần này, Chương Giản kính trọng Thẩm Duy Trinh hơn trước.";
        var context = new AdvancedEditingContext(StorySetting.Historical, [], [], new HashSet<string> { "nàng", "hắn" });

        AdvancedEditingOutcome outcome = AdvancedEditing.Review(draft, edited, context);

        Assert.Equal(2, outcome.KeptParagraphs);
        string[] lines = outcome.Text!.Split(Environment.NewLine);
        Assert.Equal(draft.Split('\n')[0], lines[0]);
        Assert.Equal(draft.Split('\n')[1], lines[1]);
        Assert.Equal("Chỉ là lần này, Chương Giản kính trọng Thẩm Duy Trinh hơn trước.", lines[2]);
    }

    [Fact]
    public void Profile_addressing_is_allowed_in_dialogue_near_its_characters_but_never_in_narration()
    {
        // A draft's modern "Em" in a historical book may become the profile's "Muội"; "huynh ấy" in narration may not.
        string draft = "Thẩm Duy Trinh nhìn A Xuân.\n“…… Em không nhớ rõ nữa.”\nNàng sợ bị Thẩm Duy Trinh nhìn thấy.";
        string edited = "Thẩm Duy Trinh nhìn A Xuân.\n“… Muội không nhớ.”\nNàng sợ huynh ấy nhìn thấy.";
        var context = new AdvancedEditingContext(StorySetting.Historical, [], [], new HashSet<string> { "nàng", "hắn" },
            [new AdvancedEditingAddressRule("A Xuân", "Thẩm Duy Trinh", new HashSet<string> { "muội", "huynh" })]);

        AdvancedEditingOutcome outcome = AdvancedEditing.Review(draft, edited, context);

        string[] lines = outcome.Text!.Split(Environment.NewLine);
        Assert.Equal("“… Muội không nhớ.”", lines[1]);
        Assert.Equal("Nàng sợ bị Thẩm Duy Trinh nhìn thấy.", lines[2]);
        Assert.Equal(1, outcome.KeptParagraphs);
    }

    [Fact]
    public void A_profile_term_never_replaces_a_draft_term_the_profile_already_backs()
    {
        // Thẩm Duy Trinh says "muội" to A Xuân; his "ngươi" belongs to the rule for his servant Diệp Thanh nearby.
        string draft = "Diệp Thanh lui ra.\n“Vậy mai huynh đưa sau,” Thẩm Duy Trinh nói với A Xuân, “Muội cũng đi nghỉ sớm đi.”";
        string edited = "Diệp Thanh lui ra.\n“Vậy mai hãy đưa,” Thẩm Duy Trinh nói với A Xuân, “Ngươi cũng đi nghỉ sớm đi.”";
        var context = new AdvancedEditingContext(StorySetting.Historical, [], [], new HashSet<string> { "nàng", "hắn" },
        [
            new AdvancedEditingAddressRule("Thẩm Duy Trinh", "A Xuân", new HashSet<string> { "huynh", "muội" }),
            new AdvancedEditingAddressRule("Thẩm Duy Trinh", "Diệp Thanh", new HashSet<string> { "ta", "ngươi" })
        ]);

        AdvancedEditingOutcome outcome = AdvancedEditing.Review(draft, edited, context);

        Assert.Equal(1, outcome.KeptParagraphs);
        Assert.Equal(draft.Split('\n')[1], outcome.Text!.Split(Environment.NewLine)[1]);
    }

    [Fact]
    public void A_narrative_pronoun_used_as_address_inside_a_quote_counts_as_new_addressing()
    {
        var narrative = new HashSet<string> { "nàng", "ông" };
        Assert.True(AdvancedEditing.IntroducesAddress("Nàng nói: “Cầu xin ngài.”", "Nàng nói: “Cầu xin ông.”", narrative));
        Assert.False(AdvancedEditing.IntroducesAddress("Trương đại phu gật đầu.", "Ông gật đầu.", narrative));
    }

    [Theory]
    [InlineData("Nàng nhìn hắn.", "Nàng chăm chú nhìn hắn.")]
    [InlineData("Đây là Tĩnh Huy.", "Đây là Tĩnh Huy, em gái ruột của hắn.")]
    [InlineData("Hắn nói với nàng.", "Nàng nghe hắn nói.")]
    public void Kinship_words_and_narrative_pronouns_are_not_new_addressing(string draft, string edited) =>
        Assert.False(AdvancedEditing.IntroducesAddress(draft, edited, new HashSet<string> { "nàng", "hắn" }));

    [Fact]
    public void Paragraph_spacing_follows_the_draft()
    {
        string draft = Draft.Replace("\n", "\n\n");
        string? text = AdvancedEditing.Accept(draft, "【BẢN NHÁP】\n" + Edited, out _);
        Assert.Equal(Edited.Replace("\n", Environment.NewLine + Environment.NewLine), text);
    }

    [Fact]
    public void Translation_prompt_carries_the_style_rule_for_the_story_setting()
    {
        var project = new CharacterProject { Setting = StorySetting.Modern };
        string prompt = new TranslationPromptBuilder().Build("他来了。", [], StorySettingRules.StyleBlock(project), PromptPreset.Default);
        Assert.Contains("不要逐字照搬中文句式", prompt);
        Assert.Contains("现代背景", prompt);
        Assert.DoesNotContain("TRANSLATION STYLE", prompt);
    }
}
