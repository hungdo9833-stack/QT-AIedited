using System.Security.Cryptography;
using System.Text;
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
        var content = JsonSerializer.SerializeToElement(snapshot, DocumentJson.Options);
        var envelope = new CharacterProjectEnvelope { Project = content, ContentSha256 = Hash(content) };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AtomicLocalFile.WriteAsync(path, bytes, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<CharacterProject> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            8192, FileOptions.Asynchronous);
        var envelope = await JsonSerializer.DeserializeAsync<CharacterProjectEnvelope>(stream, DocumentJson.Options, cancellationToken)
            .ConfigureAwait(false);
        if (envelope?.SchemaVersion != 1 || envelope.Project.ValueKind != JsonValueKind.Object ||
            !string.Equals(envelope.ContentSha256, Hash(envelope.Project), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Tệp hồ sơ nhân vật sai định dạng hoặc checksum không khớp.");
        var project = envelope.Project.Deserialize<CharacterProject>(DocumentJson.Options)
            ?? throw new InvalidDataException("Tệp không có dự án nhân vật.");
        project.Validate();
        return project;
    }

    public static CharacterProject Clone(CharacterProject project) =>
        JsonSerializer.Deserialize<CharacterProject>(JsonSerializer.Serialize(project, DocumentJson.Options), DocumentJson.Options)!;

    private static string Hash(JsonElement content) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(content, DocumentJson.Options))));

    private sealed class CharacterProjectEnvelope
    {
        public int SchemaVersion { get; set; } = 1;
        public string ContentSha256 { get; set; } = "";
        public JsonElement Project { get; set; }
    }
}
