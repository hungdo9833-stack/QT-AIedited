using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Segmentation;
using QuickTranslator.AiEditor.Core.Translation;

int chapterCount = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 50;
int targetChars = args.Length > 1 && int.TryParse(args[1], out int chars) ? chars : 4000;
string? output = args.Length > 2 ? args[2] : null;
int simulatedLatencyMilliseconds = args.Length > 3 && int.TryParse(args[3], out int latency) ? latency : 5;
string[] names = ["林朝", "苏雨", "王秀兰", "林哲", "安瑶", "林悦", "林建国", "赵明", "陈雪", "周宁"];
var truth = new HashSet<string>(StringComparer.Ordinal);
long memoryBefore = GC.GetTotalMemory(true);
var indexWatch = Stopwatch.StartNew();
List<CorpusChapterIndex> corpus = BuildCorpus(chapterCount, targetChars, names, truth);
indexWatch.Stop();
long memoryAfter = GC.GetTotalMemory(true);
CorpusEvidence[] all = corpus.SelectMany(chapter => chapter.Evidence).ToArray();
HashSet<string> detected = all.SelectMany(x => x.CandidateNames.Concat(x.Signals))
    .Where(truth.Contains).ToHashSet(StringComparer.Ordinal);
var expectedQuality = new IntelligenceQualitySet(truth, ["小朝→林朝"], ["林朝→苏雨:FullSibling"], ["青云宗"], 1, 1);
var actualQuality = new IntelligenceQualitySet(detected, [], [], [], 1, 1);
IntelligenceQualityReport quality = IntelligenceQualityEvaluator.Evaluate(expectedQuality, actualQuality);
string root = Path.Combine(Path.GetTempPath(), "qtaie-book-survey-benchmark-" + Guid.NewGuid().ToString("N"));

try
{
    PipelineResult sequential = await RunPipelineAsync(Path.Combine(root, "sequential"), corpus,
        concurrency: 1, simulatedLatencyMilliseconds, truth);
    PipelineResult parallel = await RunPipelineAsync(Path.Combine(root, "parallel"), corpus,
        concurrency: 6, simulatedLatencyMilliseconds, truth);
    double speedup = parallel.WallMilliseconds <= 0 ? 0 :
        (double)sequential.WallMilliseconds / parallel.WallMilliseconds;
    var report = new
    {
        fixture = "synthetic-200k-mocked-gemini-survey",
        chapters = chapterCount,
        sourceCharacters = (long)chapterCount * targetChars,
        corpusEvidence = all.Length,
        indexingMilliseconds = indexWatch.ElapsedMilliseconds,
        simulatedRequestLatencyMilliseconds = simulatedLatencyMilliseconds,
        sequential,
        parallel,
        comparison = new
        {
            speedup,
            requestDelta = parallel.Requests - sequential.Requests,
            inputTokenDelta = parallel.InputTokens - sequential.InputTokens,
            outputTokenDelta = parallel.OutputTokens - sequential.OutputTokens,
            bothComplete = sequential.CompletionRatio == 1 && parallel.CompletionRatio == 1
        },
        deterministicScannerQuality = new
        {
            characterPrecision = quality.Characters.Precision,
            characterRecall = quality.Characters.Recall,
            falsePositiveRate = quality.Characters.FalsePositiveRate,
            falseNegativeRate = quality.Characters.FalseNegativeRate,
            evidenceValidationAccuracy = quality.EvidenceValidationAccuracy
        },
        peakManagedMemoryApproxBytes = Math.Max(0, memoryAfter - memoryBefore),
        note = "Deterministic benchmark: real indexer/analyzer/checkpoint pipeline, mocked model responses and latency; no API key or network."
    };
    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    if (output is null) Console.WriteLine(json); else File.WriteAllText(output, json, new UTF8Encoding(false));
}
finally
{
    string safeRoot = Path.GetFullPath(root);
    if (!safeRoot.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
        !Path.GetFileName(safeRoot).StartsWith("qtaie-book-survey-benchmark-", StringComparison.Ordinal))
        throw new InvalidOperationException("Invalid benchmark temp root.");
    if (Directory.Exists(safeRoot)) Directory.Delete(safeRoot, true);
}

