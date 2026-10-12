using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed class ProjectRepository
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private static readonly JsonSerializerOptions PrettyJson = new(DocumentJson.Options) { WriteIndented = true };

    public async Task<ProjectSession> CreateAsync(string rootDirectory, string vietnameseTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(vietnameseTitle);
        string root = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(root);
        if (Directory.EnumerateFiles(root, "*.qtproj", SearchOption.TopDirectoryOnly).Any())
            throw new IOException("Thư mục đã có một dự án .qtproj. Ứng dụng không ghi đè dự án hiện có.");

        string manifestPath = Path.Combine(root, SafeFileName(vietnameseTitle) + ".qtproj");
        if (File.Exists(manifestPath)) throw new IOException("Tệp dự án đã tồn tại.");
        var now = DateTimeOffset.UtcNow;
        var manifest = new ProjectManifest
        {
            VietnameseTitle = vietnameseTitle.Trim(), CreatedAt = now, UpdatedAt = now, LastOpenedAt = now,
            ApplicationVersion = typeof(ProjectRepository).Assembly.GetName().Version?.ToString() ?? ""
        };
        manifest.Files.CharacterProfiles = $"Characters/{manifest.ProjectId:D}.characters.json";
        manifest.Validate();
        CreateLayout(root);
        await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        return new ProjectSession(manifestPath, Clone(manifest));
    }

    public async Task<ProjectOpenResult> OpenAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        manifestPath = Path.GetFullPath(manifestPath);
        if (!string.Equals(Path.GetExtension(manifestPath), ".qtproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hãy chọn đúng tệp dự án .qtproj.");
        Exception? primary = null;
        ProjectManifest manifest;
        bool recovered = false;
        try { manifest = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            primary = ex;
            try
            {
                manifest = await ReadManifestAsync(manifestPath + ".bak", cancellationToken).ConfigureAwait(false);
                recovered = true;
            }
            catch (Exception backup) when (IsRecoverable(backup))
            {
                throw new InvalidDataException("Không đọc được dự án hoặc bản dự phòng. Không có dữ liệu nào bị ghi đè.",
                    new AggregateException(primary, backup));
            }
        }
        string root = Path.GetDirectoryName(manifestPath)!;
        CreateLayout(root);
        return new(new ProjectSession(manifestPath, manifest), recovered);
    }

    public async Task SaveManifestAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Manifest.UpdatedAt = DateTimeOffset.UtcNow;
        session.Manifest.ChapterCount = session.Manifest.Chapters.Count(x => x.DeletedAt is null);
        session.Manifest.DeletedChapterCount = session.Manifest.Chapters.Count(x => x.DeletedAt is not null);
        session.Manifest.Validate();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteManifestAsync(session.ManifestPath, session.Manifest, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
    }

    public async Task<ProjectChapterEntry> AddChapterAsync(ProjectSession session, ChapterDocument chapter, int number,
        string? chineseTitle = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session); ArgumentNullException.ThrowIfNull(chapter);
        if (number <= 0) throw new ArgumentOutOfRangeException(nameof(number));
        if (session.Manifest.Chapters.Any(x => x.DeletedAt is null && x.Number == number))
            throw new InvalidOperationException($"Dự án đã có chương số {number}.");
        if (session.Manifest.Chapters.Any(x => x.ChapterId == chapter.Id))
            throw new InvalidOperationException("Chương này đã có trong dự án.");
        if (File.Exists(session.ChapterStore.GetPath(chapter.Id)))
            throw new IOException("Thư mục dự án đã có tệp cùng ID nhưng manifest không quản lý. Ứng dụng dừng để tránh ghi đè.");

        var saved = DocumentJson.Clone(chapter);
        var previousManifest = Clone(session.Manifest);
        saved.ProjectId = session.Manifest.ProjectId;
        saved.ChapterNumber = number;
        saved.ChineseTitle = chineseTitle?.Trim() ?? saved.ChineseTitle;
        saved.CharacterProject.Id = session.Manifest.ProjectId;
        await session.ChapterStore.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
        var entry = ToEntry(saved, number);
        session.Manifest.Chapters.Add(entry);
        session.Manifest.CurrentChapterId = saved.Id;
        try { await SaveManifestAsync(session, cancellationToken).ConfigureAwait(false); }
        catch
        {
            session.Manifest = previousManifest;
            TryDelete(session.ChapterStore.GetPath(saved.Id));
            throw;
        }
        return entry;
    }

    public async Task UpdateChapterAsync(ProjectSession session, ChapterDocument chapter,
        CancellationToken cancellationToken = default)
    {
        var entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapter.Id && x.DeletedAt is null)
            ?? throw new InvalidOperationException("Chương không thuộc dự án đang mở.");
        if (chapter.ProjectId is not null && chapter.ProjectId != session.Manifest.ProjectId)
            throw new InvalidOperationException("Chương đang thuộc dự án khác.");
        chapter.ProjectId = session.Manifest.ProjectId;
        chapter.ChapterNumber = entry.Number;
        chapter.CharacterProject.Id = session.Manifest.ProjectId;
        await session.ChapterStore.SaveAsync(chapter, cancellationToken).ConfigureAwait(false);
        CopySummary(entry, chapter);
        session.Manifest.CurrentChapterId = chapter.Id;
        await SaveManifestAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateChapterSummaryAsync(ProjectSession session, ChapterDocument chapter,
        CancellationToken cancellationToken = default)
    {
        var entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapter.Id && x.DeletedAt is null)
            ?? throw new InvalidOperationException("Chương không thuộc dự án đang mở.");
        CopySummary(entry, chapter);
        session.Manifest.CurrentChapterId = chapter.Id;
        await SaveManifestAsync(session, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChapterLoadResult> LoadChapterAsync(ProjectSession session, Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapterId && x.DeletedAt is null)
            ?? throw new FileNotFoundException("Không tìm thấy chương trong dự án.");
        string path = session.Resolve(entry.RelativePath);
        var result = await session.ChapterStore.LoadAsync(path, cancellationToken).ConfigureAwait(false);
        if (result.Chapter.Id != chapterId || result.Chapter.ProjectId is Guid projectId && projectId != session.Manifest.ProjectId)
            throw new InvalidDataException("Tệp chương không khớp với manifest dự án.");
        return result;
    }

    public async Task RenameChapterAsync(ProjectSession session, Guid chapterId, string title, string chineseTitle,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Tên chương không được để trống.");
        var result = await LoadChapterAsync(session, chapterId, cancellationToken).ConfigureAwait(false);
        result.Chapter.Title = title.Trim(); result.Chapter.ChineseTitle = chineseTitle.Trim();
        result.Chapter.UpdatedAt = DateTimeOffset.UtcNow;
        await UpdateChapterAsync(session, result.Chapter, cancellationToken).ConfigureAwait(false);
    }

    public async Task SoftDeleteChapterAsync(ProjectSession session, Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapterId && x.DeletedAt is null)
            ?? throw new FileNotFoundException("Không tìm thấy chương cần xóa.");
        string source = session.Resolve(entry.RelativePath);
        string relative = $"History/Deleted/{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{chapterId:D}.qtaie.json";
        string destination = session.Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) throw new IOException("Đích lưu lịch sử đã tồn tại.");
        File.Move(source, destination);
        if (File.Exists(source + ".bak")) File.Move(source + ".bak", destination + ".bak");
        string oldRelative = entry.RelativePath;
        DateTimeOffset? oldDeletedAt = entry.DeletedAt;
        Guid? oldCurrent = session.Manifest.CurrentChapterId;
        entry.RelativePath = relative; entry.DeletedAt = DateTimeOffset.UtcNow;
        if (session.Manifest.CurrentChapterId == chapterId)
            session.Manifest.CurrentChapterId = session.Manifest.Chapters.FirstOrDefault(x => x.DeletedAt is null)?.ChapterId;
        try { await SaveManifestAsync(session, cancellationToken).ConfigureAwait(false); }
        catch
        {
            File.Move(destination, source);
            if (File.Exists(destination + ".bak")) File.Move(destination + ".bak", source + ".bak");
            entry.RelativePath = oldRelative; entry.DeletedAt = oldDeletedAt; session.Manifest.CurrentChapterId = oldCurrent;
            throw;
        }
    }

    public async Task RestoreDeletedChapterAsync(ProjectSession session, Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        var entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapterId && x.DeletedAt is not null)
            ?? throw new FileNotFoundException("Không tìm thấy chương trong thùng lịch sử.");
        if (session.Manifest.Chapters.Any(x => x.DeletedAt is null && x.Number == entry.Number))
            throw new InvalidOperationException("Không thể phục hồi vì số chương đã được dùng.");
        string source = session.Resolve(entry.RelativePath);
        string relative = $"Chapters/{chapterId:D}.qtaie.json";
        string destination = session.Resolve(relative);
        if (File.Exists(destination)) throw new IOException("Tệp chương đích đã tồn tại.");
        File.Move(source, destination);
        if (File.Exists(source + ".bak")) File.Move(source + ".bak", destination + ".bak");
        string oldRelative = entry.RelativePath;
        DateTimeOffset? oldDeletedAt = entry.DeletedAt;
        Guid? oldCurrent = session.Manifest.CurrentChapterId;
        entry.RelativePath = relative; entry.DeletedAt = null; session.Manifest.CurrentChapterId = chapterId;
        try { await SaveManifestAsync(session, cancellationToken).ConfigureAwait(false); }
        catch
        {
            File.Move(destination, source);
            if (File.Exists(destination + ".bak")) File.Move(destination + ".bak", source + ".bak");
            entry.RelativePath = oldRelative; entry.DeletedAt = oldDeletedAt; session.Manifest.CurrentChapterId = oldCurrent;
            throw;
        }
    }

    public async Task<ProjectChapterEntry> DuplicateChapterAsync(ProjectSession session, Guid chapterId, int newNumber,
        string newTitle, CancellationToken cancellationToken = default)
    {
        var source = (await LoadChapterAsync(session, chapterId, cancellationToken).ConfigureAwait(false)).Chapter;
        var duplicate = Translation.TranslationService.CreateChapter(newTitle, source.OriginalText,
            source.SegmentationOptions, source.DefaultModelOptions);
        duplicate.SelectedPreset = source.SelectedPreset;
        duplicate.ChineseTitle = source.ChineseTitle;
        duplicate.ConfirmedTerms = source.ConfirmedTerms.Select(x => new ConfirmedTerm(x.Chinese, x.Vietnamese)).ToList();
        duplicate.ConfirmedContextNotes = source.ConfirmedContextNotes;
        duplicate.CharacterProject = Persistence.CharacterProjectStore.Clone(source.CharacterProject);
        duplicate.TextCleaner = JsonSerializer.Deserialize<TextCleanerSettings>(
            JsonSerializer.Serialize(source.TextCleaner, DocumentJson.Options), DocumentJson.Options)!;
        for (int i = 0; i < Math.Min(source.Segments.Count, duplicate.Segments.Count); i++)
        {
            if (!string.Equals(source.Segments[i].SourceText, duplicate.Segments[i].SourceText, StringComparison.Ordinal)) break;
            var from = source.Segments[i]; var to = duplicate.Segments[i];
            to.UserDraft = from.UserDraft; to.DraftRevision = from.DraftRevision;
            to.PreservedText = from.EditableText; to.ProtectedByPreviousEdit = !string.IsNullOrEmpty(from.EditableText);
            to.Status = to.ProtectedByPreviousEdit ? SegmentStatus.AwaitingReview : SegmentStatus.Untranslated;
        }
        return await AddChapterAsync(session, duplicate, newNumber, source.ChineseTitle, cancellationToken).ConfigureAwait(false);
    }

    private static ProjectChapterEntry ToEntry(ChapterDocument chapter, int number)
    {
        var entry = new ProjectChapterEntry { ChapterId = chapter.Id, Number = number,
            RelativePath = $"Chapters/{chapter.Id:D}.qtaie.json" };
        CopySummary(entry, chapter); return entry;
    }

    private static void CopySummary(ProjectChapterEntry entry, ChapterDocument chapter)
    {
        entry.VietnameseTitle = chapter.Title; entry.ChineseTitle = chapter.ChineseTitle;
        entry.SegmentCount = chapter.Segments.Count;
        entry.CompletedSegmentCount = chapter.Segments.Count(x => x.HasCompletedTranslation || x.Status == SegmentStatus.Approved);
        entry.State = AggregateState(chapter); entry.UpdatedAt = chapter.UpdatedAt;
    }

    public static ProjectChapterState AggregateState(ChapterDocument chapter)
    {
        if (chapter.Segments.Any(x => x.Status == SegmentStatus.Error)) return ProjectChapterState.Error;
        if (chapter.Segments.Any(x => x.Status == SegmentStatus.InspectionRequired)) return ProjectChapterState.NeedsInspection;
        if (chapter.Segments.All(x => x.Status == SegmentStatus.Approved)) return ProjectChapterState.Approved;
        if (chapter.Segments.Any(x => x.Status == SegmentStatus.Translating)) return ProjectChapterState.Translating;
        if (chapter.Segments.Any(x => x.HasCompletedTranslation || x.Status == SegmentStatus.AwaitingReview)) return ProjectChapterState.AwaitingReview;
        return ProjectChapterState.Untranslated;
    }

    internal static ProjectManifest Clone(ProjectManifest value) => JsonSerializer.Deserialize<ProjectManifest>(
        JsonSerializer.Serialize(value, DocumentJson.Options), DocumentJson.Options)!;

    private static async Task WriteManifestAsync(string path, ProjectManifest manifest, CancellationToken cancellationToken)
    {
        var content = JsonSerializer.SerializeToElement(manifest, DocumentJson.Options);
        var envelope = new ManifestEnvelope { SchemaVersion = 1, ContentSha256 = Hash(content), Project = content };
        await AtomicLocalFile.WriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(envelope, PrettyJson), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ProjectManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<ManifestEnvelope>(bytes, DocumentJson.Options)
            ?? throw new InvalidDataException("Tệp dự án trống.");
        if (envelope.SchemaVersion != 1 || envelope.Project.ValueKind != JsonValueKind.Object ||
            !string.Equals(envelope.ContentSha256, Hash(envelope.Project), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp dự án sai định dạng hoặc checksum không khớp.");
        var manifest = envelope.Project.Deserialize<ProjectManifest>(DocumentJson.Options)
            ?? throw new InvalidDataException("Manifest dự án bị thiếu.");
        manifest.Validate(); return manifest;
    }

    private static string Hash(JsonElement content) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, DocumentJson.Options))));
    private static bool IsRecoverable(Exception ex) => ex is IOException or JsonException or InvalidDataException;
    private static string SafeFileName(string value)
    {
        string result = string.Concat(value.Trim().Select(x => Path.GetInvalidFileNameChars().Contains(x) ? '_' : x));
        return string.IsNullOrWhiteSpace(result) ? "project" : result;
    }
    private static void CreateLayout(string root)
    {
        foreach (string directory in new[] { "Chapters", "Characters", "Glossary", "Settings", "Settings/prompts", "Resources", "Backups", "History", "History/Deleted", "Cache" })
            Directory.CreateDirectory(Path.Combine(root, directory.Replace('/', Path.DirectorySeparatorChar)));
    }
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private sealed class ManifestEnvelope
    {
        public int SchemaVersion { get; set; }
        public string ContentSha256 { get; set; } = "";
        public JsonElement Project { get; set; }
    }
}
