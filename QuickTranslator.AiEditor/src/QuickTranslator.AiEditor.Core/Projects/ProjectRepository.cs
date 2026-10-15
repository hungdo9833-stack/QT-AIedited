using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

public sealed partial class ProjectRepository
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
        var session = new ProjectSession(manifestPath, Clone(manifest));
        PublishManifestCommitted(session, "CreateProject");
        return session;
    }

    public async Task<ProjectOpenResult> OpenAsync(string manifestPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        manifestPath = Path.GetFullPath(manifestPath);
        if (!string.Equals(Path.GetExtension(manifestPath), ".qtproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Hãy chọn đúng tệp dự án .qtproj.");
        var loaded = await RecoveryFiles.ReadAsync(manifestPath, DecodeManifest, cancellationToken: cancellationToken).ConfigureAwait(false);
        var manifest = loaded.Value;
        bool recovered = loaded.Info.Source == RecoverySource.Backup;        string root = Path.GetDirectoryName(manifestPath)!;
        CreateLayout(root);
        var session = new ProjectSession(manifestPath, manifest) { ManifestRecovery = loaded.Info };
        session.RecoveryReport = await ProjectTransactionJournal.InspectAsync(session, cancellationToken).ConfigureAwait(false);
        if (!session.RecoveryReport.RequiresDecision)
            await ReconcileFastCheckpointSummariesAsync(session, cancellationToken).ConfigureAwait(false);
        return new(session, recovered);
    }

    public async Task SaveManifestAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        if (session.RecoveryReport.RequiresDecision)
            throw new InvalidOperationException("Còn transaction chưa đối chiếu; không lưu manifest đè trạng thái phục hồi.");
        session.Manifest.UpdatedAt = DateTimeOffset.UtcNow;
        session.Manifest.ChapterCount = session.Manifest.Chapters.Count(x => x.DeletedAt is null);
        session.Manifest.DeletedChapterCount = session.Manifest.Chapters.Count(x => x.DeletedAt is not null);
        session.Manifest.Validate();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteManifestAsync(session.ManifestPath, session.Manifest, cancellationToken).ConfigureAwait(false); }
        finally { _writeGate.Release(); }
        PublishManifestCommitted(session, "SaveManifest");
        }
        finally { session.OperationGate.Release(); }
    }

    public async Task UpdateChapterAsync(ProjectSession session, ChapterDocument chapter,
        CancellationToken cancellationToken = default)
    {
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = await WriteFastChapterCheckpointAsync(session, chapter, null, "UpdateChapter", cancellationToken)
                .ConfigureAwait(false);
        }
        finally { session.OperationGate.Release(); }
    }

    public async Task UpdateChapterSummaryAsync(ProjectSession session, ChapterDocument chapter,
        CancellationToken cancellationToken = default)
    {
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (session.RecoveryReport.RequiresDecision) throw new InvalidOperationException("Còn transaction cần phục hồi; không ghi manifest.");
            var after = Clone(session.Manifest);
            var entry = after.Chapters.SingleOrDefault(x => x.ChapterId == chapter.Id && x.DeletedAt is null)
                ?? throw new InvalidOperationException("Chương không thuộc dự án đang mở.");
            CopySummary(entry, chapter); after.CurrentChapterId = chapter.Id; NormalizeManifest(after);
            await WriteManifestAsync(session.ManifestPath, after, cancellationToken).ConfigureAwait(false);
            session.Manifest = after;
            PublishManifestCommitted(session, "UpdateChapterSummary");
        }
        finally { session.OperationGate.Release(); }
    }
    public async Task<ChapterLoadResult> LoadChapterAsync(ProjectSession session, Guid chapterId,
        CancellationToken cancellationToken = default)
    {
        FaultPoint?.Invoke("BeforeChapterRead");
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

    private async Task<string> WriteFastChapterCheckpointAsync(ProjectSession session, ChapterDocument chapter,
        string? expectedStorageRevision, string operation, CancellationToken cancellationToken,
        bool deferManifestCheckpoint = false)
    {
        if (session.RecoveryReport.RequiresDecision)
            throw new InvalidOperationException("Còn transaction cần phục hồi; không ghi checkpoint chương.");
        ProjectChapterEntry entry = session.Manifest.Chapters.SingleOrDefault(x => x.ChapterId == chapter.Id && x.DeletedAt is null)
            ?? throw new InvalidOperationException("Chương không thuộc dự án đang mở.");
        if (chapter.ProjectId is not null && chapter.ProjectId != session.Manifest.ProjectId)
            throw new InvalidOperationException("Chương đang thuộc dự án khác.");
        chapter.ProjectId = session.Manifest.ProjectId;
        chapter.ChapterNumber = entry.Number;
        chapter.CharacterProject.Id = session.Manifest.ProjectId;
        byte[] chapterBytes = ChapterFileStore.SerializeDocument(chapter);
        FaultPoint?.Invoke("BeforeFastChapterWrite");
        string revision = await session.ChapterStore.SaveSerializedAsync(chapter.Id, chapterBytes,
            expectedStorageRevision, cancellationToken).ConfigureAwait(false);
        FaultPoint?.Invoke("AfterFastChapterWrite");
        if (deferManifestCheckpoint)
        {
            CopySummary(entry, chapter);
            session.Manifest.CurrentChapterId = chapter.Id;
            return revision;
        }
        ProjectManifest after = Clone(session.Manifest);
        CopySummary(after.Chapters.Single(x => x.ChapterId == chapter.Id), chapter);
        after.CurrentChapterId = chapter.Id;
        NormalizeManifest(after);
        await WriteFastManifestAsync(session.ManifestPath, after, cancellationToken).ConfigureAwait(false);
        FaultPoint?.Invoke("AfterFastManifestWrite");
        session.Manifest = after;
        PublishManifestCommitted(session, operation);
        return revision;
    }

    internal async Task FlushDeferredManifestAsync(ProjectSession session, string operation,
        CancellationToken cancellationToken = default)
    {
        await session.OperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectManifest after = Clone(session.Manifest);
            NormalizeManifest(after);
            await WriteFastManifestAsync(session.ManifestPath, after, cancellationToken).ConfigureAwait(false);
            session.Manifest = after;
            PublishManifestCommitted(session, operation);
        }
        finally { session.OperationGate.Release(); }
    }

    private static async Task ReconcileFastCheckpointSummariesAsync(ProjectSession session,
        CancellationToken cancellationToken)
    {
        DateTime manifestWritten = File.GetLastWriteTimeUtc(session.ManifestPath);
        ProjectManifest? reconciled = null;
        foreach (ProjectChapterEntry entry in session.Manifest.Chapters.Where(value => value.DeletedAt is null))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = session.Resolve(entry.RelativePath);
            if (!File.Exists(path) || File.GetLastWriteTimeUtc(path) <= manifestWritten) continue;
            ChapterDocument chapter = (await session.ChapterStore.LoadAsync(path, cancellationToken).ConfigureAwait(false)).Chapter;
            if (chapter.Id != entry.ChapterId || chapter.ProjectId is Guid projectId && projectId != session.Manifest.ProjectId)
                throw new InvalidDataException("Checkpoint chapter mới hơn manifest nhưng identity không khớp Project.");
            if (SummaryMatches(entry, chapter)) continue;
            reconciled ??= Clone(session.Manifest);
            CopySummary(reconciled.Chapters.Single(value => value.ChapterId == chapter.Id), chapter);
        }
        if (reconciled is null) return;
        NormalizeManifest(reconciled);
        await WriteManifestAsync(session.ManifestPath, reconciled, cancellationToken).ConfigureAwait(false);
        session.Manifest = reconciled;
    }

    private static bool SummaryMatches(ProjectChapterEntry entry, ChapterDocument chapter) =>
        string.Equals(entry.VietnameseTitle, chapter.Title, StringComparison.Ordinal) &&
        string.Equals(entry.ChineseTitle, chapter.ChineseTitle, StringComparison.Ordinal) &&
        entry.SegmentCount == chapter.Segments.Count &&
        entry.CompletedSegmentCount == chapter.Segments.Count(segment =>
            segment.HasCompletedTranslation || segment.Status == SegmentStatus.Approved) &&
        entry.State == AggregateState(chapter) && entry.UpdatedAt == chapter.UpdatedAt;

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

    internal static byte[] SerializeManifest(ProjectManifest manifest)
    {
        StorageIoMetricsCapture.Serialization();
        var content = JsonSerializer.SerializeToElement(manifest, DocumentJson.Options);
        var envelope = new ManifestEnvelope { SchemaVersion = 1, ContentSha256 = Hash(content), Project = content };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, PrettyJson);
    }

    private static Task WriteManifestAsync(string path, ProjectManifest manifest, CancellationToken cancellationToken) =>
        WriteSafeManifestAsync(path, manifest, cancellationToken);

    private static async Task WriteFastManifestAsync(string path, ProjectManifest manifest,
        CancellationToken cancellationToken) =>
        _ = await RecoveryFiles.WriteFastCheckedAsync(path, SerializeManifest(manifest), DecodeManifest,
            null, cancellationToken).ConfigureAwait(false);

    internal static async Task<ProjectManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        return DecodeManifest(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
    }
    private static async Task WriteSafeManifestAsync(string path, ProjectManifest manifest, CancellationToken token) =>
        _ = await RecoveryFiles.WriteAsync(path, SerializeManifest(manifest), DecodeManifest, token).ConfigureAwait(false);
    internal static ProjectManifest DecodeManifest(byte[] bytes)
    {        var envelope = JsonSerializer.Deserialize<ManifestEnvelope>(bytes, DocumentJson.Options)
            ?? throw new InvalidDataException("Tệp dự án trống.");
        if (envelope.SchemaVersion != 1 || envelope.Project.ValueKind != JsonValueKind.Object ||
            !string.Equals(envelope.ContentSha256, Hash(envelope.Project), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp dự án sai định dạng hoặc checksum không khớp.");
        var manifest = envelope.Project.Deserialize<ProjectManifest>(DocumentJson.Options)
            ?? throw new InvalidDataException("Manifest dự án bị thiếu.");
        manifest.Validate(); return manifest;
    }

    private static string Hash(JsonElement content)
    {
        StorageIoMetricsCapture.Hash();
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, DocumentJson.Options))));
    }
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

    private sealed class ManifestEnvelope
    {
        public int SchemaVersion { get; set; }
        public string ContentSha256 { get; set; } = "";
        public JsonElement Project { get; set; }
    }
}
