using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Collections.Concurrent;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed class ChapterFileStore : IChapterStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _writeGate;
    private readonly string _directory;
    private static readonly JsonSerializerOptions EnvelopeOptions = new(DocumentJson.Options) { WriteIndented = true };
    private static readonly JsonSerializerOptions LegacyOptions = CreateLegacyOptions();

    public ChapterFileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _writeGate = WriteGates.GetOrAdd(_directory, _ => new(1, 1));
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickTranslatorAiEditor", "Chapters");

    public string DirectoryPath => _directory;

    public IReadOnlyList<string> EnumerateChapterPaths() => Directory.Exists(_directory)
        ? Directory.GetFiles(_directory, "*.qtaie.json", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];

    public string GetPath(Guid chapterId) => Path.Combine(_directory, $"{chapterId:D}.qtaie.json");

    internal static byte[] SerializeDocument(ChapterDocument chapter)
    {
        StorageIoMetricsCapture.Serialization();
        var snapshot = DocumentJson.Clone(chapter);
        DocumentJson.Migrate(snapshot); DocumentJson.Validate(snapshot);
        var content = JsonSerializer.SerializeToElement(snapshot, DocumentJson.Options);
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope { SchemaVersion = 1, Chapter = content,
            ContentSha256 = Hash(content) }, EnvelopeOptions);
    }

    internal static Task<ChapterDocument> ReadVerifiedFileAsync(string path, CancellationToken cancellationToken = default) =>
        ReadVerifiedAsync(path, cancellationToken);

    public async Task<string> CreateBatchBackupAsync(IEnumerable<Guid> chapterIds, Guid batchId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chapterIds);
        if (batchId == Guid.Empty) throw new ArgumentException("Batch ID không hợp lệ.", nameof(batchId));
        var ids = chapterIds.Distinct().ToArray();
        if (ids.Length == 0) throw new ArgumentException("Không có chương cần sao lưu.", nameof(chapterIds));
        string destination = Path.Combine(_directory, "BatchBackups",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{batchId:N}");
        Directory.CreateDirectory(destination);
        var copied = new List<string>();
        try
        {
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = GetPath(id);
                if (!File.Exists(source)) continue;
                // Validate before copying so a corrupt primary is never labelled as the batch backup.
                await ReadVerifiedAsync(source, cancellationToken).ConfigureAwait(false);
                string target = Path.Combine(destination, Path.GetFileName(source));
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                copied.Add(Path.GetFileName(source));
            }
            string manifest = Path.Combine(destination, "manifest.txt");
            await File.WriteAllLinesAsync(manifest,
                [$"BatchId={batchId:D}", $"CreatedAt={DateTimeOffset.UtcNow:O}", .. copied], cancellationToken)
                .ConfigureAwait(false);
            return destination;
        }
        catch
        {
            // Preserve any complete copies for manual recovery and report the failure to the caller.
            throw;
        }
    }

    public RecoveryInfo? LastRecovery { get; private set; }
    public async Task RestoreBackupAsync(CancellationToken cancellationToken = default) =>
        LastRecovery = await RecoveryFiles.RestoreBackupAsync(LastRecovery ?? throw new InvalidOperationException("Hãy đọc preview trước."), DeserializeDocument, cancellationToken).ConfigureAwait(false);
    public async Task SaveAsync(ChapterDocument chapter, CancellationToken cancellationToken = default)
    {
        byte[] bytes = SerializeDocument(chapter);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastRecovery = await RecoveryFiles.WriteAsync(GetPath(chapter.Id), bytes, DeserializeDocument,
                cancellationToken).ConfigureAwait(false);
        }
        finally { _writeGate.Release(); }
    }

    internal async Task<string> SaveSerializedAsync(Guid chapterId, byte[] bytes, string? expectedPrimarySha256,
        CancellationToken cancellationToken = default)
    {
        _ = DeserializeDocument(bytes);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastRecovery = await RecoveryFiles.WriteFastCheckedAsync(GetPath(chapterId), bytes, DeserializeDocument,
                expectedPrimarySha256, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        finally { _writeGate.Release(); }
    }
    public async Task<ChapterLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var loaded = await RecoveryFiles.ReadAsync(path, DeserializeDocument, cancellationToken: cancellationToken).ConfigureAwait(false);
        LastRecovery = loaded.Info;
        return new(loaded.Value, loaded.Info.Source == RecoverySource.Backup, loaded.Info.SourcePath!);
    }
    private static async Task<ChapterDocument> ReadVerifiedAsync(string path, CancellationToken cancellationToken)
    {
        return DeserializeDocument(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
    }
    internal static ChapterDocument DeserializeDocument(byte[] bytes)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, DocumentJson.Options);        if (envelope?.SchemaVersion != 1 || envelope.Chapter.ValueKind != JsonValueKind.Object || envelope.ContentSha256 is null)
            throw new InvalidDataException("Tệp tiến độ sai định dạng hoặc phiên bản.");
        var chapter = envelope.Chapter.Deserialize<ChapterDocument>(DocumentJson.Options)
            ?? throw new InvalidDataException("Thiếu nội dung chương.");
        // v1 hashed typed values (dates are written differently from generic JSON strings).
        // Recreate that exact field set before adding v2 fields or migrating status.
        string checksum = chapter.SchemaVersion == 1 ? Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(chapter, LegacyOptions)))) : Hash(envelope.Chapter);
        if (!string.Equals(checksum, envelope.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp tiến độ bị thay đổi hoặc hỏng: checksum không khớp.");
        // Fields absent in legacy bytes get stable metadata, so reopening the same source yields the same value.
        // Explicit stored snapshots are retained, including compatible fields in a v1/v2 document.
        if (!envelope.Chapter.TryGetProperty("selectedPreset", out _))
            chapter.SelectedPreset = PromptPreset.Default with { UpdatedAt = chapter.UpdatedAt };
        if (!envelope.Chapter.TryGetProperty("queue", out var queue))
            chapter.Queue = new() { Preset = PromptPreset.Default with { UpdatedAt = chapter.UpdatedAt } };
        else if (queue.ValueKind == JsonValueKind.Object && !queue.TryGetProperty("preset", out _) && chapter.Queue is not null)
            chapter.Queue.Preset = PromptPreset.Default with { UpdatedAt = chapter.UpdatedAt };
        if (!envelope.Chapter.TryGetProperty("characterProject", out _))
            chapter.CharacterProject = new() { Id = chapter.ProjectId ?? chapter.Id, UpdatedAt = chapter.UpdatedAt,
                Name = string.IsNullOrWhiteSpace(chapter.Title) ? "Bộ truyện mới" : chapter.Title };
        // Check the original JSON before migration adds fields and changes serialization of v1 data.
        DocumentJson.Migrate(chapter);
        DocumentJson.Validate(chapter);
        return chapter;
    }

    private static bool IsRecoverableReadError(Exception ex) => ex is IOException or InvalidDataException or JsonException or ArgumentException;

    private static string Hash(JsonElement chapter)
    {
        StorageIoMetricsCapture.Hash();
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(chapter, DocumentJson.Options))));
    }

    private static JsonSerializerOptions CreateLegacyOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            string[] added = info.Type == typeof(ChapterDocument)
                ? ["projectId", "chapterNumber", "chineseTitle", "selectedPreset", "queue", "segmentationHistory", "confirmedTerms", "confirmedContextNotes", "characterProject", "textCleaner", "batchReplacementHistory", "sourceRevision", "sourceEditHistory"]
                : info.Type == typeof(TranslationSegment)
                    ? ["draftHistory", "preservedText", "requiresRetranslation", "protectedByPreviousEdit", "normalizedText", "normalizationHistory"]
                    : info.Type == typeof(TranslationAttempt) ? ["presetSnapshot", "sourceSha256", "normalizationRunId", "projectResourceRevision"] : [];
            foreach (var property in info.Properties.Where(p => added.Contains(p.Name)).ToArray()) info.Properties.Remove(property);
        });
        return new(DocumentJson.Options) { TypeInfoResolver = resolver };
    }

    private sealed class Envelope
    {
        public int SchemaVersion { get; set; }
        public string ContentSha256 { get; set; } = "";
        public JsonElement Chapter { get; set; }
    }
}
