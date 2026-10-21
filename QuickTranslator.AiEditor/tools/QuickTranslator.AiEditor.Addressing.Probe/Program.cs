using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

// Addressing Ledger probe (steps 1, 2 and 2b). Offline by default: never calls an AI provider and never writes to the given
// Project. The Project folder is copied to %TEMP% first; only the copy is opened, and it is removed after.
string? project = null, outDir = null; int samples = 12, minimumMentions = 3, maxRequests = 1; bool live = false, mockScenes = false; string model = "gemini-3.6-flash";
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
        case "--mock-scenes": mockScenes = true; break;
        case "--model" when i + 1 < args.Length && args[i + 1] is "gemini-3.6-flash" or "gemini-3.8-flash" or "gemini-3.5-flash-lite": model = args[++i]; break;
        case "--max-requests" when i + 1 < args.Length && int.TryParse(args[++i], out int r) && r is >= 1 and <= 20: maxRequests = r; break;
        default:
            Console.WriteLine("Usage: --project <path\\Name.qtproj> --out <report folder> [--samples 12] [--min-mentions 3] [--mock-scenes | --live --model gemini-3.6-flash --max-requests 1]");
            return 2;
    }
}
if (project is null || outDir is null || !File.Exists(project) || !project.EndsWith(".qtproj", StringComparison.OrdinalIgnoreCase) || live && mockScenes)
{
    Console.WriteLine("A valid --project <*.qtproj> and --out <folder> are required; --mock-scenes and --live cannot be combined.");
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
    bool filterJunk = Environment.GetEnvironmentVariable("QT_PROBE_NO_FILTER") != "1"; // Baseline for before/after comparisons.
    var names = AddressingAiSurvey.CandidateNames(session.Manifest.ProjectId, sources, resources, minimumMentions, filterJunk);
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
    // Step 2b offline: contiguous dialogue scenes the CPU cannot attribute.
    var allScenes = AddressingDialogueSurvey.Scenes(sources, names);
    var titleForms = AddressingAiSurvey.TitleForms(session.Manifest.ProjectId, sources, resources, minimumMentions);
    var scenes = AddressingAiSurvey.PlanScenes(sources, names, titleForms: titleForms);
    var sceneRequests = AddressingAiSurvey.PlanRequests(scenes, "gemini-3.6-flash");
    await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step2b-scenes.md"), SceneMarkdown(allScenes, scenes, sceneRequests), new UTF8Encoding(false));
    if (sceneRequests.Count > 0) await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step2b-first-prompt.txt"), sceneRequests[0].Prompt, new UTF8Encoding(false));
    Console.WriteLine($"STEP2B offline: titles={titleForms.Count} scenes={allScenes.Count} selected={scenes.Count} quotes={scenes.Sum(t => t.Samples.Count)} " +
        $"cpuExplicit={scenes.Sum(t => t.Samples.Count(s => s.CpuSpeaker is not null))} requests={sceneRequests.Count} " +
        $"estimatedInputTokens={sceneRequests.Sum(r => r.EstimatedInputTokens):N0} HTTP=0");
    // Whole survey as the app plans it (scenes + characters + pairs + identities), packed for Gemini. Counts only; HTTP=0.
    {
        var geminiOptions = new QuickTranslator.AiEditor.Core.Models.ModelOptions { Model = model, ContextSize = 65536, MaximumOutputTokens = 10_240 };
        var full = AddressingSurveyRunner.Plan(session.Manifest.ProjectId, sources, resources, new() { ProjectId = session.Manifest.ProjectId }, new(FilterJunkCandidates: filterJunk));
        int Count(AddressingTaskKind k) => full.Tasks.Count(t => t.Kind == k);
        int fullRequests = AddressingAiSurvey.CountRequests(full.Scenes, full.Settings, geminiOptions) + AddressingAiSurvey.CountRequests(full.Tasks, full.Settings, geminiOptions);
        Console.WriteLine($"FULL PLAN offline: chapters={report.Chapters} scenes={full.Scenes.Count} characters={Count(AddressingTaskKind.Character)} pairs={Count(AddressingTaskKind.Pair)} " +
            $"identities={Count(AddressingTaskKind.Identity)} tasks={full.Scenes.Count + full.Tasks.Count} requests={fullRequests} (Gemini packing; the old 6-per-request plan = " +
            $"{(full.Scenes.Count + 5) / 6 + (full.Tasks.Count + 5) / 6}) HTTP=0");
        // How binding are the caps? Pool sizes with the caps lifted (pairs from the step-1 report: best direction per unordered pair).
        var forward = report.Pairs.Where(p => p.Listener is not null).GroupBy(p => string.CompareOrdinal(p.Speaker, p.Listener!) < 0 ? (p.Speaker, p.Listener!) : (p.Listener!, p.Speaker))
            .Select(g => g.Max(p => p.Lines)).OrderByDescending(x => x).ToArray();
        var poolTasks = AddressingAiSurvey.PlanTasks(sources, names, new(MaximumPairs: 1000, MaximumCharacters: 1000));
        Console.WriteLine($"POOL pairs(best direction lines): >=3:{forward.Count(x => x >= 3)} >=5:{forward.Count(x => x >= 5)} >=10:{forward.Count(x => x >= 10)} >=20:{forward.Count(x => x >= 20)} >=50:{forward.Count(x => x >= 50)} " +
            $"chapters/100={report.Chapters / 100} top10=[{string.Join(",", forward.Take(10))}] 60th={(forward.Length > 59 ? forward[59] : 0)} 120th={(forward.Length > 119 ? forward[119] : 0)}; " +
            $"characters pool={poolTasks.Count(t => t.Kind == AddressingTaskKind.Character)} pair tasks pool={poolTasks.Count(t => t.Kind == AddressingTaskKind.Pair)}");
        var mentions = NarrativeReferenceSurvey.Analyze(sources, names, 1).Where(x => poolTasks.Any(t => t.Kind == AddressingTaskKind.Character && t.Subject == x.Name))
            .Select(x => x.NarrationMentions).OrderByDescending(x => x).ToArray();
        Console.WriteLine($"POOL character narration mentions by rank: " + string.Join(" ", new[] { 1, 5, 10, 20, 30, 40, 60, 80, 100, 150 }.Where(r => r <= mentions.Length).Select(r => $"#{r}={mentions[r - 1]}")) +
            $" sourceChars={sources.Sum(s => (long)s.OriginalText.Length)}");
        var dropped = AddressingAiSurvey.DroppedCandidates(session.Manifest.ProjectId, sources, resources);
        // Names only (never source text), in a UTF-8 file: the console code page garbles Chinese.
        await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-dropped-candidates.md"), "# Candidates dropped by the CPU pre-filter (names only)\n\n" +
            string.Join("\n", dropped.Select(d => $"- {d.Name} — {d.Reason}")) + "\n\n# Character tasks kept\n\n" +
            string.Join("\n", full.Tasks.Where(t => t.Kind == AddressingTaskKind.Character).Select(t => $"{t.Subject} he={t.He} she={t.She} " +
                $"pairs={full.Tasks.Count(p => p.Kind == AddressingTaskKind.Pair && (p.Subject == t.Subject || p.Listener == t.Subject))}")) + "\n", new UTF8Encoding(false));
        Console.WriteLine($"DROPPED candidates={dropped.Count} (extension={dropped.Count(d => d.Reason.StartsWith("extends"))}, no-evidence={dropped.Count(d => d.Reason.StartsWith("no "))})");
    }
    if (mockScenes)
    {
        // MOCK, not AI: a deterministic stand-in that alternates two participants and injects known faults, to run plan →
        // reply → verification → ledger → pair re-planning on real text. Its answers are NOT facts. HTTP=0.
        var mockOptions = new QuickTranslator.AiEditor.Core.Models.ModelOptions { Model = "mock-scenes", ContextSize = 65536, MaximumOutputTokens = 8192 };
        var mockLedger = new AddressingLedgerStore(session);
        var mockRun = await new AddressingAiEngine(new MockSceneClient(scenes)).RunAsync(mockLedger, scenes, mockOptions, maximumRequests: 1000);
        var turns = mockRun.State.SceneTurns();
        var replanned = AddressingAiSurvey.PlanTasks(sources, names, sceneTurns: turns);
        File.Copy(mockLedger.PathName, Path.Combine(outDir, "addressing-ledger.mock.json"), overwrite: true); // Never the live checkpoint.
        await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step2b-mock-result.md"), MockMarkdown(mockRun, scenes, tasks, replanned), new UTF8Encoding(false));
        var sceneProposals = mockRun.State.Proposals.Where(p => p.Kind == AddressingTaskKind.Scene).ToArray();
        Console.WriteLine($"MOCK scenes: requests={mockRun.RequestsSent} verifiedScenes={sceneProposals.Count(p => p.EvidenceValid)}/{sceneProposals.Length} " +
            $"usableTurns={turns.Count}/{sceneProposals.Sum(p => p.Turns.Count)} rejected={string.Join(",", sceneProposals.SelectMany(p => p.Turns).SelectMany(t => t.Issues).GroupBy(x => x).Select(g => g.Key + "=" + g.Count()))} " +
            $"pairsBefore={tasks.Count(t => t.Kind == AddressingTaskKind.Pair)} pairsAfter={replanned.Count(t => t.Kind == AddressingTaskKind.Pair)} HTTP=0");
        return 0;
    }
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
    // Scenes first: their verified turns (from earlier runs) decide which quotes count for each pair task.
    var before = await ledger.LoadAsync();
    var liveTasks = scenes.Concat(AddressingAiSurvey.PlanTasks(sources, names, sceneTurns: before.SceneTurns())).ToArray();
    var run = await new AddressingAiEngine(backend).RunAsync(ledger, liveTasks, options, maxRequests);
    File.Copy(ledger.PathName, Path.Combine(outDir, "addressing-ledger.v1.json"), overwrite: true);
    var thisRun = run.State.Attempts.Skip(before.Attempts.Count).ToArray();
    await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step2-result.md"), ResultMarkdown(run, thisRun), new UTF8Encoding(false));
    Console.WriteLine($"LIVE sent={run.RequestsSent} planned={run.Planned} stoppedBy={run.StoppedBy ?? "-"} proposals={run.State.Proposals.Count} " +
        $"verified={run.State.Proposals.Count(p => p.EvidenceValid)} thisRun: ok={thisRun.Count(a => a.Succeeded)} failed={thisRun.Count(a => !a.Succeeded)}" +
        (thisRun.Any(a => !a.Succeeded) ? " (" + string.Join(",", thisRun.Where(a => !a.Succeeded).GroupBy(a => a.ErrorCode).Select(g => g.Key + "=" + g.Count())) + ")" : "") +
        $" schemaIssues={thisRun.Sum(a => a.SchemaIssues.Count)}");
    return thisRun.Length > 0 && thisRun.All(a => a.Succeeded) ? 0 : 1;
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

