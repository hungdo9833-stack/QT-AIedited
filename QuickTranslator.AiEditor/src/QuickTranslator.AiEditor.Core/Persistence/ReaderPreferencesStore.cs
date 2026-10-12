using System.Text.Json;
using QuickTranslator.AiEditor.Core.Reading;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed class ReaderPreferencesStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "Reader", "reader-settings.json");
    public bool RecoveredFromBackup { get; private set; }

    public async Task<ReaderPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        RecoveredFromBackup = false;
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new();
        try { return await ReadAsync(path, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (Recoverable(ex))
        {
            try
            {
                var recovered = await ReadAsync(path + ".bak", cancellationToken).ConfigureAwait(false);
                RecoveredFromBackup = true;
                return recovered;
            }
            catch (Exception backupError) when (Recoverable(backupError))
            {
                throw new InvalidDataException("Không đọc được thiết lập Reader và bản dự phòng; tệp cũ được giữ nguyên.",
                    new AggregateException(ex, backupError));
            }
        }
    }

    public async Task SaveAsync(ReaderPreferences preferences, CancellationToken cancellationToken = default)
    {
        preferences.Validate();
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(preferences,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AtomicLocalFile.WriteAsync(path, data, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private static async Task<ReaderPreferences> ReadAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        var preferences = await JsonSerializer.DeserializeAsync<ReaderPreferences>(stream, DocumentJson.Options, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Tệp thiết lập Reader rỗng.");
        preferences.Validate();
        return preferences;
    }

    private static bool Recoverable(Exception error) =>
        error is IOException or JsonException or InvalidDataException or ArgumentException;
}