static List<CorpusChapterIndex> BuildCorpus(int chapterCount, int targetChars, string[] names,
    HashSet<string> truth)
{
    var corpus = new List<CorpusChapterIndex>(chapterCount);
    for (int i = 0; i < chapterCount; i++)
    {
        string name = names[i % names.Length]; truth.Add(name);
        string header = $"{name}说道：今晚出发。{name}看向众人。他的父亲曾是青云宗长老。\n";
        string filler = "风吹过原野，月光落在地面上，众人继续前行。";
        var builder = new StringBuilder(targetChars + 100).Append(header);
        while (builder.Length < targetChars) builder.Append(filler);
        string source = builder.ToString(0, targetChars);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
        Guid chapter = DeterministicGuid(i);
        List<CorpusEvidence> evidence = ProjectCorpusIndexer.ScanChapter(chapter, source, hash, [], 0.15);
        corpus.Add(new CorpusChapterIndex
        {
            ChapterId = chapter,
            ChapterNumber = i + 1,
            SourceSha256 = hash,
            SourceLengthUtf16 = source.Length,
            Evidence = evidence
        });
    }
    return corpus;
}

static async Task<PipelineResult> RunPipelineAsync(string projectRoot, List<CorpusChapterIndex> corpus,
    int concurrency, int latencyMilliseconds, IReadOnlySet<string> expectedCharacters)
{
    var repository = new ProjectRepository();
    ProjectSession session = await repository.CreateAsync(projectRoot, $"Benchmark c{concurrency}");
    var store = new BookIntelligenceStore(session);
    BookIntelligenceState state = await store.LoadAsync();
    state.ScanMode = IntelligenceScanMode.FullSemantic;
    state.AnalysisConcurrency = concurrency;
    state.Chapters = CloneCorpus(corpus);
    foreach (CorpusChapterIndex index in state.Chapters)
    {
        var document = TranslationService.CreateChapter($"Chương {index.ChapterNumber}",
            string.Concat(index.Evidence.OrderBy(x => x.StartUtf16).Select(x => x.Excerpt)));
        document.Id = index.ChapterId;
        document.Segments = new TextSegmenter().Segment(document.OriginalText, document.Id, document.SegmentationOptions).ToList();
        document.SelectedSegmentId = document.Segments[0].Id;
        await repository.AddChapterAsync(session, document, index.ChapterNumber, "Synthetic");
    }
    state = await store.SaveAsync(state);
    ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
    var client = new BenchmarkSurveyClient(latencyMilliseconds, expectedCharacters);
    var watch = Stopwatch.StartNew();
    state = await new AdaptiveBookIntelligenceAnalyzer(client).AnalyzeAsync(session, state, store, resources,
        new ModelOptions { Model = "mock-gemini-survey", ContextSize = 32_768,
            MaximumOutputTokens = 4096, RequestTimeout = TimeSpan.FromMinutes(5) });
    watch.Stop();
    long total = state.Evidence.LongCount(evidence => evidence.IsCurrent);
    long completed = state.Evidence.LongCount(evidence => evidence.IsCurrent && evidence.AiAnalyzed);
    HashSet<string> extractedCharacters = state.Characters.Select(x => x.ChineseName)
        .ToHashSet(StringComparer.Ordinal);
    HashSet<string> extractedGlossary = state.Glossary.Select(x => x.Chinese)
        .ToHashSet(StringComparer.Ordinal);
    (double characterPrecision, double characterRecall) = PrecisionRecall(expectedCharacters, extractedCharacters);
    (double glossaryPrecision, double glossaryRecall) = PrecisionRecall(
        new HashSet<string>(["青云宗"], StringComparer.Ordinal), extractedGlossary);
    return new PipelineResult(concurrency, watch.ElapsedMilliseconds, state.Metrics.Requests,
        state.Metrics.InputTokens, state.Metrics.OutputTokens, completed, total,
        total == 0 ? 1 : (double)completed / total, client.MaximumConcurrency, state.Revision,
        extractedCharacters.Count, expectedCharacters.Count, characterPrecision, characterRecall,
        extractedGlossary.Count, glossaryPrecision, glossaryRecall);
}

