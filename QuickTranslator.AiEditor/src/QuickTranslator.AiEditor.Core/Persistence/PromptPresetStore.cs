using System.Text.Json;
using QuickTranslator.AiEditor.Core.Translation;
namespace QuickTranslator.AiEditor.Core.Persistence;
public sealed class PromptPresetStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "PromptPresets", "presets.qtaip.json");
    public RecoveryInfo? LastRecovery { get; private set; }
    public bool RecoveredFromBackup => LastRecovery?.Source == RecoverySource.Backup;
    public async Task<PresetLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        var loaded = await RecoveryFiles.ReadAsync(path, Decode, () => new PresetLibrary(), cancellationToken).ConfigureAwait(false);
        LastRecovery = loaded.Info; return loaded.Value;
    }
    public async Task SaveAsync(PresetLibrary library, CancellationToken cancellationToken = default)
    {
        library.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(library, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { LastRecovery = await RecoveryFiles.WriteAsync(path, bytes, Decode, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private static PresetLibrary Decode(byte[] bytes) => RecoveryFiles.DecodeJson<PresetLibrary>(bytes,
        value => value.Validate(), "schemaVersion", "presets", "selectedId");
    public async Task RestoreBackupAsync(CancellationToken cancellationToken = default) =>
        LastRecovery = await RecoveryFiles.RestoreBackupAsync(LastRecovery ?? throw new InvalidOperationException("Hãy đọc preview trước."), Decode, cancellationToken).ConfigureAwait(false);
}
