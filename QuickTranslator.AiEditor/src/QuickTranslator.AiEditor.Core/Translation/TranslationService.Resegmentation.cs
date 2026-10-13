using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed partial class TranslationService
{
    public ResegmentationPreview PreviewResegmentation(SegmenterOptions options)
    {
        lock (_sync)
        {
            EnsureStructureAvailable();
            return ResegmentationPlanner.Preview(_chapter, options);
        }
    }

    public async Task ApplyResegmentationAsync(ResegmentationPreview preview, bool confirmed,
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
                candidate = ResegmentationPlanner.Apply(_chapter, preview, confirmed, _clock.GetUtcNow());
                _batchCommitInProgress = true;
                ownsCommit = true;
            }
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
