using System.Diagnostics;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

int checkpoints = args.Length > 0 && int.TryParse(args[0], out int parsed) ? parsed : 1_000;
string root = Path.Combine(Path.GetTempPath(), "QTStudio-Storage-Benchmark", Guid.NewGuid().ToString("N"));
try
{
    var repository = new ProjectRepository();
    ProjectSession session = await repository.CreateAsync(root, "Storage benchmark synthetic");
    string source = string.Concat(Enumerable.Repeat("天地玄黄宇宙洪荒。", 220));
    var chapter = TranslationService.CreateChapter("Chương tổng hợp", source);
    await repository.AddChapterAsync(session, chapter, 1);
    chapter = (await repository.LoadChapterAsync(session, chapter.Id)).Chapter;

    Stopwatch saves = Stopwatch.StartNew();
    for (int index = 0; index < checkpoints; index++)
    {
        chapter.ConfirmedContextNotes = "Synthetic checkpoint " + index;
        chapter.UpdatedAt = DateTimeOffset.UtcNow;
        await repository.UpdateChapterAsync(session, chapter);
    }
    saves.Stop();

    string transactions = session.Resolve("History/Transactions");
    int directories = Directory.Exists(transactions)
        ? Directory.EnumerateDirectories(transactions, "*", SearchOption.TopDirectoryOnly).Count() : 0;
    int files = Directory.Exists(transactions)
        ? Directory.EnumerateFiles(transactions, "*", SearchOption.AllDirectories).Count() : 0;
    long bytes = Directory.Exists(transactions)
        ? Directory.EnumerateFiles(transactions, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : 0;

    Stopwatch reopen = Stopwatch.StartNew();
    ProjectSession reopened = (await new ProjectRepository().OpenAsync(session.ManifestPath)).Session;
    reopen.Stop();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        Checkpoints = checkpoints,
        SaveMilliseconds = Math.Round(saves.Elapsed.TotalMilliseconds, 3),
        AverageSaveMilliseconds = Math.Round(saves.Elapsed.TotalMilliseconds / checkpoints, 3),
        TransactionDirectories = directories,
        TransactionFiles = files,
        TransactionBytes = bytes,
        ReopenMilliseconds = Math.Round(reopen.Elapsed.TotalMilliseconds, 3),
        RecoveryItems = reopened.RecoveryReport.Transactions.Count
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally
{
    string guard = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "QTStudio-Storage-Benchmark")) + Path.DirectorySeparatorChar;
    string full = Path.GetFullPath(root);
    if (full.StartsWith(guard, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full))
        Directory.Delete(full, recursive: true);
}