static string ResultMarkdown(AddressingRunResult run, IReadOnlyList<AddressingAttempt> thisRun)
{
    var md = new StringBuilder("# Sổ xưng hô · Bước 2 · kết quả AI (chưa duyệt)\n\n");
    md.AppendLine($"Đã gửi {run.RequestsSent}/{run.Planned} request; dừng bởi: {run.StoppedBy ?? "—"}. Đề xuất chỉ là giả thuyết đã kiểm chứng cơ học, **chưa được duyệt**.").AppendLine();
    md.AppendLine("## Các request của lần chạy này").AppendLine();
    md.AppendLine("| # | Kết quả | Mã lỗi | Số nhiệm vụ | Token vào/ra | Lỗi cấu trúc (vị trí JSON) |").AppendLine("|---:|---|---|---:|---|---|");
    foreach (var (a, i) in thisRun.Select((a, i) => (a, i + 1)))
        md.AppendLine($"| {i} | {(a.Succeeded ? "✅" : "❌")} | {(a.ErrorCode.Length == 0 ? "—" : a.ErrorCode)} | {a.TaskIds.Count} | {a.InputTokens?.ToString() ?? "?"}/{a.OutputTokens?.ToString() ?? "?"} | " +
            $"{(a.SchemaIssues.Count == 0 ? "—" : string.Join("<br>", a.SchemaIssues.Select(x => x.Replace("|", "\\|"))))} |");
    md.AppendLine().AppendLine("## Đề xuất").AppendLine();
    md.AppendLine("| Loại | Chủ thể | Trạng thái | Kiểm chứng | Cách gọi | Lỗi |").AppendLine("|---|---|---|---|---|---|");
    foreach (var p in run.State.Proposals)
    {
        string terms = p.Kind == AddressingTaskKind.Scene
            ? $"{p.Turns.Count(t => t.IsUsable())}/{p.Turns.Count} lượt dùng được"
            : p.Kind == AddressingTaskKind.Pair
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
    md.AppendLine($"- Nhiệm vụ: {tasks.Count} ({tasks.Count(t => t.Kind == AddressingTaskKind.Pair)} cặp hai chiều, {tasks.Count(t => t.Kind == AddressingTaskKind.Character)} nhân vật)");
    md.AppendLine($"- Request dự kiến: {requests.Count}; ước tính input khoảng {requests.Sum(r => r.EstimatedInputTokens):N0} token (heuristic, không phải số đo).").AppendLine();
    md.AppendLine("## Biến thể tên (CPU, chờ AI + người duyệt)").AppendLine();
    foreach (var g in variants.Where(g => g.Variants.Count > 0 || g.SharedForms.Count > 0))
        md.AppendLine($"- **{g.FullName}**: {string.Join(", ", g.Variants.Select(v => $"{v.Form}×{v.Occurrences}"))}" +
            (g.SharedForms.Count > 0 ? $" · mơ hồ (nhiều chủ): {string.Join(", ", g.SharedForms)}" : ""));
    md.AppendLine().AppendLine("## Nhân vật · lời kể ngôi thứ 3").AppendLine();
    md.AppendLine("| Nhân vật | Mẫu | 他 | 她 | Giới tính theo nguyên văn | Chương |").AppendLine("|---|---:|---:|---:|---|---|");
    foreach (var t in tasks.Where(t => t.Kind == AddressingTaskKind.Character))
        md.AppendLine($"| {t.Subject} | {t.Samples.Count} | {t.He} | {t.She} | {t.SourceGender} | {t.FirstChapter}–{t.LastChapter} |");
    md.AppendLine().AppendLine("## Cặp · xưng hô trong thoại (hai chiều trong một nhiệm vụ)").AppendLine();
    md.AppendLine("| A ↔ B | Mẫu A → B | Mẫu B → A | Chương | Gom biến thể |").AppendLine("|---|---:|---:|---|---|");
    foreach (var t in tasks.Where(t => t.Kind == AddressingTaskKind.Pair))
        md.AppendLine($"| {t.Subject} ↔ {t.Listener} | {t.Samples.Count} | {t.ReverseSamples.Count} | {t.FirstChapter}–{t.LastChapter} | {(t.NameCandidates.Count == 0 ? "—" : string.Join(", ", t.NameCandidates.Select(v => v.Form)))} |");
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

static string Passage(AddressingTask task, int max)
{
    string text = string.Concat(task.Samples.Select(s => s.Lead + $"⟦{s.SampleId}⟧「{s.Text}」")) + task.Trailer;
    return (text.Length <= max ? text : text[..max] + "…").Replace("\n", " ¶ ").Replace("|", "\\|");
}

static string SceneMarkdown(IReadOnlyList<DialogueScene> all, IReadOnlyList<AddressingTask> scenes, IReadOnlyList<AddressingRequestPlan> requests)
{
    var md = new StringBuilder("# Sổ xưng hô · Bước 2b · cảnh hội thoại (CPU, chưa gọi AI)\n\n");
    int quotes = all.Sum(s => s.Quotes.Count), open = all.Sum(s => s.Quotes.Count(q => q.Line.SpeakerEvidence != SpeakerEvidence.Explicit));
    md.AppendLine("| Chỉ số | Giá trị |").AppendLine("|---|---:|");
    md.AppendLine($"| Cảnh hội thoại liền mạch (CPU) | {all.Count} |").AppendLine($"| Lời thoại trong các cảnh | {quotes} (CPU chưa gán người nói: {open}) |");
    md.AppendLine($"| Cảnh được chọn gửi AI (≥2 lời chưa gán, ≥2 người tham gia) | {scenes.Count} |");
    md.AppendLine($"| Lời thoại trong cảnh được chọn | {scenes.Sum(t => t.Samples.Count)} (CPU đã biết người nói: {scenes.Sum(t => t.Samples.Count(s => s.CpuSpeaker is not null))}) |");
    md.AppendLine($"| Chương có cảnh được chọn | {scenes.Select(t => t.FirstChapter).Distinct().Count()} |");
    md.AppendLine($"| Request dự kiến | {requests.Count} (ước tính input {requests.Sum(r => r.EstimatedInputTokens):N0} token, heuristic) |").AppendLine();
    md.AppendLine("## Cảnh được chọn (tối đa 15)").AppendLine();
    md.AppendLine("| Chương | Người tham gia | Lời thoại | CPU đã biết | Trích đoạn |").AppendLine("|---|---|---:|---:|---|");
    foreach (var t in scenes.Take(15))
        md.AppendLine($"| {t.FirstChapter} | {string.Join(", ", t.Participants)} | {t.Samples.Count} | {t.Samples.Count(s => s.CpuSpeaker is not null)} | {Passage(t, 160)} |");
    return md.ToString();
}

static string MockMarkdown(AddressingRunResult run, IReadOnlyList<AddressingTask> scenes, IReadOnlyList<AddressingTask> before, IReadOnlyList<AddressingTask> after)
{
    var md = new StringBuilder("# Sổ xưng hô · Bước 2b · chạy thử bằng MOCK (không phải AI)\n\n");
    md.AppendLine("> **MOCK**: câu trả lời do một bộ giả lập tất định tạo ra (luân phiên 2 người tham gia, giữ người nói CPU đã biết, cố ý cài lỗi: tên không có trong danh sách, từ không có trong câu). " +
        "Mục đích là chạy thử cả đường đi kế hoạch → trả lời → kiểm chứng → sổ → lập lại nhiệm vụ cặp trên văn bản thật. **Kết quả lượt nói KHÔNG phải dữ kiện.** HTTP = 0.").AppendLine();
    var proposals = run.State.Proposals.Where(p => p.Kind == AddressingTaskKind.Scene).ToArray();
    var turns = proposals.SelectMany(p => p.Turns).ToArray();
    md.AppendLine("| Chỉ số | Giá trị |").AppendLine("|---|---:|");
    md.AppendLine($"| Request mock | {run.RequestsSent}/{run.Planned} |");
    md.AppendLine($"| Cảnh qua kiểm chứng | {proposals.Count(p => p.EvidenceValid)}/{proposals.Length} |");
    md.AppendLine($"| Cảnh bị loại cả cảnh (quá nửa lượt sai) | {proposals.Count(p => p.Issues.Contains("TooManyRejectedTurns"))} |");
    md.AppendLine($"| Lượt dùng được làm bằng chứng | {run.State.SceneTurns().Count}/{turns.Length} |");
    md.AppendLine($"| Lượt trả lời \"không rõ\" | {turns.Count(t => t.Speaker.Length == 0)} |");
    foreach (var g in turns.SelectMany(t => t.Issues).GroupBy(x => x).OrderByDescending(g => g.Count())) md.AppendLine($"| Lượt bị loại: {g.Key} | {g.Count()} |");
    md.AppendLine($"| Nhiệm vụ cặp trước / sau khi có lượt nói theo cảnh | {before.Count(t => t.Kind == AddressingTaskKind.Pair)} / {after.Count(t => t.Kind == AddressingTaskKind.Pair)} |").AppendLine();
    var old = before.Where(t => t.Kind == AddressingTaskKind.Pair).Select(t => (t.Subject, t.Listener)).ToHashSet();
    md.AppendLine("## Cặp mới xuất hiện nhờ lượt nói theo cảnh (mock)").AppendLine();
    md.AppendLine("| Người nói → người nghe | Mẫu | Mẫu từ cảnh | Chương |").AppendLine("|---|---:|---:|---|");
    foreach (var t in after.Where(t => t.Kind == AddressingTaskKind.Pair && !old.Contains((t.Subject, t.Listener))))
        md.AppendLine($"| {t.Subject} → {t.Listener} | {t.Samples.Count} | {t.Samples.Count(s => s.FromScene)} | {t.FirstChapter}–{t.LastChapter} |");
    md.AppendLine().AppendLine("## Kiểm chứng từng lượt (5 cảnh đầu)").AppendLine();
    var byTask = scenes.ToDictionary(t => t.TaskId);
    foreach (var p in proposals.Take(5))
    {
        var task = byTask[p.TaskId];
        md.AppendLine($"### Ch.{task.FirstChapter} · {string.Join(", ", task.Participants)} · {(p.EvidenceValid ? "✅" : "❌")} {string.Join(", ", p.Issues)}").AppendLine();
        md.AppendLine("| Lượt | Mock: nói → nghe | Kết quả | Lời thoại |").AppendLine("|---|---|---|---|");
        foreach (var t in p.Turns)
        {
            var s = task.Samples.First(x => x.SampleId == t.QuoteId);
            string quote = (s.Text.Length <= 40 ? s.Text : s.Text[..40] + "…").Replace("|", "\\|").Replace("\n", " ");
            md.AppendLine($"| {t.QuoteId} | {(t.Speaker.Length == 0 ? "?" : t.Speaker)} → {(t.Listener.Length == 0 ? "?" : t.Listener)}{(s.CpuSpeaker is null ? "" : " (CPU: " + s.CpuSpeaker + ")")} | " +
                $"{(t.IsUsable() ? "dùng được" : t.Speaker.Length == 0 ? "không rõ" : "loại: " + string.Join(", ", t.Issues))} | 「{quote}」 |");
        }
        md.AppendLine();
    }
    return md.ToString();
}

/// <summary>
/// Deterministic stand-in for a model, for offline pipeline runs only: keeps the CPU's explicit speakers, alternates the
/// two participants at each paragraph break, answers "" in larger scenes, and injects known faults (an unlisted name on
/// every 13th turn, an unquoted term on every 17th) so verification is exercised on real text.
/// </summary>
sealed class MockSceneClient(IReadOnlyList<AddressingTask> tasks) : IJsonSchemaOllamaClient
{
    private readonly Dictionary<string, AddressingTask> _byId = tasks.ToDictionary(t => t.TaskId, StringComparer.Ordinal);
    private int _turn;
    public Task<OllamaCompletion> TranslateAsync(string prompt, QuickTranslator.AiEditor.Core.Models.ModelOptions options, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, QuickTranslator.AiEditor.Core.Models.ModelOptions options, JsonElement schema, CancellationToken ct = default)
    {
        var scenes = new JsonArray();
        var inPrompt = AddressingAiSurvey.TasksInPrompt(prompt, _byId.Values);
        foreach (var (task, index) in inPrompt.Select((t, i) => (t, i)).Where(x => x.t.Kind == AddressingTaskKind.Scene))
        {
            var turns = new JsonArray(); string? previous = null;
            foreach (var s in task.Samples)
            {
                string speaker = s.CpuSpeaker ?? (task.Participants.Count != 2 ? "" :
                    previous is null ? task.Participants[0] : s.Lead.Contains('\n') ? Other(task, previous) : previous);
                if (speaker.Length > 0) previous = speaker;
                string listener = speaker.Length > 0 && task.Participants.Count == 2 ? Other(task, speaker) : "";
                var address = new JsonArray();
                _turn++;
                if (_turn % 13 == 0) speaker = "路人甲";
                else if (_turn % 17 == 0) address.Add("不存在的称呼");
                turns.Add(new JsonObject
                {
                    ["quote_id"] = s.SampleId, ["speaker"] = speaker, ["listener"] = listener,
                    ["self_zh"] = new JsonArray(s.Text.Contains('我') ? ["我"] : Array.Empty<JsonNode>()), ["address_zh"] = address
                });
            }
            scenes.Add(new JsonObject { ["task_id"] = "T" + (index + 1), ["turns"] = turns, ["reason"] = "MOCK" }); // Short id, as the prompt asks.
        }
        string json = new JsonObject { ["pairs"] = new JsonArray(), ["characters"] = new JsonArray(), ["scenes"] = scenes, ["identities"] = new JsonArray() }.ToJsonString();
        return Task.FromResult(new OllamaCompletion(json, true, "stop", new QuickTranslator.AiEditor.Core.Models.GenerationMetrics()));
    }
    private static string Other(AddressingTask task, string name) => task.Participants[0] == name ? task.Participants[1] : task.Participants[0];
}
