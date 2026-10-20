using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Importing;

Console.OutputEncoding = new UTF8Encoding(false);
Console.InputEncoding = Encoding.UTF8;

if (!TryParseArguments(args, out string[] paths, out bool showRegions, out string? jsonPath))
{
    PrintUsage();
    return 0;
}

var reports = new List<FileReport>();
var failures = new List<FileFailure>();
foreach (string path in paths)
{
    try
    {
        FileReport report = await AnalyzeAsync(path, showRegions);
        reports.Add(report);
        PrintReport(report, showRegions);
        // QT_PROBE_IMPORT_ROOT=<folder>: also run the real import into a new, throw-away project under that folder.
        if (Environment.GetEnvironmentVariable("QT_PROBE_IMPORT_ROOT") is { Length: > 0 } importRoot)
            await ImportIntoNewProjectAsync(path, importRoot);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                               ArgumentException or NotSupportedException)
    {
        string displayPath = SafeFullPath(path);
        failures.Add(new(displayPath, ex.GetType().Name, ex.Message));
        Console.Error.WriteLine($"{displayPath}: {ex.GetType().Name}: {ex.Message}");
    }
}

if (jsonPath is not null)
{
    var result = new ProbeReport(reports, failures);
    var options = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };
    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(result, options), new UTF8Encoding(false));
}

return failures.Count == 0 ? 0 : 2;

// Real end-to-end import with the preview's default decisions, into a new project in its own folder (never an existing one).
static async Task ImportIntoNewProjectAsync(string path, string root)
{
    RawSourceSnapshot source = await RawSourceReader.ReadFileAsync(Path.GetFullPath(path), SourceTextEncoding.Auto);
    ImportPreviewPlan plan = ImportPreviewEditor.Create(new ChapterDetector().Detect(source)).Current;
    string folder = Path.Combine(Path.GetFullPath(root), "probe-" + Guid.NewGuid().ToString("N")[..8]);
    var repository = new QuickTranslator.AiEditor.Core.Projects.ProjectRepository();
    var session = await repository.CreateAsync(folder, "Import probe");
    var defaults = new BulkImportChapterDefaults(new QuickTranslator.AiEditor.Core.Models.SegmenterOptions(),
        new QuickTranslator.AiEditor.Core.Models.ModelOptions(), QuickTranslator.AiEditor.Core.Models.PromptPreset.Default);
    BulkImportReport report = await new SafeBulkImportService(repository).ImportAsync(session, plan, defaults);
    Console.WriteLine($"  Real import: completed={report.Completed}; imported={report.ImportedCount:N0}; " +
        $"chapters in project={session.Manifest.Chapters.Count:N0}; error={report.Error ?? "none"}; project={folder}");
}

