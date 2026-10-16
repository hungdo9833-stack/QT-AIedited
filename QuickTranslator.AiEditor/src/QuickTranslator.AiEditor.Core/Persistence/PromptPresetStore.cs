using System.Text.Json;
using QuickTranslator.AiEditor.Core.Translation;
namespace QuickTranslator.AiEditor.Core.Persistence;
public sealed class PromptPresetStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "PromptPresets", "presets.qtaip.json");
    public Task<PresetLibrary> LoadAsync(CancellationToken cancellationToken = default) =>
        SimpleFileStorage.ReadOptionalAsync(path, Decode, () => new PresetLibrary(), cancellationToken);
    public async Task SaveAsync(PresetLibrary library, CancellationToken cancellationToken = default)
    {
        library.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(library, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SimpleFileStorage.WriteAsync(path, bytes, Decode, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private static PresetLibrary Decode(byte[] bytes) => JsonFileCodec.DecodeDirect<PresetLibrary>(bytes,
        value => value.Validate(), "schemaVersion", "presets", "selectedId");
}
