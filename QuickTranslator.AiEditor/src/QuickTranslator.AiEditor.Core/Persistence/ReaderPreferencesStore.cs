using System.Text.Json;
using QuickTranslator.AiEditor.Core.Reading;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed class ReaderPreferencesStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "Reader", "reader-settings.json");
    public RecoveryInfo? LastRecovery { get; private set; }
    public bool RecoveredFromBackup => LastRecovery?.Source == RecoverySource.Backup;
    public async Task<ReaderPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await RecoveryFiles.ReadAsync(path, Decode, () => new ReaderPreferences(), cancellationToken).ConfigureAwait(false);
        LastRecovery = loaded.Info; return loaded.Value;
    }
    public async Task SaveAsync(ReaderPreferences preferences, CancellationToken cancellationToken = default)
    {
        preferences.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(preferences, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { LastRecovery = await RecoveryFiles.WriteAsync(path, bytes, Decode, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private static ReaderPreferences Decode(byte[] bytes) => RecoveryFiles.DecodeJson<ReaderPreferences>(bytes,
        value => value.Validate(), "schemaVersion", "fontFamily", "fontSize", "chapterPositions");
    public async Task RestoreBackupAsync(CancellationToken cancellationToken = default) =>
        LastRecovery = await RecoveryFiles.RestoreBackupAsync(LastRecovery ?? throw new InvalidOperationException("Hãy đọc preview trước."), Decode, cancellationToken).ConfigureAwait(false);
}
