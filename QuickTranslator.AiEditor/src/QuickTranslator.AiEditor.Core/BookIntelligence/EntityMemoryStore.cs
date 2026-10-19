using System.Collections.Concurrent;
using System.Buffers;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class EntityMemoryStore(ProjectSession session)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public string PathName => session.Resolve("Settings/abi3-entity-event-memory.v1.json");
    public Task<EntityMemoryState> LoadAsync(CancellationToken cancellationToken = default) => SimpleFileStorage.ReadOptionalAsync(
        PathName, Decode, () => new EntityMemoryState { ProjectId = session.Manifest.ProjectId }, cancellationToken);

    /// <summary>One transaction = definitions + checkpoint + append-only decisions. No legacy writes.</summary>
    public async Task<EntityMemoryState> UpdateAsync(long expectedRevision, Action<EntityMemoryState> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var gate = Gates.GetOrAdd(PathName, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            using var lease = new FileStream(PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var before = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (before.Revision != expectedRevision) throw new InvalidOperationException("Memory changed; reload before saving.");
            var after = Clone(before); update(after); after.Validate();
            if (after.Revision != before.Revision || after.ProjectId != before.ProjectId) throw new InvalidOperationException("Cannot replace memory identity/revision.");
            Preserve(before.Entities, after.Entities); Preserve(before.Evidence, after.Evidence); Preserve(before.Facts, after.Facts);
            Preserve(before.Events, after.Events); Preserve(before.Reviews, after.Reviews);
            if (Serialize(before).AsSpan().SequenceEqual(Serialize(after))) return Clone(before);
            after.Revision = checked(before.Revision + 1);
            await SimpleFileStorage.WriteAsync(PathName, Serialize(after), Decode, cancellationToken).ConfigureAwait(false);
            return Clone(after);
        }
        finally { gate.Release(); }
    }

    public static EntityMemoryState Clone(EntityMemoryState value) => JsonSerializer.Deserialize<EntityMemoryState>(Serialize(value), DocumentJson.Options)!;
    public static byte[] Serialize(EntityMemoryState value)
    {
        // Avoid retaining document-sized ArrayPool buffers on every checkpoint worker.
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        JsonSerializer.Serialize(writer, value, DocumentJson.Options); writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
    private static void Preserve<T>(IReadOnlyList<T> before, IReadOnlyList<T> after)
    {
        if (after.Count < before.Count || before.Where((x, i) => EntityMemoryIdentity.Fingerprint(x) != EntityMemoryIdentity.Fingerprint(after[i])).Any())
            throw new InvalidOperationException("Existing memory definitions/review history are immutable; append a new decision/version.");
    }
    private EntityMemoryState Decode(byte[] bytes)
    {
        // Validate required top-level fields without a pooled document-sized DOM.
        var fields = new HashSet<string>(StringComparer.Ordinal);
        var reader = new Utf8JsonReader(bytes);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("Memory must be a JSON object.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName || !fields.Add(reader.GetString()!)) throw new InvalidDataException("Invalid/duplicate memory field.");
            if (!reader.Read()) throw new InvalidDataException("Incomplete memory field.");
            reader.Skip();
        }
        string[] required = ["schemaVersion", "projectId", "revision", "entities", "evidence", "facts", "events", "reviews", "checkpoints"];
        if (reader.TokenType != JsonTokenType.EndObject || reader.Read() || required.Any(x => !fields.Contains(x))) throw new InvalidDataException("Incomplete memory document.");
        var result = JsonSerializer.Deserialize<EntityMemoryState>(bytes, DocumentJson.Options) ?? throw new InvalidDataException("Empty memory document.");
        result.Validate();
        if (result.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Memory belongs to another Project.");
        return result;
    }
}
