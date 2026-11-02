using System.Diagnostics;
using System.Text;

namespace QuickTranslator.AiEditor.Core.AutoTranslate;

public sealed record CodexSurveyOptions
{
    /// <summary>Codex workspace with its own login and quota (default: the business workspace).</summary>
    public string CodexHome { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-business");
    public string Model { get; init; } = "gpt-6-luna";
    public string Effort { get; init; } = "high";
    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(2);
    /// <summary>How many times Codex may fix files QuickTranslator could not read.</summary>
    public int RepairAttempts { get; init; } = 1;
    /// <summary>Genre tags already used on other books, offered first so the published site groups them together.</summary>
    public IReadOnlyList<string> KnownGenres { get; init; } = [];
}

public sealed record CodexRunRequest(string WorkDirectory, string Prompt, string LastMessagePath, CodexSurveyOptions Options);

public interface ICodexProcessRunner
{
    /// <summary>Runs one non-interactive Codex task; returns the exit code. Output lines go to <paramref name="output"/>.</summary>
    Task<int> RunAsync(CodexRunRequest request, IProgress<string>? output, CancellationToken cancellationToken);
}

/// <summary>
/// Surveys a whole raw book with Codex: Codex reads raw.txt in its own work folder and writes characters.json and
/// glossary.json, which are converted (and, if unreadable, sent back once for repair) before QuickTranslator uses them.
/// </summary>
public sealed class CodexSurveyRunner(CodexSurveyOptions options, ICodexProcessRunner? runner = null)
{
    public const string CharactersFile = "characters.json";
    public const string GlossaryFile = "glossary.json";
    public const string BookFile = "book.json";
    private readonly ICodexProcessRunner _runner = runner ?? new CodexCliRunner();

    // Not under AppData: Codex's elevated Windows sandbox runs commands as a separate user that cannot open it (error 267).
    public static string DefaultWorkRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QuickTranslator", "codex-survey");

    public async Task<CodexSurveyImportResult> RunAsync(string rawText, string workDirectory, Guid projectId,
        string bookTitle, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(workDirectory);
        string characters = Path.Combine(workDirectory, CharactersFile), glossary = Path.Combine(workDirectory, GlossaryFile),
            book = Path.Combine(workDirectory, BookFile);
        File.Delete(characters);
        File.Delete(glossary);
        File.Delete(book);
        await File.WriteAllTextAsync(Path.Combine(workDirectory, "raw.txt"), rawText, new UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);

        string prompt = CodexSurveyPrompt.Survey(bookTitle, options.KnownGenres);
        for (int attempt = 0; ; attempt++)
        {
            string last = Path.Combine(workDirectory, $"codex-last-{attempt}.md");
            await File.WriteAllTextAsync(Path.Combine(workDirectory, $"prompt-{attempt}.md"), prompt, new UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            int exit = await _runner.RunAsync(new(workDirectory, prompt, last, options), progress, cancellationToken)
                .ConfigureAwait(false);
            string problem;
            if (exit != 0) problem = $"Codex kết thúc với mã lỗi {exit}.";
            else if (!File.Exists(characters) || !File.Exists(glossary))
                problem = $"Codex chưa tạo đủ {CharactersFile} và {GlossaryFile}.";
            else
            {
                try
                {
                    return CodexSurveyImport.Convert(
                        await File.ReadAllTextAsync(characters, cancellationToken).ConfigureAwait(false),
                        await File.ReadAllTextAsync(glossary, cancellationToken).ConfigureAwait(false),
                        projectId, bookTitle,
                        File.Exists(book) ? await File.ReadAllTextAsync(book, cancellationToken).ConfigureAwait(false) : null);
                }
                catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { problem = ex.Message; }
            }
            // A failed run (quota, login, crash) is not something a second prompt can fix.
            if (exit != 0 || attempt >= options.RepairAttempts)
                throw new InvalidOperationException(problem + TailOf(last));
            progress?.Report("Codex đang sửa lại file: " + problem);
            prompt = CodexSurveyPrompt.Repair(problem);
        }
    }

    private static string TailOf(string path)
    {
        if (!File.Exists(path)) return "";
        string text = File.ReadAllText(path).Trim();
        return text.Length == 0 ? "" : Environment.NewLine + (text.Length > 600 ? "…" + text[^600..] : text);
    }
}

/// <summary>Runs the npm-installed Codex CLI (<c>codex exec</c>) with node directly, so arguments never pass through cmd.exe.</summary>
public sealed class CodexCliRunner : ICodexProcessRunner
{
    public async Task<int> RunAsync(CodexRunRequest request, IProgress<string>? output, CancellationToken cancellationToken)
    {
        CodexSurveyOptions options = request.Options;
        if (!File.Exists(Path.Combine(options.CodexHome, "auth.json")))
            throw new InvalidOperationException($"Codex chưa đăng nhập trong {options.CodexHome}. Hãy đăng nhập Codex ở terminal trước.");
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string script = Path.Combine(appData, "npm", "node_modules", "@openai", "codex", "bin", "codex.js");
        string node = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        if (!File.Exists(script) || !File.Exists(node))
            throw new InvalidOperationException("Không tìm thấy Codex CLI (npm i -g @openai/codex) hoặc Node.js.");

        var start = new ProcessStartInfo(node)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkDirectory
        };
        foreach (string argument in new[]
                 {
                     script, "exec", "-s", "workspace-write", "-C", request.WorkDirectory, "-o", request.LastMessagePath,
                     "--skip-git-repo-check", "-m", options.Model, "-c", $"model_reasoning_effort=\"{options.Effort}\"", "-"
                 })
            start.ArgumentList.Add(argument);
        start.Environment["CODEX_HOME"] = options.CodexHome;
        start.Environment["PATH"] = Path.GetDirectoryName(node) + ";" + Path.Combine(appData, "npm") + ";" +
            Environment.GetEnvironmentVariable("PATH");

        using var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        void Forward(object _, DataReceivedEventArgs e) { if (!string.IsNullOrWhiteSpace(e.Data)) output?.Report(e.Data.Trim()); }
        process.OutputDataReceived += Forward;
        process.ErrorDataReceived += Forward;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.StandardInput.WriteAsync(request.Prompt).ConfigureAwait(false);
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TimeoutException($"Codex chạy quá {options.Timeout.TotalMinutes:N0} phút và đã bị dừng.");
        }
        return process.ExitCode;
    }
}
