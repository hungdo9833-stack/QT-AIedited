using System.Text.Json;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed record TraditionalEditorPreferences
{
    public int SchemaVersion { get; init; } = 1;
    public string DataDirectory { get; init; } = "";
    public bool PrioritizeNames { get; init; } = true;
    public int TranslationAlgorithm { get; init; }
    public bool EnableAlignmentDiagnostics { get; init; }

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("Phiên bản thiết lập dịch truyền thống chưa được hỗ trợ.");
        if (DataDirectory is null) throw new InvalidDataException("Thiếu đường dẫn Data.");
        if (DataDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new InvalidDataException("Đường dẫn Data chứa ký tự không hợp lệ.");
        if (TranslationAlgorithm is < 0 or > 2) throw new InvalidDataException("Thuật toán tách cụm không hợp lệ.");
    }
}

public sealed class TraditionalEditorPreferencesStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "Traditional", "traditional-settings.json");

    public RecoveryInfo? LastRecovery { get; private set; }
    public async Task<TraditionalEditorPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await RecoveryFiles.ReadAsync(path, Decode, () => new TraditionalEditorPreferences(), cancellationToken).ConfigureAwait(false);
        LastRecovery = loaded.Info; return loaded.Value;
    }
    private static TraditionalEditorPreferences Decode(byte[] bytes) => RecoveryFiles.DecodeJson<TraditionalEditorPreferences>(bytes,
        value => value.Validate(), "schemaVersion", "dataDirectory");
    public async Task RestoreBackupAsync(CancellationToken cancellationToken = default) =>
        LastRecovery = await RecoveryFiles.RestoreBackupAsync(LastRecovery ?? throw new InvalidOperationException("Hãy đọc preview trước."), Decode, cancellationToken).ConfigureAwait(false);
    public async Task SaveAsync(TraditionalEditorPreferences value, CancellationToken cancellationToken = default)
    {
        value.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { LastRecovery = await RecoveryFiles.WriteAsync(path, bytes, Decode, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
