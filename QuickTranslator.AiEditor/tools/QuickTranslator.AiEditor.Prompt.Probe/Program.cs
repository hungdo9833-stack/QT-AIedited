using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

string? projectPath = null;
string? outputPath = null;
int? requestedChapter = null;
int maximumSegments = 30;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" when i + 1 < args.Length:
            projectPath = args[++i];
            break;
        case "--out" when i + 1 < args.Length:
            outputPath = args[++i];
            break;
        case "--chapter" when i + 1 < args.Length && int.TryParse(args[++i], out int chapter) && chapter > 0:
            requestedChapter = chapter;
            break;
        case "--max-segments" when i + 1 < args.Length && int.TryParse(args[++i], out int maximum) && maximum > 0:
            maximumSegments = maximum;
            break;
        default:
            return BadArguments();
    }
}

if (projectPath is null || outputPath is null ||
    !projectPath.EndsWith(".qtproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(projectPath))
    return BadArguments("--project must name an existing .qtproj file, and --out is required.");

string sourceProjectDirectory;
string outputDirectory;
try
{
    projectPath = Path.GetFullPath(projectPath);
    sourceProjectDirectory = Path.GetDirectoryName(projectPath)!;
    outputDirectory = Path.GetFullPath(outputPath);
}
catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
{
    return BadArguments("A project or output path is invalid.");
}

if (IsWithin(outputDirectory, sourceProjectDirectory))
    return BadArguments("--out must be outside the source project folder so the project remains read-only.");

string copiedProjectDirectory = Path.Combine(Path.GetTempPath(), "qtstudio-prompt-probe-" + Guid.NewGuid().ToString("N"));
try
{
    CopyDirectory(sourceProjectDirectory, copiedProjectDirectory);
    string copiedManifest = Path.Combine(copiedProjectDirectory, Path.GetFileName(projectPath));
    var repository = new ProjectRepository();
    ProjectSession session = (await repository.OpenAsync(copiedManifest)).Session;
    ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
    ProjectTranslationSettings settings = await new ProjectSettingsStore(session).LoadTranslationAsync();

    var loadedChapters = new List<(ProjectChapterEntry Entry, ChapterDocument Chapter)>();
    foreach (ProjectChapterEntry entry in session.Manifest.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number))
    {
        ChapterDocument chapter = (await repository.LoadChapterAsync(session, entry.ChapterId)).Chapter;
        loadedChapters.Add((entry, chapter));
    }

    if (Environment.GetEnvironmentVariable("QT_PROBE_DIAG") == "1")
    {
        // Whole-book speaker statistics only; never prints body text.
        var profiles = resources.Characters.Profiles;
        Console.WriteLine("genders: " + string.Join(", ", profiles.GroupBy(p => p.Gender).Select(g => $"{g.Key}={g.Count()}")));
        var owners = profiles.SelectMany(p => new[] { p.ChineseName }.Concat(p.Aliases)
                .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => (Name: n.Trim(), Profile: p)))
            .GroupBy(x => x.Name, StringComparer.Ordinal).Where(g => g.Select(x => x.Profile.Id).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Profile, StringComparer.Ordinal);
        var sources = loadedChapters.Where(x => !string.IsNullOrEmpty(x.Chapter.OriginalText))
            .Select(x => new QuickTranslator.AiEditor.Core.BookIntelligence.CensusSource(x.Chapter.Id, x.Entry.Number, x.Chapter.OriginalText)).ToArray();
        var scenes = QuickTranslator.AiEditor.Core.Addressing.AddressingDialogueSurvey.Scenes(sources, owners.Keys.ToArray(), int.MaxValue, int.MaxValue);
        var turns = DialogueTurnInference.Infer(scenes, owners);
        var quotes = scenes.SelectMany(s => s.Quotes).ToArray();
        Console.WriteLine($"names={owners.Count} scenes={scenes.Count} multiQuoteScenes={scenes.Count(s => s.Quotes.Count > 1)} quotes={quotes.Length}");
        Console.WriteLine("evidence: " + string.Join(", ", quotes.GroupBy(q => q.Line.SpeakerEvidence).Select(g => $"{g.Key}={g.Count()}")));
        bool Strong(QuickTranslator.AiEditor.Core.Addressing.SceneQuote q) => q.Line.Speaker is not null && owners.ContainsKey(q.Line.Speaker) &&
            q.Line.SpeakerEvidence is QuickTranslator.AiEditor.Core.Addressing.SpeakerEvidence.Explicit or QuickTranslator.AiEditor.Core.Addressing.SpeakerEvidence.PreviousNarration;
        Console.WriteLine($"strongKnown={quotes.Count(Strong)} scenesWith2StrongSpeakers={scenes.Count(s => s.Quotes.Where(Strong).Select(q => owners[q.Line.Speaker!].Id).Distinct().Count() >= 2)}");
        // Adjacent strong pairs A,B with distinct speakers immediately followed by a weak quote: the seed inference needs.
        int seeds = 0, beforeLong = 0, beforeMultiPara = 0;
        foreach (var s in scenes)
            for (int i = 2; i < s.Quotes.Count; i++)
            {
                var (a, b, c) = (s.Quotes[i - 2], s.Quotes[i - 1], s.Quotes[i]);
                if (!Strong(a) || !Strong(b) || Strong(c) || owners[a.Line.Speaker!].Id == owners[b.Line.Speaker!].Id) continue;
                seeds++;
                if (c.Before.Trim().Length > 60) beforeLong++;
                if (c.Before.Split('\n').Count(p => !string.IsNullOrWhiteSpace(p)) > 1) beforeMultiPara++;
            }
        Console.WriteLine($"seeds(A,B strong then weak)={seeds} seedBeforeLong={beforeLong} seedBeforeMultiPara={beforeMultiPara}");
        Console.WriteLine($"inferred={turns.Count(t => t.IsInferred)} attributedTotal={turns.Count(t => t.Speaker is not null)}");
        int[] gaps = quotes.Skip(1).Select(q => q.Before.Trim().Length).OrderBy(x => x).ToArray();
        if (gaps.Length > 0) Console.WriteLine($"gapChars p50={gaps[gaps.Length / 2]} p90={gaps[gaps.Length * 9 / 10]}");
        return 0;
    }

    (ProjectChapterEntry Entry, ChapterDocument Chapter) selected;
    if (requestedChapter is int number)
    {
        var matches = loadedChapters.Where(x => x.Entry.Number == number).ToArray();
        if (matches.Length == 0) return BadArguments($"Chapter {number} is not active in the project.");
        selected = matches[0];
    }
    else
    {
        var candidate = loadedChapters.Where(x => x.Chapter.Segments.Count > 0)
            .Select(x => new
            {
                Item = x,
                QuoteSegments = x.Chapter.Segments.Count(s => OpensWithQuote(s.SourceText)),
                QuoteCharacters = x.Chapter.Segments.Sum(s => CountQuoteCharacters(s.SourceText))
            })
            .OrderByDescending(x => x.QuoteSegments)
            .ThenByDescending(x => x.QuoteCharacters)
            .ThenBy(x => x.Item.Entry.Number)
            .FirstOrDefault();
        if (candidate is null)
        {
            Console.Error.WriteLine("ERROR: the project has no active chapter containing segments.");
            return 1;
        }
        selected = candidate.Item;
    }

    ChapterDocument baseline = selected.Chapter;
    TranslationSegment[] orderedSegments = baseline.Segments.OrderBy(x => x.Order).ToArray();
    TranslationSegment[] allQuoteSegments = orderedSegments.Where(x => OpensWithQuote(x.SourceText)).ToArray();
    TranslationSegment[] targets = allQuoteSegments.Take(maximumSegments).ToArray();
    var targetIds = targets.Select(x => x.Id).ToHashSet();
    int lastTargetOrder = targets.LastOrDefault()?.Order ?? int.MinValue;

    BookKnowledgeContextProvider? knowledge = null;
    if (settings.BookKnowledgeContextEnabled)
        knowledge = new BookKnowledgeContextProvider(await new BookIntelligenceStore(session).LoadAsync(), resources);

    var client = new RecordingClient();
    var service = new TranslationService(baseline, client, new NoSaveChapterStore());
    Configure(service, resources, knowledge);
    var segmentSummaries = new List<SegmentSummary>();
    int processed = 0;
    string promptsDirectory = Path.Combine(outputDirectory, "prompts");
    Directory.CreateDirectory(promptsDirectory);

    foreach (TranslationSegment originalSegment in orderedSegments)
    {
        if (originalSegment.Order > lastTargetOrder) break;
        ChapterDocument before = service.GetSnapshot();
        TranslationSegment current = before.Segments.Single(x => x.Id == originalSegment.Id);
        bool isTarget = targetIds.Contains(current.Id);
        string prompt;

        if (isTarget && !string.IsNullOrWhiteSpace(current.EditableText))
        {
            var forkClient = new RecordingClient();
            var fork = new TranslationService(before, forkClient, new NoSaveChapterStore());
            Configure(fork, resources, knowledge);
            await fork.TranslateAsync(current.Id, settings.Model);
            prompt = forkClient.Prompts.Single();
        }
        else if (string.IsNullOrWhiteSpace(current.EditableText) || isTarget)
        {
            int priorCalls = client.Prompts.Count;
            await service.TranslateAsync(current.Id, settings.Model);
            prompt = client.Prompts[priorCalls];
        }
        else
        {
            processed++;
            continue;
        }

        processed++;
        if (!isTarget) continue;

        int index = Array.FindIndex(orderedSegments, x => x.Id == current.Id);
        string previousEditableText = index > 0
            ? before.Segments.Single(x => x.Id == orderedSegments[index - 1].Id).EditableText
            : "";
        PreviousExcerpt excerpt = InspectPreviousExcerpt(prompt, previousEditableText);
        string promptFile = $"seg-{current.Order}.txt";
        segmentSummaries.Add(new SegmentSummary(
            current.Order,
            promptFile,
            EstimateTokens(prompt),
            prompt.Length,
            CountDialogueMapLines(prompt),
            CountLinesContaining(prompt, "(inferred)"),
            CountLinesContaining(prompt, "(likely)"),
            CountLinesContaining(prompt, "(listener unclear)"),
            prompt.Contains("Preceding source", StringComparison.Ordinal),
            prompt.Contains("opens mid-conversation", StringComparison.Ordinal),
            excerpt.Length,
            excerpt.Position));

        await File.WriteAllTextAsync(Path.Combine(promptsDirectory, promptFile), prompt, new UTF8Encoding(false));
    }

    Directory.CreateDirectory(outputDirectory);
    var summary = new
    {
        projectName = session.Manifest.VietnameseTitle,
        chapterNumber = selected.Entry.Number,
        chapterTitle = baseline.Title,
        profileCount = resources.Characters.Profiles.Count,
        quoteOpeningSegmentCount = allQuoteSegments.Length,
        capturedSegmentCount = segmentSummaries.Count,
        segments = segmentSummaries
    };
    var jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    await File.WriteAllTextAsync(Path.Combine(outputDirectory, "summary.json"),
        JsonSerializer.Serialize(summary, jsonOptions), new UTF8Encoding(false));

    Console.WriteLine($"OK chapter={selected.Entry.Number} chapters={loadedChapters.Count} profiles={resources.Characters.Profiles.Count} " +
        $"quoteOpening={allQuoteSegments.Length} captured={segmentSummaries.Count} processed={processed} " +
        $"promptChars={segmentSummaries.Sum(x => x.PromptLengthChars)} dialogueMapLines={segmentSummaries.Sum(x => x.DialogueMapLines)}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: prompt probe failed ({ex.GetType().Name}).");
    return 1;
}
finally
{
    if (Directory.Exists(copiedProjectDirectory)) Directory.Delete(copiedProjectDirectory, recursive: true);
}

