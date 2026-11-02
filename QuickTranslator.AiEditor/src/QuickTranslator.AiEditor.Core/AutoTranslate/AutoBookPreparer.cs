using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Core.AutoTranslate;

public sealed record AutoBookStatus(string Stage, string Detail, string? Title = null);

public sealed record AutoBookPrepared(ProjectSession Session, string Title, int ImportedChapters,
    CodexSurveyImportResult Survey, string Summary)
{
    public string ManifestPath => Session.ManifestPath;
}

/// <summary>
/// Prepares one book from a web link without any UI: downloads every chapter, creates a new project holding them, and
/// saves the Codex-surveyed characters and glossary as the project's resources. Translation is left to the caller.
/// </summary>
public sealed class AutoBookPreparer(ProjectRepository repository, Func<WebImportOptions, IWebNovelImporter> importerFactory,
    Func<CodexSurveyOptions, CodexSurveyRunner> surveyFactory, BulkImportChapterDefaults defaults)
{
    public string WorkRoot { get; init; } = CodexSurveyRunner.DefaultWorkRoot;
    /// <summary>VietPhrase backend that names every chapter before import; null keeps the Chinese titles.</summary>
    public IChapterTitleTranslationBackend? TitleBackend { get; init; }

    public async Task<AutoBookPrepared> PrepareAsync(Uri url, string projectsDirectory, TimeSpan downloadDelay,
        CodexSurveyOptions codex, IProgress<AutoBookStatus>? status = null, CancellationToken cancellationToken = default)
    {
        string work = Path.Combine(WorkRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..16]);
        string pages = Path.Combine(work, "pages");
        Directory.CreateDirectory(pages);
        IWebNovelImporter importer = importerFactory(new WebImportOptions { DelayBetweenRequests = downloadDelay, CacheDirectory = pages });
        string text, title;
        string? missingNote = null;
        try
        {
            status?.Report(new("Đọc mục lục", ""));
            WebBookAnalysis analysis = await importer.AnalyzeAsync(url, cancellationToken).ConfigureAwait(false);
            if (analysis.Chapters.Count == 0) throw new InvalidDataException("Không tìm thấy danh sách chương.");
            title = string.IsNullOrWhiteSpace(analysis.BookTitle) ? url.Host : analysis.BookTitle.Trim();
            status?.Report(new("Tải chương", $"0/{analysis.Chapters.Count}", title));
            var chapters = new List<WebChapterContent>();
            var progress = new Progress<WebImportProgress>(value =>
                status?.Report(new("Tải chương", $"{value.Completed}/{value.Total} · {value.CurrentTitle}")));
            try
            {
                await foreach (WebChapterContent chapter in importer.FetchChaptersAsync(analysis.Chapters, progress, cancellationToken)
                                   .WithCancellation(cancellationToken).ConfigureAwait(false))
                    chapters.Add(chapter);
            }
            // Later chapters sit behind the same wall: keep the complete chapters already downloaded and say what is missing.
            catch (WebImportBlockedException ex) when (ex.Reason is WebImportBlockReason.RequiresLogin or WebImportBlockReason.RateLimited &&
                                                       chapters.Count > 0)
            {
                missingNote = $"thiếu chương {analysis.Chapters[chapters.Count].Title} trở đi ({analysis.Chapters.Count - chapters.Count} chương): " +
                    (ex.Reason == WebImportBlockReason.RequiresLogin ? "web yêu cầu đăng nhập"
                        : "web chặn vì tải quá nhanh, hãy chạy lại sau với thời gian nghỉ lớn hơn");
            }
            if (chapters.Count == 0) throw new InvalidDataException("Không tải được chương nào có nội dung.");
            // Sites that restart numbering per volume (第1章 again) would collide in the project: number by TOC order instead.
            text = importer.ComposeImportText(chapters, keepSiteOrder: analysis.NumberingConflict);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Nội dung tải về trống.");
        }
        finally { (importer as IDisposable)?.Dispose(); }

        status?.Report(new("Tạo dự án", ""));
        ProjectSession session = await repository.CreateAsync(NewProjectFolder(projectsDirectory, title), title, cancellationToken)
            .ConfigureAwait(false);
        string rawPath = session.Resolve("Cache/web-import/" + SafeName(title) + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
        await File.WriteAllTextAsync(rawPath, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        RawSourceSnapshot source = await RawSourceReader.ReadFileAsync(rawPath, SourceTextEncoding.Auto, cancellationToken)
            .ConfigureAwait(false);
        ImportPreviewEditor editor = await Task.Run(() => ImportPreviewEditor.Create(new ChapterDetector().Detect(source)),
            cancellationToken).ConfigureAwait(false);
        string titleNote = await TranslateTitlesAsync(editor, session.Manifest.ProjectId, status, cancellationToken).ConfigureAwait(false);
        ImportPreviewPlan plan = editor.Current;
        BulkImportReport report;
        try
        {
            report = await new SafeBulkImportService(repository).ImportAsync(session, plan, defaults,
                new BulkImportOptions(), null, cancellationToken).ConfigureAwait(false);
        }
        catch (BulkImportValidationException ex)
        {
            throw new InvalidDataException(ex.Message + " " + string.Join("; ", ex.Issues.Take(5)) +
                (ex.Issues.Count > 5 ? $" (+{ex.Issues.Count - 5})" : ""), ex);
        }
        if (report.ImportedCount == 0) throw new InvalidDataException(report.Error ?? "Không nhập được chương nào vào dự án.");
        status?.Report(new("Codex khảo sát", $"Đã nhập {report.ImportedCount:N0} chương" + (report.Completed ? "." : $"; lỗi: {report.Error}")));

        var codexProgress = new Progress<string>(line =>
            status?.Report(new("Codex khảo sát", line.Length > 300 ? line[..300] + "…" : line)));
        CodexSurveyImportResult survey = await surveyFactory(codex).RunAsync(text, Path.Combine(work, "codex"),
            session.Manifest.ProjectId, title, codexProgress, cancellationToken).ConfigureAwait(false);
        var store = new ProjectResourceStore(session);
        ProjectResources resources = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        resources.Characters = survey.Characters;
        resources.Terms = survey.Terms.ToList();
        await store.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        string summary = (missingNote is null ? "" : "⚠ " + missingNote + ". ") +
            $"{report.ImportedCount:N0} chương, {titleNote}, {survey.Characters.Profiles.Count} nhân vật, " +
            $"{survey.Characters.Profiles.Sum(p => p.Relationships.Count)} quan hệ, {survey.Terms.Count} thuật ngữ" +
            (survey.Warnings.Count > 0 ? $"; {survey.Warnings.Count} mục bị bỏ: {survey.Warnings[0]}" : "");
        return new(session, title, report.ImportedCount, survey, summary);
    }

    private async Task<string> TranslateTitlesAsync(ImportPreviewEditor editor, Guid projectId, IProgress<AutoBookStatus>? status,
        CancellationToken cancellationToken)
    {
        if (TitleBackend is null) return "tiêu đề giữ tiếng Trung (chưa nạp Data VietPhrase)";
        ImportPreviewItem[] chapters = editor.Current.ImportableChapters.ToArray();
        var draft = ChapterTitleTranslationDraft.Create(projectId, "import:" + editor.Current.Source.RawSha256,
            ChapterTitleTranslationMethod.VietPhrase,
            chapters.Select(item => new ChapterTitleTranslationSource(item.Id, item.Number, item.Title, item.VietnameseTitle ?? "")));
        var progress = new Progress<ChapterTitleTranslationProgress>(value =>
            status?.Report(new("Dịch tiêu đề", $"{value.Completed:N0}/{value.Total:N0} tiêu đề · {value.Failed:N0} lỗi")));
        await new ChapterTitleTranslationService().TranslateAsync(draft, TitleBackend, new ModelOptions(),
            progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        editor.ApplyVietnameseTitles(draft.Items.ToDictionary(item => item.Id, item =>
            !string.IsNullOrWhiteSpace(item.VietnameseTitle) ? item.VietnameseTitle : FallbackTitle(item.ChapterNumber, item.ChineseTitle)));
        int failed = draft.Items.Count(item => item.Status == ChapterTitleTranslationStatus.Failed);
        return $"tiêu đề dịch VietPhrase" + (failed > 0 ? $" ({failed} lỗi dùng tên dự phòng)" : "");
    }

    private static string FallbackTitle(int? number, string originalTitle)
    {
        string trimmed = originalTitle.Trim();
        if (number is int n && !trimmed.StartsWith("Chương ", StringComparison.OrdinalIgnoreCase))
            return string.IsNullOrWhiteSpace(trimmed) ? $"Chương {n}" : $"Chương {n}: {trimmed}";
        return string.IsNullOrWhiteSpace(trimmed) ? "Chương mới" : trimmed;
    }

    private static string NewProjectFolder(string parent, string title)
    {
        string name = SafeName(title);
        string folder = Path.Combine(parent, name);
        for (int suffix = 2; Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(); suffix++)
            folder = Path.Combine(parent, $"{name} ({suffix})");
        return folder;
    }

    private static string SafeName(string value)
    {
        string name = string.Concat(value.Trim().Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x)).TrimEnd('.');
        return string.IsNullOrWhiteSpace(name) ? "QuickTranslator-Project" : name;
    }
}
