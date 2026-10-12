using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Importing;

try
{
    (RawSourceSnapshot source, string mode) = await LoadSourceAsync(args);
    var stopwatch = Stopwatch.StartNew();
    ImmutableImportPlan plan = new ChapterDetector().Detect(source);
    stopwatch.Stop();
    bool exact = plan.ReconstructSource() == source.Text;
    var report = new
    {
        Mode = mode,
        source.SourceName,
        source.EncodingName,
        source.RawSha256,
        CharactersUtf16 = source.Text.Length,
        Lines = plan.Lines.Length,
        Candidates = plan.Candidates.Length,
        Accepted = plan.Candidates.Count(x => x.Decision == CandidateDecision.Accepted),
        NeedsReview = plan.Candidates.Count(x => x.Decision == CandidateDecision.NeedsReview),
        Rejected = plan.Candidates.Count(x => x.Decision == CandidateDecision.Rejected),
        Chapters = plan.Regions.Count(x => x.Kind == PlannedRegionKind.Chapter),
        SpecialChapters = plan.Regions.Count(x => x.Kind == PlannedRegionKind.SpecialChapter),
        Volumes = plan.Regions.Count(x => x.Kind == PlannedRegionKind.Volume),
        UnclassifiedRegions = plan.Regions.Count(x => x.Kind == PlannedRegionKind.Unclassified),
        Issues = plan.Issues.GroupBy(x => x.Code).ToDictionary(x => x.Key.ToString(), x => x.Count()),
        ExactRoundTrip = exact,
        ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
        Samples = plan.Candidates.Where(x => x.Decision != CandidateDecision.Rejected).Take(10).Select(x => new
        {
            x.LineNumber, x.RawHeading, Kind = x.Kind.ToString(), x.Number, x.Title, x.Confidence,
            Decision = x.Decision.ToString(), StartUtf16 = x.HeadingSpan.StartUtf16
        })
    };
    Console.OutputEncoding = new UTF8Encoding(false);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    }));
    return exact ? 0 : 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Chapter Detector thất bại: {ex.Message}");
    return 1;
}

static async Task<(RawSourceSnapshot Source, string Mode)> LoadSourceAsync(string[] arguments)
{
    if (arguments.Length == 0 || arguments[0].Equals("--synthetic", StringComparison.OrdinalIgnoreCase))
    {
        int count = arguments.Length > 1 && int.TryParse(arguments[1], out int parsed) ? parsed : 5_000;
        if (count is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(arguments), "Số chapter synthetic phải từ 1 đến 100.000.");
        var text = new StringBuilder(count * 40);
        int volume = 0;
        for (int index = 0; index < count; index++)
        {
            int chapter = index % 1_000 + 1;
            if (chapter == 1) text.Append("第").Append(++volume).Append("卷 合成卷").Append("\r\n");
            text.Append("第").Append(chapter).Append("章 合成标题").Append(index + 1).Append("\r\n")
                .Append("这是用于检测的合成正文，不会写入任何 Project。😀").Append("\r\n");
        }
        return (RawSourceReader.FromText(text.ToString(), $"synthetic-{count}.txt"), $"synthetic:{count}");
    }

    string path = arguments[0];
    SourceTextEncoding encoding = SourceTextEncoding.Auto;
    for (int index = 1; index < arguments.Length; index++)
    {
        if (!arguments[index].Equals("--encoding", StringComparison.OrdinalIgnoreCase) || index + 1 >= arguments.Length)
            throw new ArgumentException("Cú pháp: QTStudio.ChapterDetector.Demo.exe <file> [--encoding utf8|gbk|gb18030]");
        encoding = arguments[++index].ToLowerInvariant() switch
        {
            "utf8" or "utf-8" => SourceTextEncoding.Utf8,
            "gbk" => SourceTextEncoding.Gbk,
            "gb18030" => SourceTextEncoding.Gb18030,
            _ => throw new ArgumentException("Encoding phải là utf8, gbk hoặc gb18030.")
        };
    }
    return (await RawSourceReader.ReadFileAsync(path, encoding), $"file:{Path.GetFullPath(path)}");
}
