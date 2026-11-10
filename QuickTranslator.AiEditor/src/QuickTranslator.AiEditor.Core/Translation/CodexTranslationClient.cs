using System.Text;
using QuickTranslator.AiEditor.Core.AutoTranslate;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>The Codex workspace refused because its usage window is used up; the worker stops instead of failing every chapter.</summary>
public sealed class CodexTranslationQuotaException(string message) : InvalidOperationException(message);

/// <summary>
/// Translates with a ChatGPT model through the Codex CLI (<c>codex exec</c>) of a logged-in workspace: the request is
/// the same instTrans prompt the other backends get, and Codex's last message is the translation. Each request runs in
/// its own empty folder, so Codex has nothing to read or change; quota comes from the workspace's ChatGPT plan.
/// </summary>
public sealed class CodexTranslationClient(CodexSurveyOptions options, ICodexProcessRunner? runner = null, string? workRoot = null)
    : ILlmClient
{
    private readonly ICodexProcessRunner _runner = runner ?? new CodexCliRunner();
    private readonly string _workRoot = workRoot ?? DefaultWorkRoot;

    // Not under AppData: Codex's elevated Windows sandbox runs as a separate user that cannot open it (error 267).
    public static string DefaultWorkRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "QuickTranslator", "codex-translate");

    public const string Instruction =
        "Bạn là dịch giả truyện Trung → Việt. Hãy dịch theo đúng yêu cầu bên dưới (nó dùng định dạng của mô hình dịch " +
        "Index-Translate: nguyên văn ở 【源文】, các ràng buộc đánh số ở 【约束要求】; tuân thủ mọi ràng buộc đó).\n" +
        "- Không chạy lệnh, không đọc hay tạo file. Chỉ trả lời bằng tin nhắn.\n" +
        "- Tin nhắn cuối cùng của bạn CHỈ chứa bản dịch tiếng Việt hoàn chỉnh của toàn bộ 【源文】, giữ nguyên số đoạn và " +
        "thứ tự; không lời dẫn, không giải thích, không Markdown, không lặp lại ràng buộc.\n\n";

    public CodexSurveyOptions Options => options;

    public async Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions modelOptions, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prompt);
        string folder = Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var started = DateTimeOffset.UtcNow;
        var output = new List<string>();
        var progress = new DirectProgress<string>(line => { lock (output) { output.Add(line); if (output.Count > 200) output.RemoveAt(0); } });
        try
        {
            string last = Path.Combine(folder, "last.md");
            int exit = await _runner.RunAsync(new CodexRunRequest(folder, Instruction + prompt, last, options), progress,
                cancellationToken).ConfigureAwait(false);
            string log;
            lock (output) log = string.Join('\n', output);
            if (exit != 0)
            {
                if (IsQuota(log))
                    throw new CodexTranslationQuotaException(
                        $"Codex ({Path.GetFileName(options.CodexHome)}) đã hết lượt dùng trong khung 5 giờ; chờ lượt reset rồi dịch tiếp.");
                throw new LlmRequestException(LlmErrorKind.Http, $"Codex kết thúc với mã lỗi {exit}." + Tail(log));
            }
            string text = File.Exists(last) ? Clean(await File.ReadAllTextAsync(last, cancellationToken).ConfigureAwait(false)) : "";
            if (text.Length == 0) throw new LlmRequestException(LlmErrorKind.InvalidResponse, "Codex không trả bản dịch." + Tail(log));
            return new LlmCompletion(text, true, "stop", new GenerationMetrics { ClientElapsed = DateTimeOffset.UtcNow - started });
        }
        catch (TimeoutException ex)
        {
            throw new LlmRequestException(LlmErrorKind.Timeout, ex.Message, ex);
        }
        catch (InvalidOperationException ex) when (ex is not CodexTranslationQuotaException && ex.Message.Contains("Codex", StringComparison.Ordinal))
        {
            // Not logged in or CLI missing: a setup problem, same for every chapter.
            throw new LlmRequestException(LlmErrorKind.Connection, ex.Message, ex);
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>The workspace's usage window or credits are used up (the CLI's rate-limit error).</summary>
    internal static bool IsQuota(string log) =>
        log.Contains("usage limit", StringComparison.OrdinalIgnoreCase) ||
        log.Contains("rate limit", StringComparison.OrdinalIgnoreCase) ||
        log.Contains("rate_limit", StringComparison.OrdinalIgnoreCase) ||
        log.Contains("insufficient_quota", StringComparison.OrdinalIgnoreCase) ||
        log.Contains("429", StringComparison.Ordinal);

    /// <summary>Codex's last message without a code fence it may wrap the text in.</summary>
    internal static string Clean(string message)
    {
        string text = message.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            int firstLine = text.IndexOf('\n');
            int lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine >= 0 && lastFence > firstLine) text = text[(firstLine + 1)..lastFence].Trim();
        }
        return text;
    }

    private static string Tail(string log)
    {
        string text = log.Trim();
        return text.Length == 0 ? "" : " " + (text.Length > 400 ? "…" + text[^400..] : text);
    }
}