static int BadArguments(string? message = null)
{
    if (message is not null) Console.Error.WriteLine("ERROR: " + message);
    Console.Error.WriteLine("Usage: --project <path\\Name.qtproj> --out <folder> [--chapter <number>] [--max-segments 30]");
    return 2;
}

static void Configure(TranslationService service, ProjectResources resources, BookKnowledgeContextProvider? knowledge)
{
    service.UseProjectResources(resources);
    if (knowledge is not null) service.UseBookKnowledgeContext(knowledge);
}

static bool OpensWithQuote(string text)
{
    ReadOnlySpan<char> source = text.AsSpan().TrimStart();
    return source.Length > 0 && IsOpeningQuote(source[0]);
}

static int CountQuoteCharacters(string text) => text.Count(value => IsOpeningQuote(value) ||
    value is '”' or '’' or '」' or '』' or '»' or '›');

static bool IsOpeningQuote(char value) =>
    value is '“' or '‘' or '「' or '『' or '"' or '\'' or '«' or '‹' or '„' or '‚' or '＂' or '＇';

static int EstimateTokens(string prompt) => Math.Max(1, (Encoding.UTF8.GetByteCount(prompt) + 2) / 3);

static int CountDialogueMapLines(string prompt)
{
    bool inMap = false;
    int count = 0;
    foreach (string line in prompt.ReplaceLineEndings("\n").Split('\n'))
    {
        if (line.StartsWith("DIALOGUE MAP", StringComparison.Ordinal))
        {
            inMap = true;
            continue;
        }
        if (!inMap) continue;
        if (line.StartsWith("Translate each listed quote", StringComparison.Ordinal)) break;
        if (line.StartsWith("- ", StringComparison.Ordinal)) count++;
    }
    return count;
}

