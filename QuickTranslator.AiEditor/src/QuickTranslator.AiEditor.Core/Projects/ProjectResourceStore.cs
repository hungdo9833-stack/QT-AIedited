using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Projects;

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
        ChapterDocument snapshot = DocumentJson.Clone(chapter);
        ApplyTo(snapshot);
        return snapshot;
    }
}

public sealed class ProjectResourceStore(ProjectSession session)
{
    public string PathName => session.Resolve(session.Manifest.Files.Resources);

    public async Task<ProjectResources> LoadAsync(CancellationToken cancellationToken = default)
    {
        return await SimpleFileStorage.ReadOptionalAsync(PathName, Decode, () =>
        {
            var resources = new ProjectResources
            {
                ProjectId = session.Manifest.ProjectId,
                Characters = new CharacterProject
                {
                    Id = session.Manifest.ProjectId,
                    Name = session.Manifest.VietnameseTitle
                }
            };
            resources.Validate();
            return resources;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectResources> SaveAsync(ProjectResources proposed,
        CancellationToken cancellationToken = default)
    {
        ProjectResources saved = proposed.Clone();
        if (saved.ProjectId != session.Manifest.ProjectId)
            throw new InvalidOperationException("Không ghi tài nguyên của project khác.");
        saved.Characters.Id = saved.ProjectId;
        saved.Validate();
        await session.ResourceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectResources current = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (saved.Revision != current.Revision)
                throw new InvalidOperationException("Tài nguyên dự án đã đổi sau khi nạp. Hãy nạp lại trước khi lưu.");
            foreach (CharacterProfile old in current.Characters.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.ChineseName)))
                if (!saved.Characters.Profiles.Any(p => p.ChineseName == old.ChineseName && p.VietnameseName == old.VietnameseName))
                    saved.Terms.RemoveAll(t => t.Chinese == old.ChineseName && t.Vietnamese == old.VietnameseName);
            foreach (CharacterProfile profile in saved.Characters.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.ChineseName)))
            {
                ConfirmedTerm? term = saved.Terms.SingleOrDefault(t => t.Chinese == profile.ChineseName);
                if (term is not null && term.Vietnamese != profile.VietnameseName)
                    throw new InvalidOperationException($"Tên {profile.ChineseName} có ánh xạ glossary khác; cần xác nhận trước khi lưu.");
                if (term is null) saved.Terms.Add(new(profile.ChineseName, profile.VietnameseName));
            }
            saved.Characters.UpdatedAt = current.Characters.UpdatedAt;
            if (File.Exists(PathName) &&
                JsonSerializer.Serialize(saved, DocumentJson.Options) == JsonSerializer.Serialize(current, DocumentJson.Options))
                return current;
            saved.Revision = checked(current.Revision + 1);
            saved.Characters.UpdatedAt = DateTimeOffset.UtcNow;
            saved.Validate();
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(saved,
                new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
            await SimpleFileStorage.WriteAsync(PathName, bytes, Decode, cancellationToken).ConfigureAwait(false);
            return saved.Clone();
        }
        finally { session.ResourceGate.Release(); }
    }

    private ProjectResources Decode(byte[] bytes)
    {
        ProjectResources resources = JsonFileCodec.DecodeDirect<ProjectResources>(
            bytes, value => value.Validate(), "projectId", "characters", "terms");
        if (resources.ProjectId != session.Manifest.ProjectId)
            throw new InvalidDataException("Tài nguyên thuộc dự án khác.");
        return resources;
    }
}
