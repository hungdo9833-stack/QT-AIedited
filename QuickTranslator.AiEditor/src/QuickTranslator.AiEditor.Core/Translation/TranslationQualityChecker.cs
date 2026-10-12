using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Segmentation;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class TranslationQualityChecker
{
    public IReadOnlyList<TranslationIssue> Check(string source, OllamaCompletion completion, ModelOptions options)
    {
        var issues = new List<TranslationIssue>();
        string text = completion.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            issues.Add(new(IssueCode.EmptyOutput, IssueSeverity.Error, "Ollama trả bản dịch rỗng. Hãy dịch lại hoặc tự viết bản nháp."));
            return issues;
        }
        if (completion.Done != true || completion.DoneReason is "length" or "max_tokens" ||
            completion.Metrics.OutputTokens >= options.MaximumOutputTokens)
            issues.Add(new(IssueCode.TruncatedOutput, IssueSeverity.Warning, "Kết quả có thể bị cắt hoặc chạm giới hạn token. Kiểm tra cuối đoạn trước khi duyệt."));

        int han = TextSegmenter.CountHanCharacters(source);
        int remainingHan = TextSegmenter.CountHanCharacters(text);
        int letters = text.Count(char.IsLetter);
        if (han >= 80 && letters < han / 2)
            issues.Add(new(IssueCode.PossibleMissingContent, IssueSeverity.Warning, "Bản dịch ngắn bất thường so với nguyên văn; có thể dịch thiếu."));
        if (han >= 30 && remainingHan >= Math.Max(12, han / 5))
            issues.Add(new(IssueCode.UntranslatedChinese, IssueSeverity.Warning, "Còn nhiều chữ Hán trong kết quả; có thể chưa dịch hết."));
        int sourceParagraphs = NonEmptyParagraphs(source), targetParagraphs = NonEmptyParagraphs(text);
        if (sourceParagraphs >= 3 && targetParagraphs * 2 < sourceParagraphs)
            issues.Add(new(IssueCode.PossibleMissingContent, IssueSeverity.Warning, "Số đoạn văn giảm nhiều; có thể bị gộp đoạn hoặc dịch thiếu."));
        if (HasRepeatedBlock(text))
            issues.Add(new(IssueCode.SuspiciousRepetition, IssueSeverity.Warning, "Có cụm từ lặp liên tiếp bất thường. Đây là cảnh báo cần người đọc kiểm tra."));
        if (EndsAsSentence(source) && !EndsAsSentence(text))
            issues.Add(new(IssueCode.IncompleteFinalSentence, IssueSeverity.Warning, "Nguyên văn có câu kết thúc nhưng bản dịch không có dấu kết thúc tương ứng; hãy đối chiếu câu cuối, không suy ra lỗi chỉ từ độ dài."));
        if (han >= 40 && !EndsAsSentence(source))
            issues.Add(new(IssueCode.SourceEndsMidSentence, IssueSeverity.Warning, "Đoạn nguồn kết thúc giữa câu/lời thoại hoặc không có dấu kết thúc. Kiểm tra câu cuối cùng với đoạn kế bên."));
        return issues.DistinctBy(i => i.Code).ToArray();
    }

    private static bool EndsAsSentence(string text)
    {
        string ending = text.TrimEnd().TrimEnd('”', '"', '\'', '’', '」', '』', ')', '）', ']', '】').TrimEnd();
        return ending.Length > 0 && ending[^1] is '.' or '。' or '!' or '！' or '?' or '？' or '…';
    }

    private static int NonEmptyParagraphs(string text) => Regex.Split(text, @"\r\n|\r|\n").Count(s => !string.IsNullOrWhiteSpace(s));

    private static bool HasRepeatedBlock(string text)
    {
        string[] words = Regex.Matches(text, @"[\p{L}\p{N}]+").Select(m => m.Value.ToLowerInvariant()).ToArray();
        for (int width = 2; width <= 12 && width * 3 <= words.Length; width++)
        for (int start = 0; start + width * 3 <= words.Length; start++)
        {
            bool repeated = true;
            for (int i = 0; i < width * 2; i++)
                if (words[start + i] != words[start + width + i]) { repeated = false; break; }
            if (repeated) return true;
        }
        return false;
    }
}
