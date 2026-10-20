using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Projects;

// Addressing Ledger step 1 probe. CPU only: never calls an AI provider and never writes to the given
// Project. The Project folder is copied to %TEMP% first; only the copy is opened, and it is removed after.
string? project = null, outDir = null; int samples = 12, minimumMentions = 3, maxRequests = 1; bool live = false; string model = "gemini-3.6-flash";
const string Confirmation = "SEND ADDRESSING SURVEY";
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" when i + 1 < args.Length: project = args[++i]; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--samples" when i + 1 < args.Length && int.TryParse(args[++i], out int s) && s is > 0 and <= 50: samples = s; break;
        case "--min-mentions" when i + 1 < args.Length && int.TryParse(args[++i], out int m) && m > 0: minimumMentions = m; break;
        case "--live": live = true; break;
        case "--model" when i + 1 < args.Length && args[i + 1] is "gemini-3.6-flash" or "gemini-3.8-flash" or "gemini-3.5-flash-lite": model = args[++i]; break;
        case "--max-requests" when i + 1 < args.Length && int.TryParse(args[++i], out int r) && r is >= 1 and <= 20: maxRequests = r; break;
        default:
            Console.WriteLine("Usage: --project <path\\Name.qtproj> --out <report folder> [--samples 12] [--min-mentions 3] [--live --model gemini-3.6-flash --max-requests 1]");
            return 2;
    }
}
if (project is null || outDir is null || !File.Exists(project) || !project.EndsWith(".qtproj", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("A valid --project <*.qtproj> and --out <folder> are required.");
    return 2;
}

string source = Path.GetDirectoryName(Path.GetFullPath(project))!;
string copy = Path.Combine(Path.GetTempPath(), "qtstudio-addressing-copy-" + Guid.NewGuid().ToString("N"));
try
{
    CopyDirectory(source, copy);
    var session = (await new ProjectRepository().OpenAsync(Path.Combine(copy, Path.GetFileName(project)))).Session;
    var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(session, new ProjectRepository());
    var resources = await new ProjectResourceStore(session).LoadAsync();
    var names = AddressingAiSurvey.CandidateNames(session.Manifest.ProjectId, sources, resources, minimumMentions);
    var report = AddressingDialogueSurvey.Analyze(sources, names, samples);

    Directory.CreateDirectory(outDir);
    var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter() } };
    await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step1.json"), JsonSerializer.Serialize(report, json), new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step1.md"), Markdown(session.Manifest.VietnameseTitle, report), new UTF8Encoding(false));
    // Step 2 offline plan: what WOULD be sent. No provider is constructed and nothing is sent.
    var tasks = AddressingAiSurvey.PlanTasks(sources, names);
    var requests = AddressingAiSurvey.PlanRequests(tasks, "gemini-3.6-flash");
    var variants = NameVariantSurvey.Analyze(sources, names);
    await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step2-plan.md"), PlanMarkdown(tasks, requests, variants), new UTF8Encoding(false));
    if (requests.Count > 0) await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step2-first-prompt.txt"), requests[0].Prompt, new UTF8Encoding(false));
    Console.WriteLine($"STEP2 offline: tasks={tasks.Count} (pairs={tasks.Count(t => t.Kind == AddressingTaskKind.Pair)}, characters={tasks.Count(t => t.Kind == AddressingTaskKind.Character)}) " +
        $"requests={requests.Count} estimatedInputTokens={requests.Sum(r => r.EstimatedInputTokens):N0} HTTP=0");
    Console.WriteLine($"OK chapters={report.Chapters} dialogue={report.DialogueLines} explicitSpeaker={report.ExplicitSpeakerLines} " +
        $"narration={report.NarrationSpeakerLines} pronoun={report.PronounSpeakerLines} unattributed={report.UnattributedLines} listenerKnown={report.ListenerKnownLines} " +
        $"names={report.CandidateNames.Count} pairs={report.Pairs.Count} -> {Path.GetFullPath(outDir)}");
    if (!live) return 0; // Offline: no key read, no provider constructed.

    // Live: only after a typed confirmation; at most --max-requests HTTP requests; runs on the temp COPY.
    Console.WriteLine($"Will send at most {maxRequests} request(s) to {model}, one try each, no retry or fallback. The real Project is not written.");
    Console.WriteLine("Type exactly: " + Confirmation + " (anything else cancels)");
    if (Console.ReadLine() != Confirmation) { Console.WriteLine("CancelledBeforeSend: HTTP=0"); return 0; }
    string key = Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_API_KEY") ?? "";
    if (string.IsNullOrWhiteSpace(key)) { Console.WriteLine("ApiKeyMissing: set QTSTUDIO_GEMINI_API_KEY for this PowerShell session only. HTTP=0"); return 2; }
    var options = new QuickTranslator.AiEditor.Core.Models.ModelOptions { Model = model, ContextSize = 65536, MaximumOutputTokens = 8192 };
    using var backend = SemanticSurveyBackend.Gemini(key, GeminiSurveyProjectScope.Resolve(key, Environment.GetEnvironmentVariable("QTSTUDIO_GEMINI_PROJECT_ID")),
        options, new SemanticSurveyBudget(maxRequests, 1_000_000));
    var ledger = new AddressingLedgerStore(session);
    // Resume across probe runs: each run uses a fresh copy, so restore the previous checkpoint from --out.
    string previous = Path.Combine(outDir, "addressing-ledger.v1.json");
    if (File.Exists(previous)) { Directory.CreateDirectory(Path.GetDirectoryName(ledger.PathName)!); File.Copy(previous, ledger.PathName, overwrite: true); }
    var run = await new AddressingAiEngine(backend).RunAsync(ledger, tasks, options, maxRequests);
    File.Copy(ledger.PathName, Path.Combine(outDir, "addressing-ledger.v1.json"), overwrite: true);
    await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step2-result.md"), ResultMarkdown(run), new UTF8Encoding(false));
    Console.WriteLine($"LIVE sent={run.RequestsSent} planned={run.Planned} stoppedBy={run.StoppedBy ?? "-"} proposals={run.State.Proposals.Count} " +
        $"verified={run.State.Proposals.Count(p => p.EvidenceValid)} lastError={run.State.Attempts.LastOrDefault()?.ErrorCode ?? "-"}");
    return run.State.Attempts.LastOrDefault()?.Succeeded == true ? 0 : 1;
}
finally
{
    if (Directory.Exists(copy)) Directory.Delete(copy, recursive: true);
}

