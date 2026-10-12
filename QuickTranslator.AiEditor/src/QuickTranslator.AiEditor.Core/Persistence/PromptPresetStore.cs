using System.Text.Json;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed class PromptPresetStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "PromptPresets", "presets.qtaip.json");
    public bool RecoveredFromBackup { get; private set; }

    public async Task<PresetLibrary> LoadAsync(CancellationToken cancellationToken = default)
    {
        RecoveredFromBackup = false;
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new();
        try { RecoveredFromBackup = false; return await ReadAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (Recoverable(ex))
        {
            try { var loaded = await ReadAsync(path + ".bak", cancellationToken).ConfigureAwait(false); RecoveredFromBackup = true; return loaded; }
            catch (Exception backupError) when (Recoverable(backupError))
            { throw new InvalidDataException("Không đọc được preset và bản dự phòng; giữ các tệp để phục hồi, không ghi đè bằng preset rỗng.", new AggregateException(ex, backupError)); }
        }
    }

    public async Task SaveAsync(PresetLibrary library, CancellationToken cancellationToken = default)
    {
        library.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(library, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool damaged = false;
            if (File.Exists(path))
                try { await ReadAsync(path, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (Recoverable(ex)) { damaged = true; }
            await AtomicLocalFile.WriteAsync(path, bytes, cancellationToken, preserveExistingBackup: damaged).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private static async Task<PresetLibrary> ReadAsync(string file, CancellationToken ct)
    {
        await using var stream = File.OpenRead(file);
        var library = await JsonSerializer.DeserializeAsync<PresetLibrary>(stream, DocumentJson.Options, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Preset không có dữ liệu.");
        library.Validate();
        return library;
    }
    private static bool Recoverable(Exception ex) => ex is IOException or InvalidDataException or JsonException or ArgumentException;
}
