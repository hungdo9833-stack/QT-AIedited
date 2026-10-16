using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed partial class TranslationService
{
    public SourceEditPreview PreviewSourceEdit(string newSource, SegmenterOptions? segmentation = null)
    {
        lock (_sync)
        {
            EnsureStructureAvailable();
            return SourceEditPlanner.Preview(DocumentJson.Clone(_chapter), newSource, segmentation);
        }
    }

    public async Task ApplySourceEditAsync(SourceEditPreview preview, bool confirmed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool ownsCommit = false;
        try
        {
            ChapterDocument candidate;
            lock (_sync)
            {
                EnsureStructureAvailable();
                candidate = SourceEditPlanner.Apply(_chapter, preview, confirmed, _clock.GetUtcNow());
                _batchCommitInProgress = true;
                ownsCommit = true;
            }
            // Persist the complete candidate through the registered Project store before
            // publishing it to the live editor. A failed/cancelled write leaves live state intact.
            await _store.SaveAsync(candidate, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _chapter = candidate;
                _revision++;
                _savedRevision = _revision;
            }
        }
        finally
        {
            if (ownsCommit) lock (_sync) _batchCommitInProgress = false;
            _saveGate.Release();
        }
    }
}