static async Task<FileReport> AnalyzeAsync(string path, bool includeRegionText)
{
    string fullPath = Path.GetFullPath(path);
    RawSourceSnapshot source = await RawSourceReader.ReadFileAsync(fullPath, SourceTextEncoding.Auto);
    ImmutableImportPlan detected = new ChapterDetector().Detect(source);
    ImportPreviewEditor editor = ImportPreviewEditor.Create(detected);
    ImportPreviewPlan plan = editor.Current;

    ImportPreviewItem[] chapters = plan.Chapters.ToArray();
    int[] numbers = chapters.Where(x => x.Number is > 0).Select(x => x.Number!.Value)
        .Distinct().OrderBy(x => x).ToArray();
    string[] gaps = numbers.Zip(numbers.Skip(1), (left, right) => (left, right))
        .Where(pair => pair.right > pair.left + 1)
        .Select(pair => $"{pair.left}?{pair.right}")
        .ToArray();

    IssueGroup[] issueGroups = plan.Issues
        .GroupBy(x => new { x.Code, x.Severity })
        .OrderByDescending(x => x.Key.Severity)
        .ThenBy(x => x.Key.Code)
        .Select(x => new IssueGroup(x.Key.Code, x.Key.Severity, x.Count()))
        .ToArray();
    // QT_PROBE_WARNINGS=1 also lists warning messages (they name chapter numbers only, never novel text).
    bool withWarnings = Environment.GetEnvironmentVariable("QT_PROBE_WARNINGS") == "1";
    string[] errorMessages = plan.Issues
        .Where(x => x.Severity == DetectionIssueSeverity.Error || withWarnings && x.Severity == DetectionIssueSeverity.Warning)
        .Select(x => x.Message)
        .Take(20)
        .ToArray();
    if (withWarnings)
        errorMessages = errorMessages.Concat(plan.Items.Select((item, index) => (item, index))
            .Where(x => x.item.IsChapter && !x.item.Included)
            .Select(x => $"not imported #{x.item.Number} title=[{x.item.Title}] decision={x.item.Decision} previous=[{(x.index > 0 ? plan.Items[x.index - 1].Title : "")}] #{(x.index > 0 ? plan.Items[x.index - 1].Number : null)}")).ToArray();

    RegionReport[] regions = plan.Items
        .Where(x => x.Kind == PlannedRegionKind.Unclassified)
        .Select(x => new RegionReport(
            x.Span.StartUtf16,
            x.Span.LengthUtf16,
            includeRegionText ? FirstNonBlankLines(plan.Slice(x.Span)) : null))
        .ToArray();

    SourceCoverageSummary coverage = plan.CoverageAudit.Summary;
    var coverageReport = new CoverageReport(
        coverage.TotalUtf16,
        coverage.ImportedContentUtf16,
        coverage.ImportedHeadingUtf16,
        coverage.MetadataUtf16,
        coverage.VerifiedDuplicateUtf16,
        coverage.ExplicitlyExcludedUtf16,
        coverage.UnresolvedUtf16,
        coverage.IsFullyAccounted,
        coverage.IsVerifiedComplete,
        coverage.IsPartialImport);

    return new FileReport(
        fullPath,
        source.Text.Length,
        plan.Lines.Length,
        source.EncodingName,
        new ChapterReport(
            chapters.Length,
            chapters.Count(x => x.Included),
            chapters.Count(x => x.Decision == CandidateDecision.NeedsReview),
            chapters.Count(x => x.Decision == CandidateDecision.Rejected),
            chapters.Count(x => x.Number is not > 0),
            plan.AutomaticallySkippedDuplicateCount),
        numbers.Length == 0 ? null : numbers[0],
        numbers.Length == 0 ? null : numbers[^1],
        gaps,
        plan.CoverageAudit.CanImport && !plan.HasBlockingIssues && plan.ImportableChapters.Any(),
        coverageReport,
        issueGroups,
        errorMessages,
        new RegionSummary(regions.Length, regions.Sum(x => x.Length), regions));
}

static string[] FirstNonBlankLines(string text) => text
    .Split(["\r\n", "\n", "\r"], StringSplitOptions.None)
    .Where(line => !string.IsNullOrWhiteSpace(line))
    .Take(3)
    .Select(line => Truncate(line.Trim(), 60))
    .ToArray();

static string Truncate(string value, int maximumLength) => value.Length <= maximumLength
    ? value
    : value[..(maximumLength - 1)] + "…";

static void PrintReport(FileReport report, bool showRegions)
{
    Console.WriteLine($"File: {report.File}");
    Console.WriteLine($"  Chars: {report.CharactersUtf16:N0}; Lines: {report.Lines:N0}; Encoding: {report.DetectedEncoding}");
    Console.WriteLine($"  Chapters: total {report.Chapters.Total:N0}; included {report.Chapters.Included:N0}; " +
                      $"NeedsReview {report.Chapters.NeedsReview:N0}; Rejected {report.Chapters.Rejected:N0}; " +
                      $"without number {report.Chapters.WithoutNumber:N0}; auto-skipped exact duplicates {report.Chapters.AutoSkippedExactDuplicates:N0}");
    Console.WriteLine($"  Chapter numbers: min {FormatNumber(report.MinimumChapterNumber)}; " +
                      $"max {FormatNumber(report.MaximumChapterNumber)}; gaps {FormatList(report.NumberingGaps)}");
    Console.WriteLine($"  CanImport: {report.CanImport}");
    CoverageReport c = report.Coverage;
    Console.WriteLine($"  Coverage: TotalUtf16={c.TotalUtf16:N0}; ImportedContentUtf16={c.ImportedContentUtf16:N0}; " +
                      $"ImportedHeadingUtf16={c.ImportedHeadingUtf16:N0}; MetadataUtf16={c.MetadataUtf16:N0}; " +
                      $"VerifiedDuplicateUtf16={c.VerifiedDuplicateUtf16:N0}; ExplicitlyExcludedUtf16={c.ExplicitlyExcludedUtf16:N0}; " +
                      $"UnresolvedUtf16={c.UnresolvedUtf16:N0}; IsFullyAccounted={c.IsFullyAccounted}; " +
                      $"IsVerifiedComplete={c.IsVerifiedComplete}; IsPartialImport={c.IsPartialImport}");
    Console.WriteLine("  Issues:");
    if (report.Issues.Length == 0)
        Console.WriteLine("    none");
    else
        foreach (IssueGroup issue in report.Issues)
            Console.WriteLine($"    {issue.Code} / {issue.Severity}: {issue.Count:N0}");
    if (report.ErrorMessages.Length > 0)
    {
        Console.WriteLine($"  Error messages (showing {report.ErrorMessages.Length:N0}, max 20):");
        foreach (string message in report.ErrorMessages)
            Console.WriteLine($"    - {message}");
    }
    Console.WriteLine($"  Free (Unclassified) regions: {report.FreeRegions.Count:N0}; total chars {report.FreeRegions.TotalCharactersUtf16:N0}");
    foreach (RegionReport region in report.FreeRegions.Regions)
    {
        Console.WriteLine($"    offset {region.Offset}, length {region.Length}");
        if (showRegions && region.FirstNonBlankLines is not null)
            foreach (string line in region.FirstNonBlankLines)
                Console.WriteLine($"      {line}");
    }
    Console.WriteLine();
}

