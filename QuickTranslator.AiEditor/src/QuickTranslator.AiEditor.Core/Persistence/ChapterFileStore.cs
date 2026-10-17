using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed class ChapterFileStore : IChapterStore
{
    private readonly string _directory;
    private static readonly JsonSerializerOptions PrettyJson = new(DocumentJson.Options) { WriteIndented = true };

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

    internal static byte[] SerializeDocument(ChapterDocument chapter)
    {
        StorageIoMetricsCapture.Serialization();
        ChapterDocument snapshot = DocumentJson.Clone(chapter);
        DocumentJson.Validate(snapshot);
        return JsonSerializer.SerializeToUtf8Bytes(snapshot, PrettyJson);
    }

    internal static Task<ChapterDocument> ReadVerifiedFileAsync(string path,
        CancellationToken cancellationToken = default) =>
        SimpleFileStorage.ReadRequiredAsync(path, DeserializeDocument, cancellationToken);

    public async Task SaveAsync(ChapterDocument chapter, CancellationToken cancellationToken = default)
    {
        byte[] bytes = SerializeDocument(chapter);
        await SimpleFileStorage.WriteAsync(GetPath(chapter.Id), bytes, DeserializeDocument, cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task SaveSerializedAsync(Guid chapterId, byte[] bytes,
        CancellationToken cancellationToken = default)
    {
        _ = DeserializeDocument(bytes);
        await SimpleFileStorage.WriteAsync(GetPath(chapterId), bytes, DeserializeDocument, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ChapterLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ChapterDocument chapter = await SimpleFileStorage.ReadRequiredAsync(path, DeserializeDocument, cancellationToken)
            .ConfigureAwait(false);
        return new(chapter, Path.GetFullPath(path));
    }

    internal static ChapterDocument DeserializeDocument(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Tệp chương phải là JSON object.");

        ChapterDocument chapter = root.Deserialize<ChapterDocument>(DocumentJson.Options)
            ?? throw new InvalidDataException("Thiếu nội dung chương.");
        DocumentJson.Validate(chapter);
        return chapter;
    }
}
