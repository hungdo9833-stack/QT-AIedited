using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

// Only immutable values escape the service lock; no document/history collections are exposed.
public sealed record SegmentEditorState(Guid Id, SegmentStatus Status, int AttemptCount,
    bool HasEditableText, int Order, int HanCharacterCount, string SourceText,
    bool RequiresRetranslation, bool HasUserDraft);

public sealed record EditorStateSnapshot(string Title, SegmentEditorState? SelectedSegment,
    int Total, int Untranslated, int Translating, int AwaitingReview, int Approved, int Errors,
    int NeedsInspection, int InspectionRequired, int RequiresRetranslation, int NormalizedChanges,
    bool CanUndoNormalization, bool CanUndoBatchReplacement, bool CanRestoreStructure,
    QueueStatus QueueStatus)
{
    public int Count(SegmentStatus status) => status switch
    {
        SegmentStatus.Untranslated => Untranslated,
        SegmentStatus.Translating => Translating,
        SegmentStatus.AwaitingReview => AwaitingReview,
        SegmentStatus.Approved => Approved,
        SegmentStatus.Error => Errors,
        SegmentStatus.InspectionRequired => InspectionRequired,
        _ => 0
    };
}
