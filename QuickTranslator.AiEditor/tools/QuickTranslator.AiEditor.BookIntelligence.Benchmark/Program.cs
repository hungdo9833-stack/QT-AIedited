using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Persistence;

int chapterCount = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 500;
int targetChars = args.Length > 1 && int.TryParse(args[1], out int chars) ? chars : 4000;
string? output = args.Length > 2 ? args[2] : null;
string[] names = ["林朝", "苏雨", "王秀兰", "林哲", "安瑶", "林悦", "林建国", "赵明", "陈雪", "周宁"];
var state = new BookIntelligenceState { ProjectId = Guid.NewGuid() };
var truth = new HashSet<string>(StringComparer.Ordinal);
long memoryBefore = GC.GetTotalMemory(true);
var watch = Stopwatch.StartNew();
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
    state.Chapters.Add(new CorpusChapterIndex { ChapterId = chapter, ChapterNumber = i + 1,
        SourceSha256 = hash, SourceLengthUtf16 = source.Length, Evidence = evidence });
}
watch.Stop();
long memoryAfter = GC.GetTotalMemory(true);
CorpusEvidence[] all = state.Evidence.ToArray();
CorpusEvidence[] fast = all.Where(x => x.Priority is EvidencePriority.High or EvidencePriority.Medium || x.IsAuditSample).ToArray();
HashSet<string> detected = all.SelectMany(x => x.CandidateNames.Concat(x.Signals))
    .Where(truth.Contains).ToHashSet(StringComparer.Ordinal);
var expectedQuality = new IntelligenceQualitySet(truth, ["小朝→林朝"], ["林朝→苏雨:FullSibling"], ["青云宗"], 1, 1);
var actualQuality = new IntelligenceQualitySet(detected, [], [], [], 1, 1);
IntelligenceQualityReport quality = IntelligenceQualityEvaluator.Evaluate(expectedQuality, actualQuality);
byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(state, DocumentJson.Options);
var report = new
{
    fixture = "synthetic-cpu-planner-no-real-model",
    chapters = chapterCount,
    sourceCharacters = (long)chapterCount * targetChars,
    cpuMilliseconds = watch.ElapsedMilliseconds,
    fullSemantic = new { evidence = all.Length, requestsAtEightEvidence = (all.Length + 7) / 8,
        estimatedInputTokens = all.Sum(x => x.Excerpt.Length) / 2, aiMilliseconds = 0, outputTokens = 0 },
    fastAdaptive = new { evidence = fast.Length, skippedPendingAudit = all.Length - fast.Length,
        auditSamples = fast.Count(x => x.Priority == EvidencePriority.Low && x.IsAuditSample),
        requestsAtEightEvidence = (fast.Length + 7) / 8, estimatedInputTokens = fast.Sum(x => x.Excerpt.Length) / 2,
        aiMilliseconds = 0, outputTokens = 0 },
    quality = new { characterPrecision = quality.Characters.Precision, characterRecall = quality.Characters.Recall,
        aliasPrecision = quality.Aliases.Precision, aliasRecall = quality.Aliases.Recall,
        relationshipPrecision = quality.Relationships.Precision, relationshipRecall = quality.Relationships.Recall,
        glossaryPrecision = quality.Glossary.Precision, glossaryRecall = quality.Glossary.Recall,
        falsePositiveRate = quality.Characters.FalsePositiveRate, falseNegativeRate = quality.Characters.FalseNegativeRate,
        evidenceValidationAccuracy = quality.EvidenceValidationAccuracy },
    peakManagedMemoryApproxBytes = Math.Max(0, memoryAfter - memoryBefore),
    knowledgeBaseJsonBytes = serialized.Length,
    note = "AI metrics are zero because this deterministic benchmark never calls Ollama. Estimated tokens use two UTF-16 Chinese characters per token and are not model measurements."
};
string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (output is null) Console.WriteLine(json); else File.WriteAllText(output, json, new UTF8Encoding(false));

static Guid DeterministicGuid(int value)
{
    byte[] bytes = new byte[16]; BitConverter.GetBytes(value).CopyTo(bytes, 0); bytes[15] = 0x42; return new Guid(bytes);
}
