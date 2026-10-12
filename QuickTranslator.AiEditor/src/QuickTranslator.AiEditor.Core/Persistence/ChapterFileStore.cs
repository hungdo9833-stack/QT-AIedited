using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed class ChapterFileStore : IChapterStore
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _directory;
    private static readonly JsonSerializerOptions EnvelopeOptions = new(DocumentJson.Options) { WriteIndented = true };
    private static readonly JsonSerializerOptions LegacyOptions = CreateLegacyOptions();

    public ChapterFileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickTranslatorAiEditor", "Chapters");

    public string DirectoryPath => _directory;

    public IReadOnlyList<string> EnumerateChapterPaths() => Directory.Exists(_directory)
        ? Directory.GetFiles(_directory, "*.qtaie.json", SearchOption.TopDirectoryOnly)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
        : [];

    public string GetPath(Guid chapterId) => Path.Combine(_directory, $"{chapterId:D}.qtaie.json");

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

    public async Task SaveAsync(ChapterDocument chapter, CancellationToken cancellationToken = default)
    {
        var snapshot = DocumentJson.Clone(chapter);
        DocumentJson.Migrate(snapshot);
        DocumentJson.Validate(snapshot);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string path = GetPath(snapshot.Id);
        string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(_directory);
            var envelope = new Envelope
            {
                SchemaVersion = 1,
                Chapter = JsonSerializer.SerializeToElement(snapshot, DocumentJson.Options)
            };
            envelope.ContentSha256 = Hash(envelope.Chapter);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, EnvelopeOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
                File.Move(temporary, path);
            else if (await IsValidFileAsync(path, cancellationToken).ConfigureAwait(false))
                File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
            else
                // Preserve the last good backup when recovering a damaged primary.
                File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _writeGate.Release();
        }
    }

    public async Task<ChapterLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Exception? primaryFailure;
        try
        {
            return new(await ReadVerifiedAsync(path, cancellationToken).ConfigureAwait(false), false, Path.GetFullPath(path));
        }
        catch (Exception ex) when (IsRecoverableReadError(ex)) { primaryFailure = ex; }

        try
        {
            string backup = path + ".bak";
            return new(await ReadVerifiedAsync(backup, cancellationToken).ConfigureAwait(false), true, Path.GetFullPath(backup));
        }
        catch (Exception ex) when (IsRecoverableReadError(ex))
        {
            throw new InvalidDataException("Không đọc được tệp tiến độ hoặc bản dự phòng. Giữ cả hai tệp để phục hồi; không tạo chương mới đè lên chúng.",
                new AggregateException(primaryFailure!, ex));
        }
    }

    private static async Task<bool> IsValidFileAsync(string path, CancellationToken cancellationToken)
    {
        try { await ReadVerifiedAsync(path, cancellationToken).ConfigureAwait(false); return true; }
        catch (Exception ex) when (IsRecoverableReadError(ex)) { return false; }
    }

    private static async Task<ChapterDocument> ReadVerifiedAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, FileOptions.Asynchronous);
        var envelope = await JsonSerializer.DeserializeAsync<Envelope>(stream, DocumentJson.Options, cancellationToken).ConfigureAwait(false);
        if (envelope?.SchemaVersion != 1 || envelope.Chapter.ValueKind != JsonValueKind.Object || envelope.ContentSha256 is null)
            throw new InvalidDataException("Tệp tiến độ sai định dạng hoặc phiên bản.");
        var chapter = envelope.Chapter.Deserialize<ChapterDocument>(DocumentJson.Options)
            ?? throw new InvalidDataException("Thiếu nội dung chương.");
        // v1 hashed typed values (dates are written differently from generic JSON strings).
        // Recreate that exact field set before adding v2 fields or migrating status.
        string checksum = chapter.SchemaVersion == 1 ? Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(chapter, LegacyOptions)))) : Hash(envelope.Chapter);
        if (!string.Equals(checksum, envelope.ContentSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp tiến độ bị thay đổi hoặc hỏng: checksum không khớp.");
        // Check the original JSON before migration adds fields and changes serialization of v1 data.
        DocumentJson.Migrate(chapter);
        DocumentJson.Validate(chapter);
        return chapter;
    }

    private static bool IsRecoverableReadError(Exception ex) => ex is IOException or InvalidDataException or JsonException or ArgumentException;

    private static string Hash(JsonElement chapter) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(chapter, DocumentJson.Options))));

    private static JsonSerializerOptions CreateLegacyOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            string[] added = info.Type == typeof(ChapterDocument)
                ? ["projectId", "chapterNumber", "chineseTitle", "selectedPreset", "queue", "segmentationHistory", "confirmedTerms", "confirmedContextNotes", "characterProject", "textCleaner", "batchReplacementHistory"]
                : info.Type == typeof(TranslationSegment)
                    ? ["draftHistory", "preservedText", "requiresRetranslation", "protectedByPreviousEdit", "normalizedText", "normalizationHistory"]
                    : info.Type == typeof(TranslationAttempt) ? ["presetSnapshot", "sourceSha256", "normalizationRunId"] : [];
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
