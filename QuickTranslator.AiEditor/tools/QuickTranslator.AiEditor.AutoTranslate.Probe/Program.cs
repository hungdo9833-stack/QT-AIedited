using QuickTranslator.AiEditor.Core.AutoTranslate;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Traditional;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.WebImport;

// Runs the "Tự động dịch từ link" preparation (download, new project, Codex survey) for real, without translating.
// Usage: AutoTranslate.Probe <url> <projects-dir> [--analyze-only] [--model gpt-6-luna] [--effort high] [--workspace business]
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length < 2) { Console.Error.WriteLine("Usage: <url> <projects-dir> [--analyze-only] [--model m] [--effort e] [--workspace w]"); return 1; }
var url = new Uri(args[0]);
string projects = Path.GetFullPath(args[1]);
string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }

if (args.Contains("--analyze-only"))
{
    using var importer = new WebNovelImporter(new WebImportOptions());
    WebBookAnalysis analysis = await importer.AnalyzeAsync(url);
    Console.WriteLine($"Title: {analysis.BookTitle}; chapters: {analysis.Chapters.Count}; {analysis.DetectionSummary}");
    foreach (string warning in analysis.Warnings) Console.WriteLine("  warning: " + warning);
    foreach (WebChapterLink link in analysis.Chapters.Take(3).Concat(analysis.Chapters.TakeLast(2))) Console.WriteLine($"  {link.Index}: {link.Title}");
    WebChapterContent first = await importer.FetchChapterAsync(analysis.Chapters[0]);
    Console.WriteLine($"First chapter: {first.Title}; {first.Text.Length:N0} chars; {first.PageCount} page(s)");
    string[] lines = first.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
    foreach (string line in lines.Take(6)) Console.WriteLine("  | " + line);
    Console.WriteLine($"  lines ending in a digit: {lines.Count(l => char.IsAsciiDigit(l[^1]))}/{lines.Length}");
    return 0;
}

if (args.Contains("--fetch-all"))
{
    string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..16];
    using var importer = new WebNovelImporter(new WebImportOptions
    {
        DelayBetweenRequests = TimeSpan.FromSeconds(1.5),
        CacheDirectory = Path.Combine(CodexSurveyRunner.DefaultWorkRoot, hash, "pages")
    });
    WebBookAnalysis analysis = await importer.AnalyzeAsync(url);
    foreach (WebChapterLink link in analysis.Chapters)
    {
        try
        {
            WebChapterContent chapter = await importer.FetchChapterAsync(link);
            if (chapter.Text.Length < 800) Console.WriteLine($"  short {link.Index}: {link.Title}; {chapter.Text.Length} chars; {link.Url}");
        }
        catch (WebImportBlockedException ex) { Console.WriteLine($"  FAIL {link.Index}: {link.Title}; {ex.Reason}; {link.Url}"); }
    }
    Console.WriteLine($"Checked {analysis.Chapters.Count} chapters.");
    return 0;
}

var codex = new CodexSurveyOptions
{
    Model = Option("--model", "gpt-6-luna"),
    Effort = Option("--effort", "high"),
    CodexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-" + Option("--workspace", "business"))
};
var traditional = new TraditionalTranslationService();
string dataDirectory = (await new TraditionalEditorPreferencesStore(TraditionalEditorPreferencesStore.DefaultPath).LoadAsync()).DataDirectory;
if (!string.IsNullOrWhiteSpace(dataDirectory)) { await traditional.LoadDataAsync(dataDirectory); Console.WriteLine("VietPhrase Data: " + dataDirectory); }
var preparer = new AutoBookPreparer(new ProjectRepository(), options => new WebNovelImporter(options),
    options => new CodexSurveyRunner(options), new BulkImportChapterDefaults(new SegmenterOptions(), new ModelOptions(), PromptPreset.Default))
{
    TitleBackend = traditional.CurrentSnapshot is null ? null : new TraditionalChapterTitleTranslationBackend(traditional, hanViet: false)
};
string lastStage = "";
var status = new Progress<AutoBookStatus>(value =>
{
    if (value.Stage != lastStage || value.Stage == "Codex khảo sát") Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {value.Stage}: {value.Detail}");
    lastStage = value.Stage;
});
var started = DateTime.Now;
AutoBookPrepared prepared = await preparer.PrepareAsync(url, projects, TimeSpan.FromSeconds(1.5), codex, status);
Console.WriteLine($"Done in {(DateTime.Now - started).TotalMinutes:N1} phút: {prepared.Summary}");
Console.WriteLine("Project: " + prepared.ManifestPath);
foreach (string warning in prepared.Survey.Warnings) Console.WriteLine("  dropped: " + warning);
return 0;
