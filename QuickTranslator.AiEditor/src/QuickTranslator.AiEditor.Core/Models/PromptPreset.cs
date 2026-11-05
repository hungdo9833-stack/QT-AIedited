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
    // Index-Translate's own instTrans format (see TranslationPromptBuilder.InstTrans). Lines starting with
    // 【硬性要求】/【注意】/【参考】 under 【约束要求】 are numbered by the builder, so presets can add their own.
    public const string DefaultTemplate = """
        请将以下中文小说翻译成越南语，并且严格遵循所有约束要求。

        【源文】
        {{source_text}}

        【约束要求】
        {{glossary}}
        {{context}}
        【硬性要求】逐句完整翻译，保留原文的段落与对话格式，不要省略、概括或添加内容
        【注意】译文使用自然流畅的越南语文学语言，保留原文的语气与情感

        只输出译文，不要有任何额外说明。
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
