using System.Runtime.CompilerServices;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Projects;

public enum PublicationLanguage { Vietnamese, Chinese }

public sealed record ProjectPublicationOptions(
    bool IncludeProjectTitle = true,
    bool IncludeChapterTitles = true,
    bool UseUtf8Bom = false,
    bool AllowIncomplete = false,
    PublicationLanguage Language = PublicationLanguage.Vietnamese);

public sealed record ProjectPublicationIssueChapter(
    Guid ChapterId,
    int Number,
    string Title,
    bool IsIncomplete,
    bool NeedsInspection)
{
    public string Label => $"Chương {Number:0000} · {Title} ({(IsIncomplete ? "Chưa hoàn tất" : "Cần kiểm tra")})";
    public override string ToString() => Label;
}

public sealed record ProjectPublicationInspection(
    string ProjectTitle,
    string ProjectRevision,
    int TotalChapterCount,
    int IncompleteChapterCount,
    int InspectionChapterCount,
    IReadOnlyList<ProjectPublicationIssueChapter>? IssueChapters = null,
    IReadOnlyList<int>? ChapterNumbers = null,
    int? FromNumber = null,
    int? ToNumber = null,
    PublicationLanguage Language = PublicationLanguage.Vietnamese)
{
    public bool RequiresConfirmation => IncompleteChapterCount > 0 || InspectionChapterCount > 0;
    public bool IsRange => FromNumber is not null || ToNumber is not null;

    /// <summary>
    /// The same inspection limited to chapter numbers <paramref name="from"/>..<paramref name="to"/> (inclusive; null =
    /// open end). Counts, issues and the chapters exported all follow the range. Call it on the whole-project inspection.
    /// </summary>
    public ProjectPublicationInspection WithRange(int? from, int? to)
    {
        if (from is null && to is null) return this;
        if (from > to) throw new ArgumentException("Chương bắt đầu phải nhỏ hơn hoặc bằng chương kết thúc.");
        IReadOnlyList<int> numbers = ChapterNumbers
            ?? throw new InvalidOperationException("Kết quả kiểm tra không có danh sách số chương; hãy kiểm tra lại.");
        bool Contains(int number) => number >= (from ?? int.MinValue) && number <= (to ?? int.MaxValue);
        ProjectPublicationIssueChapter[] issues = (IssueChapters ?? []).Where(issue => Contains(issue.Number)).ToArray();
        return this with
        {
            TotalChapterCount = numbers.Count(Contains),
            IncompleteChapterCount = issues.Count(issue => issue.IsIncomplete),
            InspectionChapterCount = issues.Count(issue => issue.NeedsInspection),
            IssueChapters = issues,
            FromNumber = from,
            ToNumber = to
        };
    }
}

public sealed record ProjectPublicationChapter(
    Guid ChapterId,
    int Number,
    string Title,
    string Text,
    bool IsComplete,
    bool NeedsInspection);

public sealed record ProjectPublicationProgress(int CompletedChapterCount, int TotalChapterCount, string ChapterTitle);

public sealed record ProjectPublicationReport(
    string OutputPath,
    int ExportedChapterCount,
    int IncompleteChapterCount,
    int InspectionChapterCount);

/// <summary>
/// Builds a read-only publication view from the manifest-backed catalog. Chapter bodies are loaded one at a time,
/// and Vietnamese text comes from ChapterTranslationComposer, shared with Reader and chapter TXT export.
/// Chinese publications use the original chapter text without translation completeness checks.
/// </summary>
public sealed partial class ProjectPublicationService
{
    private const string ChapterSeparator = "\n\n\n";
    private readonly ProjectRepository _repository;
    private readonly ProjectCatalog _catalog;

    public ProjectPublicationService(ProjectRepository repository, ProjectCatalog catalog)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public async Task<ProjectPublicationInspection> InspectAsync(ProjectSession session,
        IProgress<ProjectPublicationProgress>? progress = null, CancellationToken cancellationToken = default,
        PublicationLanguage language = PublicationLanguage.Vietnamese)
    {
        ArgumentNullException.ThrowIfNull(session);
        PublicationSnapshot snapshot = Capture(session, language: language);
        int incomplete = 0, inspection = 0, completed = 0;
        var issueChapters = new List<ProjectPublicationIssueChapter>();
        var numbers = new List<int>(snapshot.Chapters.Count);
        await foreach (ProjectPublicationChapter chapter in ReadSnapshotAsync(session, snapshot, progress, cancellationToken)
                           .ConfigureAwait(false))
        {
            if (!chapter.IsComplete) incomplete++;
            if (chapter.NeedsInspection) inspection++;
            if (!chapter.IsComplete || chapter.NeedsInspection)
                issueChapters.Add(new(chapter.ChapterId, chapter.Number, chapter.Title, !chapter.IsComplete, chapter.NeedsInspection));
            numbers.Add(chapter.Number);
            completed++;
        }
        return new(snapshot.ProjectTitle, snapshot.Revision, completed, incomplete, inspection, issueChapters, numbers,
            Language: language);
    }

