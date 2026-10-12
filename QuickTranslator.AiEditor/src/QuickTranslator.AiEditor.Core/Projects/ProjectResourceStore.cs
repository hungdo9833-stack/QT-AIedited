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

    public async Task<ProjectResources> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(PathName) && !File.Exists(PathName + ".bak"))
        {
            // Import only project-owned legacy stores; never promote a chapter snapshot automatically.
            var settings = new ProjectSettingsStore(session);
            string characterPath = session.Resolve(session.Manifest.Files.CharacterProfiles);
            var characters = File.Exists(characterPath)
                ? await new CharacterProjectStore(System.IO.Path.GetDirectoryName(characterPath)!).LoadAsync(characterPath, cancellationToken)
                : new CharacterProject { Name = session.Manifest.VietnameseTitle };
            characters.Id = session.Manifest.ProjectId;
            var legacy = new ProjectResources { ProjectId = session.Manifest.ProjectId, Characters = characters,
                Terms = (await settings.LoadGlossaryAsync(cancellationToken)).Terms,
                Context = await settings.LoadContextNotesAsync(cancellationToken) };
            legacy.Validate();
            return legacy;
        }
        try { return await ReadAsync(PathName, cancellationToken); }
        catch (Exception ex) when (Recoverable(ex))
        {
            try { return await ReadAsync(PathName + ".bak", cancellationToken); }
            catch (Exception backup) when (Recoverable(backup))
            { throw new InvalidDataException("Không đọc được tài nguyên dự án và bản dự phòng; không dùng snapshot chương để ghi đè.", new AggregateException(ex, backup)); }
        }
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
            if (File.Exists(PathName) && JsonSerializer.Serialize(saved, DocumentJson.Options) == JsonSerializer.Serialize(current, DocumentJson.Options))
                return current;
            saved.Revision = checked(current.Revision + 1);
            saved.Characters.UpdatedAt = DateTimeOffset.UtcNow;
            var content = JsonSerializer.SerializeToElement(saved, DocumentJson.Options);
            var envelope = new Envelope { Content = content, Sha256 = Hash(content) };
            bool damaged = false;
            if (File.Exists(PathName))
                try { _ = await ReadAsync(PathName, cancellationToken); }
                catch (Exception ex) when (Recoverable(ex)) { damaged = true; }
            await AtomicLocalFile.WriteAsync(PathName, JsonSerializer.SerializeToUtf8Bytes(envelope,
                new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true }), cancellationToken, damaged);
            return saved.Clone();
        }
        finally { session.ResourceGate.Release(); }
    }

    private async Task<ProjectResources> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(await File.ReadAllBytesAsync(path, cancellationToken), DocumentJson.Options);
        if (envelope is null || envelope.SchemaVersion != 1 || envelope.Content.ValueKind != JsonValueKind.Object ||
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
