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
    public void Ordinary_prose_with_quotes_colons_and_one_arrow_is_left_untouched()
    {
        string prose = "“Đi thôi”: hắn nói xong liền quay lưng.\nCon đường từ làng → thị trấn rất dài.\n“Được.”";
        PromptEchoFilter.Result result = PromptEchoFilter.Strip(prose);
        Assert.Equal(prose, result.Text);
        Assert.Equal(0, result.RemovedLines);
    }

    [Fact]
    public void Character_notes_sit_after_the_instructions_and_are_labelled_never_to_be_output()
    {
        string notes = "CHARACTER CONSISTENCY (confirmed project rules):\n- CHARACTER 苏白 = Tô Bạch";
        string prompt = new TranslationPromptBuilder().Build("你好。", [], "", PromptPreset.Default, notes);
        int instructions = prompt.IndexOf("You are a professional", StringComparison.Ordinal);
        int characters = prompt.IndexOf("CHARACTER CONSISTENCY", StringComparison.Ordinal);
        Assert.True(instructions >= 0 && characters > instructions);
        Assert.True(characters < prompt.IndexOf("<source_text>", StringComparison.Ordinal));
        Assert.Contains("never translate, repeat or output these notes", prompt);
    }
}
