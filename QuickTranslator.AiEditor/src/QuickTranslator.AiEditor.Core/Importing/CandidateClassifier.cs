using System.Collections.Immutable;

namespace QuickTranslator.AiEditor.Core.Importing;

internal static class CandidateClassifier
{
    private static readonly string[] BodyConnectors = ["的内容", "的故事", "中写", "里写", "讲述", "說的是", "说的是", "内容是"];

    public static HeadingCandidate Classify(SourceLine line, int lineIndex, ImmutableArray<SourceLine> lines, HeadingRecognition recognition)
    {
        var evidence = ImmutableArray.CreateBuilder<CandidateEvidence>();
        Add("recognizer", $"{recognition.Recognizer} nhận diện marker {recognition.Marker}.", recognition.BaseConfidence);
        if (recognition.Number is not null) Add("number", "Số thứ tự được phân tích hợp lệ.", 0.06);
        if (lineIndex == 0) Add("source-start", "Tiêu đề nằm ở đầu nguồn.", 0.04);
        if (lineIndex > 0 && lines[lineIndex - 1].IsBlank) Add("blank-before", "Có dòng trống trước tiêu đề.", 0.04);
        if (lineIndex + 1 < lines.Length && lines[lineIndex + 1].IsBlank) Add("blank-after", "Có dòng trống sau tiêu đề.", 0.04);
        if (line.Content.Trim().Length <= 60) Add("concise", "Dòng tiêu đề ngắn, độc lập.", 0.04);
        if (recognition.HasExplicitSeparator) Add("separator", "Marker và tên được ngăn cách rõ.", 0.02);

        if (recognition.Title.Length > 160) Add("very-long", "Dòng quá dài đối với tiêu đề chương.", -0.38);
        else if (recognition.Title.Length > 80) Add("long", "Tên chương dài bất thường.", -0.18);
        bool sentenceEnding = recognition.Title.IndexOfAny(['。', '！', '？', '；']) >= 0;
        bool inlineProse = !recognition.HasExplicitSeparator && sentenceEnding &&
            (recognition.Title.Length >= 8 || recognition.Title.IndexOfAny(['，', ',']) >= 0);
        if (sentenceEnding)
            Add("sentence-ending", "Phần sau marker có dấu kết câu như văn bản thường.", inlineProse ? -0.46 : -0.16);
        if (recognition.Title.IndexOfAny(['“', '”', '「', '」', '『', '』', '"']) >= 0)
            Add("dialogue", "Dòng chứa dấu hội thoại.", -0.18);
        if (BodyConnectors.Any(x => recognition.Title.StartsWith(x, StringComparison.Ordinal)))
            Add("body-connector", "Phần sau marker bắt đầu như câu mô tả nội dung.", recognition.HasExplicitSeparator ? -0.20 : -0.44);
        if (!recognition.HasExplicitSeparator &&
            (recognition.Marker is "节" or "節" && recognition.Title.StartsWith("课", StringComparison.Ordinal) ||
             recognition.Marker == "回" && recognition.Title.StartsWith("合", StringComparison.Ordinal)))
            Add("lexical-continuation", "Marker ghép với chữ sau thành cụm từ trong câu, không phải tiêu đề chương.", -0.70);

        double confidence = Math.Clamp(evidence.Sum(x => x.Weight), 0, 1);
        CandidateDecision decision = confidence >= 0.70 ? CandidateDecision.Accepted :
            confidence >= 0.45 ? CandidateDecision.NeedsReview : CandidateDecision.Rejected;
        return new(line.Number, line.ContentSpan, line.Content, recognition.Kind, recognition.Number,
            recognition.NumberText, recognition.Title, recognition.Marker, recognition.Recognizer,
            Math.Round(confidence, 3), decision, evidence.ToImmutable());

        void Add(string code, string description, double weight) => evidence.Add(new(code, description, weight));
    }
}
