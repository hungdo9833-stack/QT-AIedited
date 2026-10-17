using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class BookIntelligenceStore(ProjectSession session)
{
    public string PathName => session.Resolve("Settings/ai-book-intelligence.json");
    public async Task<BookIntelligenceState> LoadAsync(CancellationToken cancellationToken = default)
    {
        BookIntelligenceState read = await SimpleFileStorage.ReadOptionalAsync(PathName, Decode,
            () => new BookIntelligenceState
            {
                ProjectId = session.Manifest.ProjectId,
                UpdatedAt = DateTimeOffset.UtcNow,
                AutoApplyVerified = false
            }, cancellationToken).ConfigureAwait(false);
        if (read.ProjectId != session.Manifest.ProjectId)
            throw new InvalidDataException("Chỉ mục khảo sát thuộc Project khác.");
        read.Validate();
        return read;
    }

    public async Task<BookIntelligenceState> SaveAsync(BookIntelligenceState proposed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        BookIntelligenceState saved = Clone(proposed);
        saved.Validate();
        if (saved.ProjectId != session.Manifest.ProjectId)
            throw new InvalidOperationException("Không ghi kết quả khảo sát của Project khác.");
        BookIntelligenceState current = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (saved.Revision != current.Revision)
            throw new InvalidOperationException("Hàng chờ khảo sát đã đổi sau khi nạp. Hãy nạp lại trước khi lưu.");
        saved.Revision = checked(current.Revision + 1);
        saved.UpdatedAt = DateTimeOffset.UtcNow;
        saved.AutoApplyVerified = false;
        saved.Validate();
        await SimpleFileStorage.WriteAsync(PathName,
            JsonSerializer.SerializeToUtf8Bytes(saved, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true }),
            Decode, cancellationToken).ConfigureAwait(false);
        return Clone(saved);
    }

    public static BookIntelligenceState Clone(BookIntelligenceState state) =>
        JsonSerializer.Deserialize<BookIntelligenceState>(JsonSerializer.Serialize(state, DocumentJson.Options), DocumentJson.Options)!;

    private BookIntelligenceState Decode(byte[] bytes)
    {
        BookIntelligenceState state = JsonFileCodec.DecodeDirect<BookIntelligenceState>(
            bytes, _ => { }, "schemaVersion", "projectId", "chapters", "characters", "glossary");
        state.Validate();
        return state;
    }
}
