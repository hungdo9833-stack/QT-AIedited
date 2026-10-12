using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

// One atomic resource bundle: chapter/manifest schema versions stay unchanged.
public sealed class ProjectResources
{
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public CharacterProject Characters { get; set; } = new();
    public List<ConfirmedTerm> Terms { get; set; } = [];
    public string Context { get; set; } = "";

    public ProjectResources Clone() => JsonSerializer.Deserialize<ProjectResources>(
        JsonSerializer.Serialize(this, DocumentJson.Options), DocumentJson.Options)!;

    public void Validate()
    {
        if (ProjectId == Guid.Empty || Revision < 0 || Characters is null || Characters.Id != ProjectId ||
            Terms is null || Terms.Count > 1000 || Terms.Any(t => t is null ||
                string.IsNullOrWhiteSpace(t.Chinese) || string.IsNullOrWhiteSpace(t.Vietnamese)) ||
            Terms.Select(t => t.Chinese).Distinct(StringComparer.Ordinal).Count() != Terms.Count ||
            Context is null || Context.Length > 4000)
            throw new InvalidDataException("Tài nguyên dự án không hợp lệ hoặc thuộc dự án khác.");
        Characters.Validate();
    }

    internal void ApplyTo(ChapterDocument chapter)
    {
        chapter.CharacterProject = CharacterProjectStore.Clone(Characters);
        chapter.ConfirmedTerms = Terms.ToList();
        chapter.ConfirmedContextNotes = Context;
    }

    public ChapterDocument CreateWorkingChapter(ChapterDocument chapter)
    {
        if (chapter.ProjectId != ProjectId) throw new InvalidOperationException("Chương không thuộc project sở hữu tài nguyên.");
        var snapshot = DocumentJson.Clone(chapter);
        ApplyTo(snapshot);
        return snapshot;
    }
}

public sealed class ProjectResourceStore(ProjectSession session)
{
    public string PathName => session.Resolve("Settings/resources.json");