static int CountLinesContaining(string prompt, string marker) =>
    prompt.ReplaceLineEndings("\n").Split('\n').Count(line => line.Contains(marker, StringComparison.Ordinal));

static PreviousExcerpt InspectPreviousExcerpt(string prompt, string previousText)
{
    const string suffix = " excerpt (reference only; never overwrite user text): ";
    int previous = prompt.IndexOf("Previous ", StringComparison.Ordinal);
    if (previous < 0) return new(0, "none");
    int suffixAt = prompt.IndexOf(suffix, previous, StringComparison.Ordinal);
    if (suffixAt < 0) return new(0, "unknown");
    int start = suffixAt + suffix.Length;
    if (previousText.Length == 0 || start >= prompt.Length) return new(0, "unknown");

    string trimmedPrevious = previousText.TrimEnd();
    int bestLength = 0;
    string bestPosition = "unknown";
    int maximum = Math.Min(prompt.Length - start, previousText.Length + 2);
    for (int length = 1; length <= maximum; length++)
    {
        string raw = prompt.Substring(start, length);
        string value = raw.StartsWith("… ", StringComparison.Ordinal) ? raw[2..] : raw;
        bool head = previousText.StartsWith(value, StringComparison.Ordinal);
        bool tail = trimmedPrevious.EndsWith(value, StringComparison.Ordinal);
        if (!head && !tail) continue;
        bestLength = raw.Length;
        bestPosition = head && tail && (value.Length == previousText.Length || value.Length == trimmedPrevious.Length)
            ? "whole" : raw.StartsWith("… ", StringComparison.Ordinal) || tail && !head ? "tail" : "head";
    }
    return new(bestLength, bestPosition);
}

static bool IsWithin(string candidate, string directory)
{
    string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
    return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
        string target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

readonly record struct PreviousExcerpt(int Length, string Position);

sealed record SegmentSummary(
    int Order,
    string PromptFile,
    int PromptTokenEstimate,
    int PromptLengthChars,
    int DialogueMapLines,
    int InferredLines,
    int LikelyLines,
    int ListenerUnclearLines,
    bool ContainsPrecedingSource,
    bool ContainsOpensMidConversation,
    int PreviousExcerptLengthChars,
    string PreviousExcerptPosition);

sealed class RecordingClient : ILlmClient
{
    public List<string> Prompts { get; } = [];

    public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options,
        CancellationToken cancellationToken = default)
    {
        Prompts.Add(prompt);
        return Task.FromResult(new LlmCompletion("Đây là một câu tiếng Việt dùng cho phép thử ngoại tuyến.",
            true, "stop", new GenerationMetrics()));
    }
}

sealed class NoSaveChapterStore : IChapterStore
{
    public string GetPath(Guid chapterId) => Path.Combine(Path.GetTempPath(), chapterId + ".probe");
    public Task SaveAsync(ChapterDocument chapter, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<ChapterLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The prompt probe never loads through its no-save chapter store.");
}
