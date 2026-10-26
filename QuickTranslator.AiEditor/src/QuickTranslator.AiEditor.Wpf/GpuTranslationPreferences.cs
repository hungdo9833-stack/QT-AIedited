using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickTranslator.AiEditor.Wpf;

internal enum TranslationSource { Gpu, Api }
internal enum ApiTranslationProvider { Gemini }
internal enum GpuTranslationMode { LocalOnly, RemoteOnly, ParallelLocalRemote }

internal sealed class GpuTranslationPreferences
{
    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuickTranslator.AiEditor", "translation-source.json");
    private static JsonSerializerOptions JsonOptions(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: true));
        return options;
    }

    public TranslationSource Source { get; set; } = TranslationSource.Gpu;
    public ApiTranslationProvider ApiProvider { get; set; } = ApiTranslationProvider.Gemini;
    public GpuTranslationMode Mode { get; set; }
    // Session-only: a persisted flag would make every restart ignore a recovered job's workers.
    [JsonIgnore] public bool ModeExplicitlySelected { get; set; }
    public Guid? RemoteProfileId { get; set; }

    public static GpuTranslationPreferences Load(string? path = null)
    {
        string file = path ?? DefaultPath;
        if (!File.Exists(file)) return new();
        try
        {
            return JsonSerializer.Deserialize<GpuTranslationPreferences>(File.ReadAllBytes(file), JsonOptions())
                ?? new();
        }
        catch (JsonException) { return new(); }
    }

    public void Save(string? path = null)
    {
        string file = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions(indented: true));
        string temporary = file + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, file, true);
    }
}
