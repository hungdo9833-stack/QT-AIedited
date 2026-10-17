using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Models;

public sealed record PromptPreset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Preset mới";
    public string Description { get; init; } = "";
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public int Revision { get; init; } = 1;
    public string Template { get; init; } = DefaultTemplate;
    public const string DefaultTemplate = """
        You are a professional Chinese (zh) to Vietnamese (vi) translator.
        Accurately convey the meaning and nuances of this novel excerpt in natural Vietnamese.
        Preserve names, paragraph breaks, dialogue, tone and punctuation. Do not omit, summarize or invent content.
        Produce only the Vietnamese translation, without explanations, commentary or Markdown fences.
        {{glossary}}
        {{context}}
        Please translate the following Chinese text into Vietnamese:
        <source_text>
        {{source_text}}
        </source_text>
        """;
    public static PromptPreset Default => new()
    {
        Id = Guid.Parse("a52489df-2373-4e94-811c-a57b5a577050"), Name = "Tiểu thuyết Trung–Việt"
    };

    public void Validate()
    {
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 200 || Description.Length > 1000 || Revision < 1 ||
            string.IsNullOrWhiteSpace(Template) || Template.Length > 16000)
            throw new ArgumentException("Preset cần tên, ID, phiên bản, mô tả tối đa 1.000 ký tự và prompt hợp lệ (tối đa 16.000 ký tự).");
        var variables = Regex.Matches(Template, @"\{\{([^{}]+)\}\}").Select(m => m.Groups[1].Value).ToArray();
        if (variables.Count(v => v == "source_text") != 1 || variables.Any(v => v is not ("source_text" or "glossary" or "context")))
            throw new ArgumentException("Prompt phải có đúng một {{source_text}}; chỉ hỗ trợ thêm {{glossary}} và {{context}}.");
    }

    public override string ToString() => Name;
}