static void CopyDirectory(string from, string to)
{
    Directory.CreateDirectory(to);
    foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
    {
        if (file.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) continue;
        string target = Path.Combine(to, Path.GetRelativePath(from, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

static string ResultMarkdown(AddressingRunResult run)
{
    var md = new StringBuilder("# Sổ xưng hô · Bước 2 · kết quả AI (chưa duyệt)\n\n");
    md.AppendLine($"Đã gửi {run.RequestsSent}/{run.Planned} request; dừng bởi: {run.StoppedBy ?? "—"}. Đề xuất chỉ là giả thuyết đã kiểm chứng cơ học, **chưa được duyệt**.").AppendLine();
    md.AppendLine("| Loại | Chủ thể | Trạng thái | Kiểm chứng | Cách gọi | Lỗi |").AppendLine("|---|---|---|---|---|---|");
    foreach (var p in run.State.Proposals)
    {
        string terms = p.Kind == AddressingTaskKind.Pair
            ? string.Join("; ", p.Phases.Select(x => $"ch.{x.FromChapter}–{(x.ToChapter == 0 ? "…" : x.ToChapter)}: xưng {x.SelfVi} / gọi {x.AddressVi}"))
            : $"{p.Gender}; " + string.Join("; ", p.Phases.Select(x => $"ch.{x.FromChapter}–{(x.ToChapter == 0 ? "…" : x.ToChapter)}: {x.NarrativeVi}")) +
              (p.NameForms.Count > 0 ? "; tên: " + string.Join(", ", p.NameForms.Select(f => $"{f.Zh}={f.Vi}")) : "");
        md.AppendLine($"| {p.Kind} | {p.Subject}{(p.Listener is null ? "" : " → " + p.Listener)} | {p.Status} | {(p.EvidenceValid ? "✅" : "❌")} | {terms} | {string.Join(", ", p.Issues)} |");
    }
    return md.ToString();
}

static string PlanMarkdown(IReadOnlyList<AddressingTask> tasks, IReadOnlyList<AddressingRequestPlan> requests, IReadOnlyList<NameVariantGroup> variants)
{
    var md = new StringBuilder();
    md.AppendLine("# Sổ xưng hô · Bước 2 · kế hoạch offline (chưa gọi AI)").AppendLine();
    md.AppendLine($"- Nhiệm vụ: {tasks.Count} ({tasks.Count(t => t.Kind == AddressingTaskKind.Pair)} cặp, {tasks.Count(t => t.Kind == AddressingTaskKind.Character)} nhân vật)");
    md.AppendLine($"- Request dự kiến: {requests.Count}; ước tính input khoảng {requests.Sum(r => r.EstimatedInputTokens):N0} token (heuristic, không phải số đo).").AppendLine();
    md.AppendLine("## Biến thể tên (CPU, chờ AI + người duyệt)").AppendLine();
    foreach (var g in variants.Where(g => g.Variants.Count > 0 || g.SharedForms.Count > 0))
        md.AppendLine($"- **{g.FullName}**: {string.Join(", ", g.Variants.Select(v => $"{v.Form}×{v.Occurrences}"))}" +
            (g.SharedForms.Count > 0 ? $" · mơ hồ (nhiều chủ): {string.Join(", ", g.SharedForms)}" : ""));
    md.AppendLine().AppendLine("## Nhân vật · lời kể ngôi thứ 3").AppendLine();
    md.AppendLine("| Nhân vật | Mẫu | 他 | 她 | Giới tính theo nguyên văn | Chương |").AppendLine("|---|---:|---:|---:|---|---|");
    foreach (var t in tasks.Where(t => t.Kind == AddressingTaskKind.Character))
        md.AppendLine($"| {t.Subject} | {t.Samples.Count} | {t.He} | {t.She} | {t.SourceGender} | {t.FirstChapter}–{t.LastChapter} |");
    md.AppendLine().AppendLine("## Cặp · xưng hô trong thoại").AppendLine();
    md.AppendLine("| Người nói → người nghe | Mẫu | Chương | Gom biến thể |").AppendLine("|---|---:|---|---|");
    foreach (var t in tasks.Where(t => t.Kind == AddressingTaskKind.Pair))
        md.AppendLine($"| {t.Subject} → {t.Listener} | {t.Samples.Count} | {t.FirstChapter}–{t.LastChapter} | {(t.NameCandidates.Count == 0 ? "—" : string.Join(", ", t.NameCandidates.Select(v => v.Form)))} |");
    return md.ToString();
}

static string Markdown(string title, AddressingSurveyReport r)
{
    static string Pct(int part, int total) => total == 0 ? "0%" : $"{100.0 * part / total:0.#}%";
    static string Terms(IReadOnlyDictionary<string, int> terms) => terms.Count == 0 ? "—" : string.Join(", ", terms.Take(6).Select(t => $"{t.Key}×{t.Value}"));
    var md = new StringBuilder();
    md.AppendLine($"# Sổ xưng hô · Bước 1 (CPU) · {title}").AppendLine();
    md.AppendLine("Báo cáo chỉ đọc: giả thuyết của CPU, **chưa phải dữ kiện**, chưa gọi AI và không ghi vào Project.").AppendLine();
    md.AppendLine("| Chỉ số | Giá trị |").AppendLine("|---|---:|");
    md.AppendLine($"| Chương | {r.Chapters} |").AppendLine($"| Ký tự nguyên văn | {r.SourceUtf16:N0} |");
    md.AppendLine($"| Lời thoại tách được | {r.DialogueLines} |");
    md.AppendLine($"| Người nói rõ ràng (Explicit) | {r.ExplicitSpeakerLines} ({Pct(r.ExplicitSpeakerLines, r.DialogueLines)}) |");
    md.AppendLine($"| Người nói theo câu kể ngay trước (yếu hơn) | {r.NarrationSpeakerLines} ({Pct(r.NarrationSpeakerLines, r.DialogueLines)}) |");
    md.AppendLine($"| Người nói là đại từ (chỉ ứng viên) | {r.PronounSpeakerLines} ({Pct(r.PronounSpeakerLines, r.DialogueLines)}) |");
    md.AppendLine($"| Không xác định người nói | {r.UnattributedLines} ({Pct(r.UnattributedLines, r.DialogueLines)}) |");
    md.AppendLine($"| Có ứng viên người nghe | {r.ListenerKnownLines} ({Pct(r.ListenerKnownLines, r.DialogueLines)}) |");
    md.AppendLine($"| Tên ứng viên dùng để khớp | {r.CandidateNames.Count} |").AppendLine($"| Cặp (người nói → người nghe) | {r.Pairs.Count} |").AppendLine();
    if (r.AliasHints.Count > 0)
    {
        md.AppendLine("## Gợi ý bí danh (chưa gộp, cần người duyệt)").AppendLine();
        foreach (var hint in r.AliasHints.OrderBy(x => x.Value, StringComparer.Ordinal)) md.AppendLine($"- {hint.Key} ≈ {hint.Value}");
        md.AppendLine();
    }
    md.AppendLine("## Cặp nhân vật").AppendLine();
    md.AppendLine("| Người nói → người nghe | Lời thoại | Nói rõ | Nghe: hô gọi / mệnh đề / cảnh | Chương | Tự xưng | Hô gọi | 你/您 |");
    md.AppendLine("|---|---:|---:|---|---|---|---|---|");
    foreach (var p in r.Pairs.Take(60))
        md.AppendLine($"| {p.Speaker} → {p.Listener ?? "?"} | {p.Lines} | {p.ExplicitSpeakerLines} | {p.VocativeListenerLines} / {p.ClauseListenerLines} / {p.SceneListenerLines} | " +
            $"{p.FirstChapter}–{p.LastChapter} | {Terms(p.SelfTerms)} | {Terms(p.VocativeTerms)} | {p.SecondPerson}/{p.HonorificSecondPerson} |");
    md.AppendLine().AppendLine("## Mẫu thoại của các cặp chính").AppendLine();
    foreach (var p in r.Pairs.Where(x => x.Listener is not null).Take(15))
    {
        md.AppendLine($"### {p.Speaker} → {p.Listener} ({p.Lines} lời thoại)").AppendLine();
        foreach (var s in p.Samples.Take(6))
            md.AppendLine($"- Ch.{s.ChapterNumber} @{s.QuoteStartUtf16} · {s.SpeakerEvidence}/{s.ListenerEvidence}: `{s.LeadClause}` 「{s.Quote}」");
        md.AppendLine();
    }
    return md.ToString();
}
