using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

const int MaximumRequests = 5;
if (!string.Equals(Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_LIVE_AUTHORIZED"),
        "CHO PHÉP TEST API THẬT", StringComparison.Ordinal))
    throw new InvalidOperationException("Live preflight chưa được cấp quyền rõ ràng.");

string apiKey = Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY") ?? "";
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("Thiếu QTSTUDIO_GEMINI_API_KEY.");

string output = args.Length > 0 ? args[0] :
    Path.Combine("artifacts", "benchmarks", "gemini-live-preflight-10k.json");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
string root = Path.Combine(Path.GetTempPath(), "qtaie-gemini-live-preflight-" + Guid.NewGuid().ToString("N"));
var watch = Stopwatch.StartNew();
BookIntelligenceState? state = null;
GeminiSurveyPoolClient? pool = null;
Exception? failure = null;

try
{
    List<CorpusChapterIndex> corpus = BuildCorpus();
    var repository = new ProjectRepository();
    ProjectSession session = await repository.CreateAsync(root, "Gemini live preflight 10k");
    var store = new BookIntelligenceStore(session);
    state = await store.LoadAsync();
    state.ScanMode = IntelligenceScanMode.FullSemantic;
    state.Chapters = corpus;
    ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();

    using var handler = new HttpClientHandler { AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    IReadOnlyList<GeminiSurveyModel> models = GeminiSurveyPoolClient.ModelsFromSettings(
        "gemini-3.8-flash,gemini-3.6-flash");
    Guid scope = GeminiSurveyProjectScope.Resolve(apiKey,
        Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_PROJECT_ID"));
    pool = new GeminiSurveyPoolClient(http, apiKey, scope, models, MaximumRequests);
    state.AnalysisConcurrency = Math.Min(pool.RecommendedConcurrency, MaximumRequests);
    state = await store.SaveAsync(state);
    state = await new AdaptiveBookIntelligenceAnalyzer(pool).AnalyzeAsync(session, state, store, resources,
        new ModelOptions
        {
            Model = "gemini-3.8-flash",
            ContextSize = 65_536,
            MaximumOutputTokens = 8_192,
            Temperature = 0.1,
            RequestTimeout = TimeSpan.FromMinutes(5)
        });
}
catch (Exception ex)
{
    failure = ex;
}
finally
{
    watch.Stop();
    var report = new
    {
        fixture = "synthetic-chinese-10k-live-preflight",
        sourceCharacters = 10_000,
        elapsedMilliseconds = watch.ElapsedMilliseconds,
        requestBudget = MaximumRequests,
        httpRequestsUsed = pool?.RequestsUsed ?? 0,
        requestAttemptsByModel = pool?.RequestAttemptsByModel,
        inputTokens = state?.Metrics.InputTokens ?? 0,
        outputTokensIncludingThinking = state?.Metrics.OutputTokens ?? 0,
        evidenceTotal = state?.Evidence.LongCount(x => x.IsCurrent) ?? 0,
        evidenceAnalyzed = state?.Evidence.LongCount(x => x.IsCurrent && x.AiAnalyzed) ?? 0,
        characters = state?.Characters.Select(x => new
        {
            chinese = x.ChineseName,
            vietnamese = x.VietnameseName,
            aliases = x.Aliases,
            gender = x.Gender.ToString(),
            primaryPronoun = x.PrimaryNarrativePronoun,
            secondaryPronoun = x.SecondaryNarrativePronoun,
            evidenceCount = x.EvidenceIds.Count,
            reason = x.Reason
        }),
        relationships = state?.Relationships.Select(x => new
        {
            speaker = x.SpeakerChineseName,
            listener = x.ListenerChineseName,
            relationship = x.Relationship,
            selfReference = x.SelfReference,
            addresseeReference = x.AddresseeReference,
            reverseSelfReference = x.ReverseSelfReference,
            reverseAddresseeReference = x.ReverseAddresseeReference,
            evidenceCount = x.EvidenceIds.Count,
            reason = x.Reason
        }),
        glossary = state?.Glossary.Select(x => new
        {
            chinese = x.Chinese,
            vietnamese = x.Vietnamese,
            category = x.Category,
            evidenceCount = x.EvidenceIds.Count
        }),
        error = failure?.Message ?? state?.LastError ?? ""
    };
    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    await File.WriteAllTextAsync(output, json, new UTF8Encoding(false));
    Console.WriteLine(json);
    pool?.Dispose();

    string safeRoot = Path.GetFullPath(root);
    if (safeRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) &&
        Path.GetFileName(safeRoot).StartsWith("qtaie-gemini-live-preflight-", StringComparison.Ordinal) &&
        Directory.Exists(safeRoot))
        Directory.Delete(safeRoot, true);
}

if (failure is not null) Environment.ExitCode = 1;

static List<CorpusChapterIndex> BuildCorpus()
{
    string[] headers =
    [
        "林朝对苏雨说道：“妹妹，今晚回青云宗。”苏雨答道：“哥哥，我听你的。”林朝是苏雨的哥哥，苏雨是林朝的妹妹。",
        "王秀兰对林哲说道：“儿子，路上小心。”林哲答道：“妈，您放心。”王秀兰是林哲的母亲，林哲是她的儿子。",
        "安瑶对赵明说道：“师弟，先练青云剑诀。”赵明答道：“师姐，请指教。”安瑶是赵明的师姐，赵明是安瑶的师弟。",
        "林建国对林悦说道：“女儿，早点回来。”林悦答道：“爸，我知道了。”林建国是林悦的父亲，林悦是他的女儿。",
        "陈雪对周宁说道：“姐姐会保护你。”周宁答道：“妹妹谢谢姐姐。”陈雪是周宁的姐姐，周宁是陈雪的妹妹。"
    ];
    const string filler = "夜风吹过山谷，月光落在青石路上，众人安静地继续前行。";
    var corpus = new List<CorpusChapterIndex>(headers.Length);
    for (int index = 0; index < headers.Length; index++)
    {
        var builder = new StringBuilder(2_100).Append(headers[index]);
        while (builder.Length < 2_000) builder.Append(filler);
        string source = builder.ToString(0, 2_000);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        byte[] guidBytes = new byte[16];
        BitConverter.GetBytes(index + 1).CopyTo(guidBytes, 0);
        guidBytes[15] = 0x77;
        Guid chapterId = new(guidBytes);
        corpus.Add(new CorpusChapterIndex
        {
            ChapterId = chapterId,
            ChapterNumber = index + 1,
            SourceSha256 = hash,
            SourceLengthUtf16 = source.Length,
            Evidence = ProjectCorpusIndexer.ScanChapter(chapterId, source, hash, [], 0.15)
        });
    }
    return corpus;
}
