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
        if (DataDirectory.IndexOfAny(Path.GetInvalidPathChars()) >= 0) throw new InvalidDataException("Đường dẫn Data chứa ký tự không hợp lệ.");
        if (TranslationAlgorithm is < 0 or > 2) throw new InvalidDataException("Thuật toán tách cụm không hợp lệ.");
    }
}

public sealed class TraditionalEditorPreferencesStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "Traditional", "traditional-settings.json");

    public async Task<TraditionalEditorPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return new();
        await using var stream = File.OpenRead(path);
        var value = await JsonSerializer.DeserializeAsync<TraditionalEditorPreferences>(stream, DocumentJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Tệp thiết lập dịch truyền thống rỗng.");
        value.Validate();
        return value;
    }

    public async Task SaveAsync(TraditionalEditorPreferences value, CancellationToken cancellationToken = default)
    {
        value.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AtomicLocalFile.WriteAsync(path, bytes, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
}
