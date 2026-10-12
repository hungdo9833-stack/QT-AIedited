using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace QuickTranslator.AiEditor.Core.Importing;

public sealed class ChapterDetector
{
    private static readonly IChapterTitleRecognizer[] Recognizers =
        [new ChineseOrdinalHeadingRecognizer(), new LatinNumberedHeadingRecognizer(), new SpecialChapterHeadingRecognizer()];

    public ImmutableImportPlan Detect(string source, string sourceName = "clipboard") => Detect(RawSourceReader.FromText(source, sourceName));
    public ImmutableImportPlan Detect(RawSourceSnapshot source)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateSnapshot(source);
        ImmutableArray<SourceLine> lines = UnicodeLineScanner.Scan(source);
        var candidates = ImmutableArray.CreateBuilder<HeadingCandidate>();
        for (int index = 0; index < lines.Length; index++)
        {
            foreach (IChapterTitleRecognizer recognizer in Recognizers)
            {
                if (!recognizer.TryRecognize(lines[index].Content, out HeadingRecognition recognition)) continue;
                candidates.Add(CandidateClassifier.Classify(lines[index], index, lines, recognition));
                break;
            }
        }
        ImmutableArray<HeadingCandidate> candidateArray = candidates.ToImmutable();
        var issues = ChapterSequenceAnalyzer.Analyze(candidateArray, source.Text.Length).ToBuilder();
        ImmutableArray<PlannedSourceRegion> regions = BoundaryPlanner.Plan(source, lines, candidateArray);
        PlannedSourceRegion? prefix = regions.FirstOrDefault(x => x.Kind == PlannedRegionKind.Unclassified);
        if (prefix is not null && candidateArray.Any(x => x.Decision != CandidateDecision.Rejected))
            issues.Add(new(DetectionIssueCode.UnclassifiedSource, DetectionIssueSeverity.Information, prefix.Span,
                "Vùng trước tiêu đề đầu tiên được giữ nguyên dưới dạng chưa phân loại.", []));
        var plan = new ImmutableImportPlan(source, lines, candidateArray, regions, issues.ToImmutable());
        plan.ValidateExactCoverage();
        return plan;
    }

    private static void ValidateSnapshot(RawSourceSnapshot source)
    {
        if (source.OriginalBytes.IsDefault) throw new InvalidDataException("Snapshot thiếu raw bytes.");
        string rawHash = Convert.ToHexString(SHA256.HashData(source.OriginalBytes.AsSpan()));
        if (!string.Equals(rawHash, source.RawSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Raw source hash không khớp snapshot.");
        byte[] textBytes;
        try { textBytes = new UTF8Encoding(false, true).GetBytes(source.Text); }
        catch (EncoderFallbackException ex) { throw new InvalidDataException("Snapshot chứa UTF-16 không hợp lệ.", ex); }
        string textHash = Convert.ToHexString(SHA256.HashData(textBytes));
        if (!string.Equals(textHash, source.TextSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Text hash không khớp snapshot.");
    }
}
