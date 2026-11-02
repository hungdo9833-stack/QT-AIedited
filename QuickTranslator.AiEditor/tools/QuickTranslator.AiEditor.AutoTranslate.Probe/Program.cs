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
var url = args[0].StartsWith("--", StringComparison.Ordinal) ? null! : new Uri(args[0]);
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

// --reapply <codex-folder> <project.qtproj>: convert an earlier Codex result again with the current rules (no Codex call)
// and replace the project's characters and glossary. The previous resources file is kept next to it as a backup.
if (args[0] == "--reapply")
{
    var repository = new ProjectRepository();
    ProjectSession session = (await repository.OpenAsync(Path.GetFullPath(args[2]))).Session;
    var store = new ProjectResourceStore(session);
    File.Copy(store.PathName, store.PathName + ".before-reapply-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: false);
    string bookPath = Path.Combine(args[1], CodexSurveyRunner.BookFile);
    CodexSurveyImportResult result = CodexSurveyImport.Convert(
        await File.ReadAllTextAsync(Path.Combine(args[1], CodexSurveyRunner.CharactersFile)),
        await File.ReadAllTextAsync(Path.Combine(args[1], CodexSurveyRunner.GlossaryFile)),
        session.Manifest.ProjectId, session.Manifest.VietnameseTitle,
        File.Exists(bookPath) ? await File.ReadAllTextAsync(bookPath) : null);
    ProjectResources resources = await store.LoadAsync();
    resources.Characters = result.Characters;
    resources.Terms = result.Terms.ToList();
    await store.SaveAsync(resources);
    if (result.Book is { } book)
    {
        AutoBookPreparer.ApplyBookInfo(session.Manifest, book);
        await repository.SaveManifestAsync(session);
        Console.WriteLine($"Title: {book.VietnameseTitle}\nGenres: {string.Join(", ", book.Genres)}\nSynopsis ({book.Synopsis.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words):\n{book.Synopsis}\n");
    }
    foreach (var profile in result.Characters.Profiles)
        Console.WriteLine($"  {profile.VietnameseName}: {profile.PrimaryNarrativePronoun} / {profile.SecondaryNarrativePronoun}");
    foreach (string warning in result.Warnings) Console.WriteLine("  note: " + warning);
    return 0;
}

// --book-info <codex-folder> <project.qtproj>: ask Codex for book.json only (title, blurb, genres) in a folder that still holds
// raw.txt and characters.json, then run --reapply with the same arguments to apply it.
if (args[0] == "--book-info")
{
    var options = new CodexSurveyOptions
    {
        Model = Option("--model", "gpt-6-luna"), Effort = Option("--effort", "high"),
        CodexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-" + Option("--workspace", "business"))
    };
    ProjectSession session = (await new ProjectRepository().OpenAsync(Path.GetFullPath(args[2]))).Session;
    string folder = Path.GetFullPath(args[1]);
    File.Delete(Path.Combine(folder, CodexSurveyRunner.BookFile));
    string prompt = CodexSurveyPrompt.BookOnly(session.Manifest.ChineseTitle.Length > 0 ? session.Manifest.ChineseTitle : session.Manifest.VietnameseTitle);
    await File.WriteAllTextAsync(Path.Combine(folder, "prompt-book.md"), prompt);
    var codexStarted = DateTime.Now;
    int exit = await new CodexCliRunner().RunAsync(new(folder, prompt, Path.Combine(folder, "codex-last-book.md"), options),
        new Progress<string>(line => { if (line.Contains("ERROR")) Console.WriteLine("  " + line); }), CancellationToken.None);
    Console.WriteLine($"Codex exit {exit} after {(DateTime.Now - codexStarted).TotalMinutes:N1} phút; book.json: {File.Exists(Path.Combine(folder, CodexSurveyRunner.BookFile))}");
    return exit;
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
