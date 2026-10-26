namespace QuickTranslator.AiEditor.Core.Models;

public sealed record SegmenterOptions
{
    /// <summary>Keep the complete chapter body in one segment, ignoring the numeric segment-size limits.</summary>
    public bool WholeChapter { get; init; }
    public int MinimumHanCharacters { get; init; } = 300;
    public int TargetHanCharacters { get; init; } = 400;
    public int MaximumHanCharacters { get; init; } = 500;
    // Also bounds very long passages containing few or no Han characters.
    public int MaximumTextElements { get; init; } = 2000;

    public void Validate()
    {
        if (MinimumHanCharacters < 1 || MinimumHanCharacters > TargetHanCharacters ||
            TargetHanCharacters > MaximumHanCharacters || MaximumHanCharacters > 10000 ||
            MaximumTextElements < MaximumHanCharacters || MaximumTextElements > 50000)
            throw new ArgumentException("Kích thước đoạn không hợp lệ: tối thiểu ≤ mục tiêu ≤ tối đa; giới hạn ký tự tổng phải ≥ tối đa.");
    }
}
