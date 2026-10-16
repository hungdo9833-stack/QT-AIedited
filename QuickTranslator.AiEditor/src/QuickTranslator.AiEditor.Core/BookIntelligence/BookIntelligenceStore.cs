using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class BookIntelligenceStore(ProjectSession session)
{
    public string PathName => session.Resolve("Settings/ai-book-intelligence.json");
    public RecoveryInfo? LastRecovery { get; private set; }

    public async Task<BookIntelligenceState> LoadAsync(CancellationToken cancellationToken = default)
    {
        RecoveryRead<BookIntelligenceState> read = await RecoveryFiles.ReadAsync(PathName, Decode,
            () => new BookIntelligenceState
            {
                ProjectId = session.Manifest.ProjectId,
                UpdatedAt = DateTimeOffset.UtcNow,
                AutoApplyVerified = false
            }, cancellationToken).ConfigureAwait(false);
        LastRecovery = read.Info;
        if (read.Value.ProjectId != session.Manifest.ProjectId)
            throw new InvalidDataException("Chỉ mục khảo sát thuộc Project khác.");
        read.Value.Validate();
        return read.Value;
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
        JsonElement content = JsonSerializer.SerializeToElement(saved, DocumentJson.Options);
        var envelope = new Envelope { Content = content, Sha256 = Hash(content) };
        LastRecovery = await RecoveryFiles.WriteAsync(PathName,
            JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true }),
            Decode, cancellationToken).ConfigureAwait(false);
        return Clone(saved);
    }

    public static BookIntelligenceState Clone(BookIntelligenceState state) =>
        JsonSerializer.Deserialize<BookIntelligenceState>(JsonSerializer.Serialize(state, DocumentJson.Options), DocumentJson.Options)!;

    private BookIntelligenceState Decode(byte[] bytes)
    {
        Envelope envelope = JsonSerializer.Deserialize<Envelope>(bytes, DocumentJson.Options)
            ?? throw new InvalidDataException("File khảo sát trống.");
        if (envelope.SchemaVersion != 1 || envelope.Content.ValueKind != JsonValueKind.Object ||
            !string.Equals(envelope.Sha256, Hash(envelope.Content), StringComparison.Ordinal))
            throw new InvalidDataException("File khảo sát sai checksum hoặc schema.");
        BookIntelligenceState state = envelope.Content.Deserialize<BookIntelligenceState>(DocumentJson.Options)
            ?? throw new InvalidDataException("File khảo sát thiếu nội dung.");
        state.Validate();
        return state;
    }

    private static string Hash(JsonElement value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, DocumentJson.Options))));

    private sealed class Envelope
    {
        public int SchemaVersion { get; set; } = 1;
        public string Sha256 { get; set; } = "";
        public JsonElement Content { get; set; }
    }
}