static (double Precision, double Recall) PrecisionRecall(IReadOnlySet<string> expected, IReadOnlySet<string> actual)
{
    int truePositive = actual.Count(expected.Contains);
    return (actual.Count == 0 ? (expected.Count == 0 ? 1 : 0) : (double)truePositive / actual.Count,
        expected.Count == 0 ? 1 : (double)truePositive / expected.Count);
}

static List<CorpusChapterIndex> CloneCorpus(List<CorpusChapterIndex> corpus) =>
    JsonSerializer.Deserialize<List<CorpusChapterIndex>>(
        JsonSerializer.Serialize(corpus, DocumentJson.Options), DocumentJson.Options)!;

static Guid DeterministicGuid(int value)
{
    byte[] bytes = new byte[16]; BitConverter.GetBytes(value).CopyTo(bytes, 0); bytes[15] = 0x42; return new Guid(bytes);
}

internal sealed record PipelineResult(int Concurrency, long WallMilliseconds, long Requests,
    long InputTokens, long OutputTokens, long CompletedEvidence, long TotalEvidence,
    double CompletionRatio, int MaximumObservedConcurrency, long FinalStateRevision,
    int ExtractedCharacters, int ExpectedCharacters, double CharacterPrecision, double CharacterRecall,
    int ExtractedGlossary, double GlossaryPrecision, double GlossaryRecall);

internal sealed class BenchmarkSurveyClient(int latencyMilliseconds, IReadOnlySet<string> knownNames)
    : IOllamaClient, IAdaptiveSurveyBatchClient
{
    private int _active;
    private int _maximum;
    public string CacheSignature => "mock-gemini-adaptive-v1";
    public int TargetInputTokens => 40_000;
    public int MaximumConcurrency => Volatile.Read(ref _maximum);

    public async Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        int active = Interlocked.Increment(ref _active);
        int observed;
        do { observed = _maximum; }
        while (active > observed && Interlocked.CompareExchange(ref _maximum, active, observed) != observed);
        var watch = Stopwatch.StartNew();
        try
        {
            if (latencyMilliseconds > 0) await Task.Delay(latencyMilliseconds, cancellationToken);
            var characters = new List<object>();
            var glossary = new List<object>();
            foreach (Match match in Regex.Matches(prompt,
                @"EVIDENCE ([0-9a-f-]{36})[^\r\n]*\r?\n(.*?)(?=\r?\n\r?\nEVIDENCE |\z)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                string evidenceId = match.Groups[1].Value;
                string excerpt = match.Groups[2].Value;
                foreach (string name in knownNames.Where(name => excerpt.Contains(name, StringComparison.Ordinal)))
                    characters.Add(new
                    {
                        chinese_name = name, vietnamese_name = name, aliases = Array.Empty<string>(),
                        gender = "unknown", primary_pronoun = "", secondary_pronoun = "",
                        confidence = "proposed", evidence_ids = new[] { evidenceId }, reason = "benchmark truth"
                    });
                if (excerpt.Contains("青云宗", StringComparison.Ordinal))
                    glossary.Add(new
                    {
                        chinese = "青云宗", vietnamese = "Thanh Vân Tông", category = "organization",
                        context_dependent = false, confidence = "proposed",
                        evidence_ids = new[] { evidenceId }, reason = "benchmark truth"
                    });
            }
            string response = JsonSerializer.Serialize(new
            {
                characters,
                relationships = Array.Empty<object>(),
                glossary
            });
            return new OllamaCompletion(response, true, "stop",
                new GenerationMetrics
                {
                    InputTokens = Math.Max(1, (Encoding.UTF8.GetByteCount(prompt) + 2) / 3),
                    OutputTokens = Math.Max(12, (Encoding.UTF8.GetByteCount(response) + 2) / 3),
                    ClientElapsed = watch.Elapsed
                });
        }
        finally { Interlocked.Decrement(ref _active); }
    }
}
