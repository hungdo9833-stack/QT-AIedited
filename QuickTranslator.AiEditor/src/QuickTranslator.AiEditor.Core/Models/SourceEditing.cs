namespace QuickTranslator.AiEditor.Core.Models;

public sealed class SourceEditRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset ChangedAt { get; set; }
    public long PreviousSourceRevision { get; set; }
    public string Description { get; set; } = "";
    public string PreviousOriginalText { get; set; } = "";
    public List<TranslationSegment> PreviousSegments { get; set; } = [];
    public List<SegmentationChange> PreviousSegmentationHistory { get; set; } = [];
    public QueueCheckpoint PreviousQueue { get; set; } = new();
    public string PreviousSourceSha256 { get; set; } = "";
    public string NewSourceSha256 { get; set; } = "";
}

public sealed record SourceTextChange(int OldStartUtf16, int OldLengthUtf16,
    int NewStartUtf16, int NewLengthUtf16, string RemovedText, string AddedText);

public sealed record SourceEditPreview(
    long ExpectedSourceRevision,
    string ExpectedSourceSha256,
    string NewSourceSha256,
    string NewSourceText,
    SegmenterOptions SegmentationOptions,
    SourceTextChange Change,
    int OldSegmentCount,
    int NewSegmentCount,
    int RetainedSegmentCount,
    int AffectedSegmentCount,
    int ProtectedAffectedSegmentCount,
    bool ReplacesMostOfSource);
