using System.Text.Json;
using QuickTranslator.AiEditor.Core.Reading;

namespace QuickTranslator.AiEditor.Core.Persistence;

public sealed class ReaderPreferencesStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslatorAiEditor", "Reader", "reader-settings.json");
    public Task<ReaderPreferences> LoadAsync(CancellationToken cancellationToken = default) =>
        SimpleFileStorage.ReadOptionalAsync(path, Decode, () => new ReaderPreferences(), cancellationToken);
    public async Task SaveAsync(ReaderPreferences preferences, CancellationToken cancellationToken = default)
    {
        preferences.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(preferences, new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SimpleFileStorage.WriteAsync(path, bytes, Decode, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private static ReaderPreferences Decode(byte[] bytes) => JsonFileCodec.DecodeDirect<ReaderPreferences>(bytes,
        value => value.Validate(), "schemaVersion", "theme", "density", "fontFamily", "fontSize", "lineHeight",
        "paragraphSpacing", "pageMargin", "tableOfContentsVisible", "collapseExtraBlankLinesForDisplay",
        "pageWidth", "textAlignment", "firstLineIndent", "chapterPositions");
}