    public RecoveryInfo? LastRecovery { get; private set; }
    private readonly List<RecoveryInfo> _sources = [];
    public IReadOnlyList<RecoveryInfo> RecoverySources => _sources;
    public async Task RestoreRecoveredBackupsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var info in _sources.Where(x => x.Source == RecoverySource.Backup).ToArray())
        {
            if (info.PrimaryPath == PathName) LastRecovery = await RecoveryFiles.RestoreBackupAsync(info, Decode, cancellationToken).ConfigureAwait(false);
            else if (info.PrimaryPath == session.Resolve(session.Manifest.Files.CharacterProfiles))
            {
                var characters = new CharacterProjectStore(Path.GetDirectoryName(info.PrimaryPath)!);
                await characters.LoadAsync(info.PrimaryPath, cancellationToken).ConfigureAwait(false);
                await characters.RestoreBackupAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var settings = new ProjectSettingsStore(session);
                await settings.LoadGlossaryAsync(cancellationToken).ConfigureAwait(false);
                await settings.LoadContextNotesAsync(cancellationToken).ConfigureAwait(false);
                await settings.RestoreRecoveredBackupsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
    public async Task<ProjectResources> LoadAsync(CancellationToken cancellationToken = default)
    {
        _sources.Clear();
        var canonical = await RecoveryFiles.ReadAsync(PathName, Decode, () => (ProjectResources)null!, cancellationToken).ConfigureAwait(false);
        LastRecovery = canonical.Info; _sources.Add(canonical.Info);
        if (canonical.Info.Source != RecoverySource.Missing) return canonical.Value;
        var settings = new ProjectSettingsStore(session);
        string characterPath = session.Resolve(session.Manifest.Files.CharacterProfiles);
        var characterStore = new CharacterProjectStore(Path.GetDirectoryName(characterPath)!);
        CharacterProject characters;
        try { characters = await characterStore.LoadAsync(characterPath, cancellationToken).ConfigureAwait(false); }
        catch (FileNotFoundException) { characters = new CharacterProject { Name = session.Manifest.VietnameseTitle }; }
        if (characterStore.LastRecovery is not null) _sources.Add(characterStore.LastRecovery);
        characters.Id = session.Manifest.ProjectId;
        var legacy = new ProjectResources { ProjectId = session.Manifest.ProjectId, Characters = characters,
            Terms = (await settings.LoadGlossaryAsync(cancellationToken).ConfigureAwait(false)).Terms,
            Context = await settings.LoadContextNotesAsync(cancellationToken).ConfigureAwait(false) };
        _sources.AddRange(settings.RecoverySources); legacy.Validate(); return legacy;
    }
    public async Task<ProjectResources> SaveAsync(ProjectResources proposed, CancellationToken cancellationToken = default)
    {
        var saved = proposed.Clone();
        if (saved.ProjectId != session.Manifest.ProjectId) throw new InvalidOperationException("Không ghi tài nguyên của project khác.");
        saved.ProjectId = session.Manifest.ProjectId;
        saved.Characters.Id = saved.ProjectId;
        saved.Validate();
        await session.ResourceGate.WaitAsync(cancellationToken);
        try
        {
            var current = await LoadAsync(cancellationToken);
            if (saved.Revision != current.Revision)
                throw new InvalidOperationException("Tài nguyên dự án đã đổi sau khi nạp. Hãy nạp lại trước khi lưu; không hạ revision.");
            var oldNames = current.Characters.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.ChineseName));
            foreach (var old in oldNames)
                if (!saved.Characters.Profiles.Any(p => p.ChineseName == old.ChineseName && p.VietnameseName == old.VietnameseName))
                    saved.Terms.RemoveAll(t => t.Chinese == old.ChineseName && t.Vietnamese == old.VietnameseName);
            foreach (var profile in saved.Characters.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.ChineseName)))
            {
                var term = saved.Terms.SingleOrDefault(t => t.Chinese == profile.ChineseName);
                if (term is not null && term.Vietnamese != profile.VietnameseName)
                    throw new InvalidOperationException($"Tên {profile.ChineseName} có ánh xạ glossary khác; cần xác nhận trước khi lưu.");
                if (term is null) saved.Terms.Add(new(profile.ChineseName, profile.VietnameseName));
            }
            saved.Characters.UpdatedAt = current.Characters.UpdatedAt;
            saved.Validate();
            if (LastRecovery?.Source != RecoverySource.Missing && JsonSerializer.Serialize(saved, DocumentJson.Options) == JsonSerializer.Serialize(current, DocumentJson.Options))
            {
                if (LastRecovery?.Source == RecoverySource.Backup)
                {
                    var unchanged = JsonSerializer.SerializeToElement(current, DocumentJson.Options);
                    LastRecovery = await RecoveryFiles.WriteAsync(PathName, JsonSerializer.SerializeToUtf8Bytes(new Envelope { Content = unchanged, Sha256 = Hash(unchanged) },
                        new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true }), Decode, cancellationToken).ConfigureAwait(false);
                }
                return current;
            }
            saved.Revision = checked(current.Revision + 1);
            saved.Characters.UpdatedAt = DateTimeOffset.UtcNow;
            var content = JsonSerializer.SerializeToElement(saved, DocumentJson.Options);
            var envelope = new Envelope { Content = content, Sha256 = Hash(content) };
            if (session.RecoveryReport.RequiresDecision) throw new InvalidOperationException("Project có transaction cần phục hồi; không ghi resources.");
            LastRecovery = await RecoveryFiles.WriteAsync(PathName, JsonSerializer.SerializeToUtf8Bytes(envelope,
                new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true }), Decode, cancellationToken).ConfigureAwait(false);            return saved.Clone();
        }
        finally { session.ResourceGate.Release(); }
    }

    private ProjectResources Decode(byte[] bytes)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(bytes, DocumentJson.Options);        if (envelope is null || envelope.SchemaVersion != 1 || envelope.Content.ValueKind != JsonValueKind.Object ||
            envelope.Sha256 != Hash(envelope.Content)) throw new InvalidDataException("Tài nguyên dự án sai checksum/định dạng.");
        var resources = envelope.Content.Deserialize<ProjectResources>(DocumentJson.Options) ?? throw new InvalidDataException("Thiếu tài nguyên.");
        resources.Validate();
        if (resources.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Tài nguyên thuộc dự án khác.");
        return resources;
    }
    private static string Hash(JsonElement value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, DocumentJson.Options))));
    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or JsonException or ArgumentException;
    private sealed class Envelope
    {
        public int SchemaVersion { get; set; } = 1;
        public string Sha256 { get; set; } = "";
        public JsonElement Content { get; set; }
    }
}
