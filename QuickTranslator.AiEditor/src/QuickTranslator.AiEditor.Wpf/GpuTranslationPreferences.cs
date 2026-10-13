namespace QuickTranslator.AiEditor.Wpf;

internal enum GpuTranslationMode { LocalOnly, RemoteOnly, ParallelLocalRemote }

internal sealed class GpuTranslationPreferences
{
    public GpuTranslationMode Mode { get; set; }
    public bool ModeExplicitlySelected { get; set; }
    public Guid? RemoteProfileId { get; set; }
}