static string FormatNumber(int? value) => value?.ToString() ?? "none";
static string FormatList(string[] values) => values.Length == 0 ? "none" : string.Join(", ", values);

static bool TryParseArguments(string[] arguments, out string[] paths, out bool showRegions, out string? jsonPath)
{
    var inputs = new List<string>();
    showRegions = false;
    jsonPath = null;
    for (int index = 0; index < arguments.Length; index++)
    {
        string argument = arguments[index];
        if (argument.Equals("--show-regions", StringComparison.OrdinalIgnoreCase))
        {
            showRegions = true;
        }
        else if (argument.Equals("--json", StringComparison.OrdinalIgnoreCase))
        {
            if (++index >= arguments.Length || jsonPath is not null)
            {
                paths = [];
                return false;
            }
            jsonPath = arguments[index];
        }
        else if (argument.StartsWith("--", StringComparison.Ordinal))
        {
            paths = [];
            return false;
        }
        else
        {
            inputs.Add(argument);
        }
    }
    paths = inputs.ToArray();
    return paths.Length > 0;
}

static void PrintUsage() => Console.Error.WriteLine(
    "Usage: Import.Probe <file.txt> [<file2.txt> ...] [--show-regions] [--json <out.json>]");

static string SafeFullPath(string path)
{
    try { return Path.GetFullPath(path); }
    catch (Exception) { return path; }
}

internal sealed record ProbeReport(IReadOnlyList<FileReport> Files, IReadOnlyList<FileFailure> Failures);
internal sealed record FileFailure(string File, string ErrorType, string Message);
internal sealed record FileReport(
    string File,
    int CharactersUtf16,
    int Lines,
    string DetectedEncoding,
    ChapterReport Chapters,
    int? MinimumChapterNumber,
    int? MaximumChapterNumber,
    string[] NumberingGaps,
    bool CanImport,
    CoverageReport Coverage,
    IssueGroup[] Issues,
    string[] ErrorMessages,
    RegionSummary FreeRegions);
internal sealed record ChapterReport(
    int Total,
    int Included,
    int NeedsReview,
    int Rejected,
    int WithoutNumber,
    int AutoSkippedExactDuplicates);
internal sealed record CoverageReport(
    int TotalUtf16,
    int ImportedContentUtf16,
    int ImportedHeadingUtf16,
    int MetadataUtf16,
    int VerifiedDuplicateUtf16,
    int ExplicitlyExcludedUtf16,
    int UnresolvedUtf16,
    bool IsFullyAccounted,
    bool IsVerifiedComplete,
    bool IsPartialImport);
internal sealed record IssueGroup(ImportPreviewIssueCode Code, DetectionIssueSeverity Severity, int Count);
internal sealed record RegionSummary(int Count, int TotalCharactersUtf16, RegionReport[] Regions);
internal sealed record RegionReport(int Offset, int Length, string[]? FirstNonBlankLines);