    public async Task<ProjectPublicationReport> ExportTextAsync(ProjectSession session, string path,
        ProjectPublicationOptions options, IProgress<ProjectPublicationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(options);
        ProjectPublicationInspection inspection = await InspectAsync(session, cancellationToken: cancellationToken,
                language: options.Language)
            .ConfigureAwait(false);
        return await ExportTextAsync(session, inspection, path, options, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectPublicationReport> ExportTextAsync(ProjectSession session,
        ProjectPublicationInspection inspection, string path, ProjectPublicationOptions options,
        IProgress<ProjectPublicationProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Đường dẫn xuất bản không được để trống.", nameof(path));
        EnsureAllowed(inspection, options);
        PublicationSnapshot snapshot = Capture(session, inspection.ProjectRevision, inspection.FromNumber, inspection.ToNumber,
            options.Language);
        string outputPath = ValidateOutputPath(session, path);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        string temporaryPath = outputPath + ".publication-" + Guid.NewGuid().ToString("N") + ".tmp";
        int exported = 0;
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(options.UseUtf8Bom), 64 * 1024,
                             leaveOpen: true))
            {
                bool hasContent = false;
                if (options.IncludeProjectTitle)
                {
                    await writer.WriteAsync(snapshot.ProjectTitle.AsMemory(), cancellationToken).ConfigureAwait(false);
                    hasContent = true;
                }

                await foreach (ProjectPublicationChapter chapter in ReadSnapshotAsync(session, snapshot, progress,
                                   cancellationToken).ConfigureAwait(false))
                {
                    if (hasContent) await writer.WriteAsync(ChapterSeparator.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (options.IncludeChapterTitles)
                    {
                        await writer.WriteAsync(chapter.Title.AsMemory(), cancellationToken).ConfigureAwait(false);
                        await writer.WriteAsync("\n\n".AsMemory(), cancellationToken).ConfigureAwait(false);
                    }
                    await writer.WriteAsync(chapter.Text.AsMemory(), cancellationToken).ConfigureAwait(false);
                    hasContent = true;
                    exported++;
                }
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            EnsureRevision(session, snapshot.Revision);
            CommitTemporaryFile(temporaryPath, outputPath);
            return new(outputPath, exported,
                options.Language == PublicationLanguage.Chinese ? 0 : inspection.IncompleteChapterCount,
                options.Language == PublicationLanguage.Chinese ? 0 : inspection.InspectionChapterCount);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { System.Diagnostics.Trace.TraceError($"Không dọn được file xuất tạm {temporaryPath}: {ex}"); }
        }
    }

    public async IAsyncEnumerable<ProjectPublicationChapter> ReadChaptersAsync(ProjectSession session,
        string expectedRevision, IProgress<ProjectPublicationProgress>? progress = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default, int? fromNumber = null, int? toNumber = null,
        PublicationLanguage language = PublicationLanguage.Vietnamese)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(expectedRevision)) throw new ArgumentException("Thiếu revision Project.", nameof(expectedRevision));
        PublicationSnapshot snapshot = Capture(session, expectedRevision, fromNumber, toNumber, language);
        await foreach (ProjectPublicationChapter chapter in ReadSnapshotAsync(session, snapshot, progress, cancellationToken)
                           .ConfigureAwait(false))
            yield return chapter;
    }

    public void EnsureRevision(ProjectSession session, string expectedRevision)
    {
        string current = _catalog.GetSnapshot(session).Revision;
        if (!string.Equals(current, expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Project đã thay đổi trong lúc xuất bản. File đích được giữ nguyên; hãy thử lại để lấy bản mới nhất.");
    }

    public static string ValidateOutputPath(ProjectSession session, string path)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Đường dẫn xuất bản không được để trống.", nameof(path));
        string outputPath = Path.GetFullPath(path);
        string projectRoot = Path.GetFullPath(session.RootDirectory).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (outputPath.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hãy lưu file xuất bản bên ngoài thư mục dữ liệu Project để tránh ghi đè manifest, chapter hoặc tài nguyên truyện.");
        return outputPath;
    }

    private async IAsyncEnumerable<ProjectPublicationChapter> ReadSnapshotAsync(ProjectSession session,
        PublicationSnapshot snapshot, IProgress<ProjectPublicationProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (int index = 0; index < snapshot.Chapters.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureRevision(session, snapshot.Revision);
            PublicationEntry entry = snapshot.Chapters[index];
            var loaded = await _repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken).ConfigureAwait(false);
            if (snapshot.Language == PublicationLanguage.Chinese)
            {
                string chineseTitle = string.IsNullOrWhiteSpace(entry.Title)
                    ? string.IsNullOrWhiteSpace(loaded.Chapter.ChineseTitle) ? $"第{entry.Number}章" : loaded.Chapter.ChineseTitle.Trim()
                    : entry.Title.Trim();
                string original = ComposeOriginalText(loaded.Chapter);
                progress?.Report(new(index + 1, snapshot.Chapters.Count, chineseTitle));
                yield return new(entry.ChapterId, entry.Number, chineseTitle, original, true, false);
                continue;
            }
            ComposedChapter composed = ChapterTranslationComposer.Compose(loaded.Chapter);
            string title = string.IsNullOrWhiteSpace(entry.Title)
                ? string.IsNullOrWhiteSpace(loaded.Chapter.Title) ? $"Chương {entry.Number}" : loaded.Chapter.Title.Trim()
                : entry.Title.Trim();
            progress?.Report(new(index + 1, snapshot.Chapters.Count, title));
            yield return new(entry.ChapterId, entry.Number, title, composed.Text, composed.IsComplete,
                composed.InspectionIds.Count > 0);
        }
        EnsureRevision(session, snapshot.Revision);
    }

    internal static string ComposeOriginalText(ChapterDocument chapter) => string.IsNullOrEmpty(chapter.OriginalText)
        ? ChapterTranslationComposer.Join(chapter.Segments.OrderBy(segment => segment.Order).Select(segment => segment.SourceText))
        : chapter.OriginalText;

    private PublicationSnapshot Capture(ProjectSession session, string? expectedRevision = null,
        int? fromNumber = null, int? toNumber = null, PublicationLanguage language = PublicationLanguage.Vietnamese)
    {
        ProjectCatalogSnapshot catalog = _catalog.GetSnapshot(session);
        if (expectedRevision is not null && !string.Equals(catalog.Revision, expectedRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("Project đã thay đổi sau khi kiểm tra xuất bản. Hãy kiểm tra lại trước khi xuất.");
        List<PublicationEntry> chapters = catalog.Chapters.Where(chapter => !chapter.IsDeleted &&
                chapter.Number >= (fromNumber ?? int.MinValue) && chapter.Number <= (toNumber ?? int.MaxValue))
            .OrderBy(chapter => chapter.DisplayOrder)
            .Select(chapter => new PublicationEntry(chapter.ChapterId, chapter.Number,
                language == PublicationLanguage.Chinese ? chapter.ChineseTitle : chapter.VietnameseTitle))
            .ToList();
        string title = language == PublicationLanguage.Chinese && !string.IsNullOrWhiteSpace(session.Manifest.ChineseTitle)
            ? session.Manifest.ChineseTitle.Trim() : session.Manifest.VietnameseTitle.Trim();
        return new(title, catalog.Revision, chapters, language);
    }

    private static void EnsureAllowed(ProjectPublicationInspection inspection, ProjectPublicationOptions options)
    {
        if (inspection.TotalChapterCount == 0)
            throw new InvalidOperationException(inspection.IsRange ? "Không có chương nào trong phạm vi đã chọn."
                : "Project chưa có chương để xuất bản.");
        if (options.Language == PublicationLanguage.Vietnamese && inspection.Language != options.Language)
            throw new InvalidOperationException("Hãy kiểm tra lại theo ngôn ngữ xuất bản đã chọn.");
        if (options.Language == PublicationLanguage.Vietnamese && inspection.RequiresConfirmation && !options.AllowIncomplete)
            throw new InvalidOperationException((inspection.IsRange ? "Phạm vi đã chọn" : "Project") +
                " còn chương chưa hoàn tất hoặc cần kiểm tra. Hãy xác nhận xuất bản nháp trước khi tiếp tục.");
    }

    public static void CommitTemporaryFile(string temporaryPath, string outputPath)
    {
        temporaryPath = Path.GetFullPath(temporaryPath);
        outputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(temporaryPath)) throw new FileNotFoundException("Không tìm thấy file xuất tạm.", temporaryPath);
        File.Move(temporaryPath, outputPath, overwrite: true);
    }

    private sealed record PublicationEntry(Guid ChapterId, int Number, string Title);
    private sealed record PublicationSnapshot(string ProjectTitle, string Revision, IReadOnlyList<PublicationEntry> Chapters,
        PublicationLanguage Language);
}
