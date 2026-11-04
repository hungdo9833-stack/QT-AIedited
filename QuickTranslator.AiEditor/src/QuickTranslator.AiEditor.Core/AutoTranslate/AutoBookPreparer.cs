using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Importing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.WebImport;

namespace QuickTranslator.AiEditor.Core.AutoTranslate;

public sealed record AutoBookStatus(string Stage, string Detail, string? Title = null);

/// <summary>A project made earlier from the same link.</summary>
/// <param name="SourceChapters">Chapters the site lists now; null when the site could not be read.</param>
public sealed record AutoBookExisting(string ManifestPath, string Title, int Chapters, int? SourceChapters, bool Surveyed)
{
    /// <summary>Surveyed and holding every chapter: translation can simply continue in it.</summary>
    public bool Complete => Surveyed && (SourceChapters is null || Chapters >= SourceChapters);
}

/// <summary>A book whose chapters are all downloaded and joined into one import text.</summary>
public sealed record AutoBookDownload(Uri Url, string Title, string Text, string WorkDirectory);

public sealed record AutoBookPrepared(ProjectSession Session, string Title, int ImportedChapters,
    CodexSurveyImportResult Survey, string Summary)
{
    public string ManifestPath => Session.ManifestPath;
}

/// <summary>The site walled off later chapters. No project was made; the same link resumes from the cached pages.</summary>
public sealed class AutoBookPausedException(string title, int downloaded, int total, string nextChapter, WebImportBlockReason reason)
    : Exception($"Đã tải {downloaded}/{total} chương, dừng ở {nextChapter}: " + (reason == WebImportBlockReason.RequiresLogin
        ? "web yêu cầu đăng nhập."
        : "web chặn vì tải quá nhanh. Chạy lại link này sau (có thể tăng thời gian nghỉ) để tải tiếp các chương còn thiếu."))
{
    public string Title { get; } = title;
    public int Downloaded { get; } = downloaded;
    public int Total { get; } = total;
    public WebImportBlockReason Reason { get; } = reason;
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
        CodexSurveyOptions codex, IProgress<AutoBookStatus>? status = null, CancellationToken cancellationToken = default,
        IProgress<ContentFlagReport>? contentFlags = null)
    {
        AutoBookDownload download = await DownloadAsync(url, downloadDelay, status, cancellationToken).ConfigureAwait(false);
        return await PrepareAsync(download, projectsDirectory, codex, status, cancellationToken, contentFlags).ConfigureAwait(false);
    }

    /// <summary>
    /// The best project already made from <paramref name="url"/> among <paramref name="manifestPaths"/> (a complete one
    /// first, then the one with most chapters), so the same link continues it instead of surveying again.
    /// </summary>
    public async Task<AutoBookExisting?> FindExistingAsync(Uri url, IEnumerable<string> manifestPaths, TimeSpan downloadDelay,
        CancellationToken cancellationToken = default)
    {
        AutoBookExisting? best = null;
        int? sourceChapters = null;
        bool counted = false;
        foreach (string path in manifestPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ProjectManifest manifest;
            try { manifest = await ProjectRepository.ReadManifestAsync(path, cancellationToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
            if (!SameLink(manifest.SourceUrl, url)) continue;
            bool surveyed;
            try
            {
                ProjectResources resources = await new ProjectResourceStore(new ProjectSession(path, manifest))
                    .LoadAsync(cancellationToken).ConfigureAwait(false);
                surveyed = resources.Characters.Profiles.Count > 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { surveyed = false; }
            if (!counted)
            {
                sourceChapters = await CountSourceChaptersAsync(url, downloadDelay, cancellationToken).ConfigureAwait(false);
                counted = true;
            }
            var found = new AutoBookExisting(path, manifest.VietnameseTitle,
                manifest.Chapters.Count(entry => entry.DeletedAt is null), sourceChapters, surveyed);
            if (best is null || found.Complete && !best.Complete || found.Complete == best.Complete && found.Chapters > best.Chapters)
                best = found;
        }
        return best;
    }

    private static bool SameLink(string? saved, Uri url) =>
        Uri.TryCreate(saved, UriKind.Absolute, out Uri? savedUri) &&
        string.Equals(savedUri.AbsoluteUri.TrimEnd('/'), url.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Chapters the site's table of contents lists now; null when it cannot be read (offline, walled).</summary>
    private async Task<int?> CountSourceChaptersAsync(Uri url, TimeSpan downloadDelay, CancellationToken cancellationToken)
    {
        if (url.IsFile) return null;
        IWebNovelImporter importer = importerFactory(new WebImportOptions { DelayBetweenRequests = downloadDelay, CacheDirectory = PagesDirectory(url) });
        try
        {
            WebBookAnalysis analysis = await importer.AnalyzeAsync(url, cancellationToken).ConfigureAwait(false);
            // A book split into pages (52shuku) lists pages, not chapters: their count cannot be compared with the project's.
            return analysis.ContinuousPages ? null : analysis.Chapters.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
        finally { (importer as IDisposable)?.Dispose(); }
    }

    private string WorkDirectory(Uri url) =>
        Path.Combine(WorkRoot, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri)))[..16]);

    private string PagesDirectory(Uri url) => Path.Combine(WorkDirectory(url), "pages");

    /// <summary>
    /// Downloads every chapter. Kept apart from <see cref="PrepareAsync(AutoBookDownload,string,CodexSurveyOptions,IProgress{AutoBookStatus},CancellationToken,IProgress{ContentFlagReport})"/>
    /// so the next book can download while Codex surveys this one.
    /// </summary>
    /// <exception cref="AutoBookPausedException">The site walled off later chapters.</exception>
    public async Task<AutoBookDownload> DownloadAsync(Uri url, TimeSpan downloadDelay, IProgress<AutoBookStatus>? status = null,
        CancellationToken cancellationToken = default)
    {
        if (url.IsFile) return await LoadFileAsync(url, status, cancellationToken).ConfigureAwait(false);
        string work = WorkDirectory(url), pages = PagesDirectory(url);
        Directory.CreateDirectory(pages);
        IWebNovelImporter importer = importerFactory(new WebImportOptions { DelayBetweenRequests = downloadDelay, CacheDirectory = pages });
        string text, title;
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
            // A survey of part of a book misses most characters, so nothing is created until every chapter is in. The pages
            // already downloaded stay in the cache: running the same link again only fetches the rest.
            catch (WebImportBlockedException ex) when (ex.Reason is WebImportBlockReason.RequiresLogin or WebImportBlockReason.RateLimited)
            {
                throw new AutoBookPausedException(title, chapters.Count, analysis.Chapters.Count,
                    analysis.Chapters[chapters.Count].Title, ex.Reason);
            }
            if (chapters.Count == 0) throw new InvalidDataException("Không tải được chương nào có nội dung.");
            // Sites that restart numbering per volume (第1章 again) would collide in the project: number by TOC order instead.
            text = importer.ComposeImportText(chapters, keepSiteOrder: analysis.NumberingConflict);
            if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Nội dung tải về trống.");
        }
        finally { (importer as IDisposable)?.Dispose(); }
        return new(url, title, text, work);
    }

    /// <summary>A local TXT book (file:// link): read with encoding detection; the file name is the book's title.</summary>
    private async Task<AutoBookDownload> LoadFileAsync(Uri file, IProgress<AutoBookStatus>? status, CancellationToken cancellationToken)
    {
        string path = file.LocalPath;
        string title = Path.GetFileNameWithoutExtension(path).Trim();
        status?.Report(new("Đọc file TXT", path, title));
        if (!File.Exists(path)) throw new FileNotFoundException("Không tìm thấy file TXT: " + path, path);
        RawSourceSnapshot source = await RawSourceReader.ReadFileAsync(path, SourceTextEncoding.Auto, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(source.Text)) throw new InvalidDataException("File TXT trống: " + path);
        return new(file, title.Length > 0 ? title : "Truyện", source.Text, WorkDirectory(file));
    }

    /// <summary>Creates the project from a finished download, surveys it with Codex and saves the resources.</summary>
    public async Task<AutoBookPrepared> PrepareAsync(AutoBookDownload download, string projectsDirectory,
        CodexSurveyOptions codex, IProgress<AutoBookStatus>? status = null, CancellationToken cancellationToken = default,
        IProgress<ContentFlagReport>? contentFlags = null)
    {
        (Uri url, string title, string text, string work) = download;
        status?.Report(new("Tạo dự án", ""));
        ProjectSession session = await repository.CreateAsync(NewProjectFolder(projectsDirectory, title), title, cancellationToken)
            .ConfigureAwait(false);
        session.Manifest.ChineseTitle = title;
        session.Manifest.SourceUrl = url.AbsoluteUri;
        await repository.SaveManifestAsync(session, cancellationToken).ConfigureAwait(false);
        string rawPath = session.Resolve("Cache/web-import/" + SafeName(title) + ".txt");
        Directory.CreateDirectory(Path.GetDirectoryName(rawPath)!);
        await File.WriteAllTextAsync(rawPath, text, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        RawSourceSnapshot source = await RawSourceReader.ReadFileAsync(rawPath, SourceTextEncoding.Auto, cancellationToken)
            .ConfigureAwait(false);
        ImportPreviewEditor editor = await Task.Run(() => ImportPreviewEditor.Create(new ChapterDetector().Detect(source)),
            cancellationToken).ConfigureAwait(false);
        if (!editor.Current.ImportableChapters.Any())
            throw new InvalidDataException("Không tách được chương nào từ nội dung tải về (kiểm tra dòng tiêu đề chương trong " + rawPath + ").");
        // TXT books (and sites the web check missed) that restart 第1章 per volume: number on through the book.
        editor.RenumberAcrossVolumes();
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
        CodexSurveyImportResult survey;
        try
        {
            survey = await surveyFactory(codex).RunAsync(text, Path.Combine(work, "codex"),
                session.Manifest.ProjectId, title, codexProgress, cancellationToken, contentFlags).ConfigureAwait(false);
        }
        catch (ContentStopException stop)
        {
            await SaveContentFlagsAsync(session, stop.Report, cancellationToken).ConfigureAwait(false);
            throw;
        }
        if (survey.ContentFlags.HasFlags)
            await SaveContentFlagsAsync(session, survey.ContentFlags, cancellationToken).ConfigureAwait(false);
        var store = new ProjectResourceStore(session);
        ProjectResources resources = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        resources.Characters = survey.Characters;
        resources.Terms = survey.Terms.ToList();
        await store.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        if (survey.Book is CodexBookInfo book)
        {
            ApplyBookInfo(session.Manifest, book);
            await repository.SaveManifestAsync(session, cancellationToken).ConfigureAwait(false);
            title = book.VietnameseTitle;
        }
        string summary = (survey.ContentFlags.HasFlags ? $"⚠ {survey.ContentFlags.Flags.Count} cảnh báo nội dung. " : "") +
            (survey.Book is { } info ? $"“{info.VietnameseTitle}” · {string.Join(", ", info.Genres)} · " : "") +
            $"{report.ImportedCount:N0} chương, {titleNote}, {survey.Characters.Profiles.Count} nhân vật, " +
            $"{survey.Characters.Profiles.Sum(p => p.Relationships.Count)} quan hệ, {survey.Terms.Count} thuật ngữ" +
            (survey.Warnings.Count > 0 ? $"; {survey.Warnings.Count} ghi chú: {survey.Warnings[0]}" : "");
        return new(session, title, report.ImportedCount, survey, summary);
    }

    /// <summary>Keeps the content-safety findings with the project (Settings/content-flags.json) for later review.</summary>
    public static async Task SaveContentFlagsAsync(ProjectSession session, ContentFlagReport report, CancellationToken cancellationToken)
    {
        string path = session.Resolve("Settings/" + ContentFlagFile.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(new { report.MustStop, report.Flags },
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames the project to the translated title and fills the blurb and genres used when publishing.</summary>
    public static void ApplyBookInfo(ProjectManifest manifest, CodexBookInfo book)
    {
        if (string.IsNullOrWhiteSpace(manifest.ChineseTitle)) manifest.ChineseTitle = manifest.VietnameseTitle;
        manifest.VietnameseTitle = book.VietnameseTitle;
        if (book.Synopsis.Length > 0) manifest.Description = book.Synopsis;
        if (book.Genres.Count > 0) manifest.Genres = book.Genres.ToList();
        manifest.UpdatedAt = DateTimeOffset.UtcNow;
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
