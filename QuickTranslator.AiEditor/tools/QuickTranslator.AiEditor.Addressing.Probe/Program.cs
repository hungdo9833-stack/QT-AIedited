using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Projects;

// Addressing Ledger step 1 probe. CPU only: never calls an AI provider and never writes to the given
// Project. The Project folder is copied to %TEMP% first; only the copy is opened, and it is removed after.
string? project = null, outDir = null; int samples = 12, minimumMentions = 3;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" when i + 1 < args.Length: project = args[++i]; break;
        case "--out" when i + 1 < args.Length: outDir = args[++i]; break;
        case "--samples" when i + 1 < args.Length && int.TryParse(args[++i], out int s) && s is > 0 and <= 50: samples = s; break;
        case "--min-mentions" when i + 1 < args.Length && int.TryParse(args[++i], out int m) && m > 0: minimumMentions = m; break;
        default:
            Console.WriteLine("Usage: --project <path\\Name.qtproj> --out <report folder> [--samples 12] [--min-mentions 3]");
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
    var census = CharacterMentionCensus.Scan(session.Manifest.ProjectId, 0, sources, resources,
        new BookIntelligenceState { ProjectId = session.Manifest.ProjectId });
    var names = census.Candidates
        .Where(c => c.Category is MentionCandidateCategory.PersonCandidate or MentionCandidateCategory.AliasCandidate && c.Frequency >= minimumMentions)
        .Select(c => c.ChineseName)
        .Concat(resources.Characters.Profiles.SelectMany(p => new[] { p.ChineseName }.Concat(p.Aliases)))
        .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
    var report = AddressingDialogueSurvey.Analyze(sources, names, samples);

    Directory.CreateDirectory(outDir);
    var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new JsonStringEnumConverter() } };
    await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step1.json"), JsonSerializer.Serialize(report, json), new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(outDir, "addressing-step1.md"), Markdown(session.Manifest.VietnameseTitle, report), new UTF8Encoding(false));
    Console.WriteLine($"OK chapters={report.Chapters} dialogue={report.DialogueLines} explicitSpeaker={report.ExplicitSpeakerLines} " +
        $"pronoun={report.PronounSpeakerLines} unattributed={report.UnattributedLines} listenerKnown={report.ListenerKnownLines} " +
        $"names={report.CandidateNames.Count} pairs={report.Pairs.Count} -> {Path.GetFullPath(outDir)}");
    return 0;
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
