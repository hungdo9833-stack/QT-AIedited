using System.IO;
using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Wpf;

internal enum ShellTheme { Dark, Light }

internal sealed class WorkspaceLayoutPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public ShellTheme Theme { get; set; } = ShellTheme.Dark;
    public bool InspectorVisible { get; set; } = true;
    public bool ProjectSidebarVisible { get; set; } = true;
    public bool FourPaneMode { get; set; }
    public double InspectorWidth { get; set; } = 300;
    public double ProjectSidebarWidth { get; set; } = 290;
    public double TwoPaneSourceStar { get; set; } = 1;
    public double SegmentNavigatorWidth { get; set; } = 250;
    public double FourPaneLeftStar { get; set; } = 1;
    public double FourPaneTopStar { get; set; } = 1;

    public void Validate()
    {
        if (SchemaVersion != 1 || !Enum.IsDefined(Theme) || InspectorWidth is < 240 or > 600 ||
            ProjectSidebarWidth is < 220 or > 520 || TwoPaneSourceStar is < 0.25 or > 4 ||
            SegmentNavigatorWidth is < 180 or > 520 || FourPaneLeftStar is < 0.25 or > 4 ||
            FourPaneTopStar is < 0.25 or > 4)
            throw new InvalidDataException("Bố cục QT Studio không hợp lệ.");
    }
}

internal sealed class WorkspaceLayoutStore(string? path = null)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "QuickTranslatorAiEditor", "workspace-layout.json");
    public string PathName { get; } = Path.GetFullPath(path ?? DefaultPath);

    public async Task<WorkspaceLayoutPreferences> LoadAsync(CancellationToken cancellationToken = default)
    {
        var read = await RecoveryFiles.ReadAsync(PathName, Decode, () => new WorkspaceLayoutPreferences(), cancellationToken)
            .ConfigureAwait(false);
        return read.Value;
    }

    public Task SaveAsync(WorkspaceLayoutPreferences value, CancellationToken cancellationToken = default)
    {
        value.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value,
            new JsonSerializerOptions(DocumentJson.Options) { WriteIndented = true });
        return RecoveryFiles.WriteAsync(PathName, bytes, Decode, cancellationToken);
    }

    private static WorkspaceLayoutPreferences Decode(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("schemaVersion", out _) ||
            !document.RootElement.TryGetProperty("theme", out _) ||
            !document.RootElement.TryGetProperty("inspectorVisible", out _))
            throw new InvalidDataException("Bố cục QT Studio thiếu trường bắt buộc.");
        WorkspaceLayoutPreferences value = JsonSerializer.Deserialize<WorkspaceLayoutPreferences>(bytes, DocumentJson.Options)
            ?? throw new InvalidDataException("Bố cục QT Studio trống.");
        value.Validate();
        return value;
    }
}
