using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed class CharacterProjectStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CharacterProjectStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickTranslatorAiEditor", "CharacterProjects");

    public string GetPath(Guid projectId) => Path.Combine(_directory, $"{projectId:D}.characters.json");

    public Task SaveAsync(CharacterProject project, CancellationToken cancellationToken = default) =>
        SaveToAsync(project, GetPath(project.Id), cancellationToken);

    public async Task SaveToAsync(CharacterProject project, string path, CancellationToken cancellationToken = default)
    {
        var snapshot = Clone(project);
        snapshot.UpdatedAt = DateTimeOffset.UtcNow;
        snapshot.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SimpleFileStorage.WriteAsync(path, bytes, Decode, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<CharacterProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        return await SimpleFileStorage.ReadRequiredAsync(path, Decode, cancellationToken).ConfigureAwait(false);
    }

    private static CharacterProject Decode(byte[] bytes)
    {
        return JsonFileCodec.DecodeDirect<CharacterProject>(bytes, value => value.Validate(), "schemaVersion", "id",
            "name", "updatedAt", "normalizeAfterTranslation", "consistencyLockEnabled", "profiles");
    }

    public static CharacterProject Clone(CharacterProject project) =>
        JsonSerializer.Deserialize<CharacterProject>(JsonSerializer.Serialize(project, DocumentJson.Options), DocumentJson.Options)!;
}
