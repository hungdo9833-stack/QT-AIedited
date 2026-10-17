using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// User-owned runtime settings for one concrete backend/GPU. Project and chapter
/// documents may retain historical snapshots, but never own or overwrite this profile.
/// </summary>
public sealed class AiRuntimeProfile
{
    public ModelOptions ModelOptions { get; set; } = new();
    public SegmenterOptions SegmentationOptions { get; set; } = new();

    public static AiRuntimeProfile CreateDefault(string model) => new()
    {
        ModelOptions = new ModelOptions { Model = model },
        SegmentationOptions = new SegmenterOptions()
    };

    public void Validate()
    {
        if (ModelOptions is null || SegmentationOptions is null)
            throw new InvalidDataException("AI Runtime Profile thiếu ModelOptions hoặc SegmentationOptions.");
        ModelOptions.Validate();
        SegmentationOptions.Validate();
    }

    public AiRuntimeProfile Clone() => new()
    {
        ModelOptions = ModelOptions with { },
        SegmentationOptions = SegmentationOptions with { }
    };
}
