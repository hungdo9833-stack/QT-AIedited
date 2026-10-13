namespace QuickTranslator.AiEditor.Core.Models;

public sealed record ResegmentationPreview(
    string ExpectedDocumentSha256,
    SegmenterOptions Options,
    int OldSegmentCount,
    int NewSegmentCount,
    int RetainedSegmentCount,
    int ProtectedSegmentCount,
    int ResegmentedRegionCount,
    bool BoundariesChanged,
    IReadOnlyList<string> Warnings);

public sealed record ProjectResegmentationItem(Guid ChapterId, string Title,
    ResegmentationPreview Preview);

public sealed record ProjectResegmentationPreview(IReadOnlyList<ProjectResegmentationItem> Chapters)
{
    public int ChangedChapters => Chapters.Count(item => item.Preview.BoundariesChanged);
    public int ProtectedChapters => Chapters.Count(item => item.Preview.ProtectedSegmentCount > 0);
    public int WarningCount => Chapters.Sum(item => item.Preview.Warnings.Count);
}
