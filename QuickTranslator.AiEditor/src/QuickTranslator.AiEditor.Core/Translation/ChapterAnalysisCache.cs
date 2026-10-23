using System.Text.Json;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Translation;

/// <summary>
/// Service-local, bounded LRU of source-only chapter analyses. Callers supply operation snapshots.
/// Exact source and compact character JSON form a collision-free content key, including aliases, genders,
/// pronoun policies and relationship ranges. Chapter number matters to dialogue attribution and rules.
/// </summary>
internal sealed class ChapterAnalysisCache(int capacity = 4)
{
    // Zero capacity provides an uncached baseline for equivalence tests.
    private readonly int _capacity = capacity >= 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _sync = new();
    private readonly LinkedList<Entry> _entries = new();
    private int _analysisCount;

    // Internal diagnostics let tests verify reuse without timing assumptions or a public testing API.
    internal int AnalysisCount { get { lock (_sync) return _analysisCount; } }
    internal int Count { get { lock (_sync) return _entries.Count; } }

    internal sealed record Analysis(IReadOnlyList<NarrativePronounReference> NarrativeReferences,
        DialogueMapBuilder.ChapterAnalysis Dialogue);

    private sealed record Entry(Guid ChapterId, int? ChapterNumber, string Source, string Characters, Analysis Value);

    internal Analysis Get(ChapterDocument chapter)
    {
        // A hit needs only ordinal content comparison and profile serialization, with no name or quote scans.
        string characters = JsonSerializer.Serialize(chapter.CharacterProject);
        lock (_sync)
        {
            for (var node = _entries.First; node is not null; node = node.Next)
            {
                Entry entry = node.Value;
                if (entry.ChapterId != chapter.Id || entry.ChapterNumber != chapter.ChapterNumber ||
                    !string.Equals(entry.Source, chapter.OriginalText, StringComparison.Ordinal) ||
                    !string.Equals(entry.Characters, characters, StringComparison.Ordinal)) continue;
                _entries.Remove(node);
                _entries.AddFirst(node);
                return entry.Value;
            }

            // Detach the retained owners from mutable caller snapshots. The serialized key is also the snapshot
            // used for analysis, so later profile edits cannot change an existing entry or an in-flight operation.
            var snapshot = new ChapterDocument
            {
                Id = chapter.Id, ChapterNumber = chapter.ChapterNumber, OriginalText = chapter.OriginalText,
                CharacterProject = JsonSerializer.Deserialize<CharacterProject>(characters)!
            };
            var analysis = new Analysis(new NarrativePronounTracker().Scan(snapshot.OriginalText, snapshot.CharacterProject),
                DialogueMapBuilder.AnalyzeChapter(snapshot));
            _analysisCount++;
            _entries.AddFirst(new Entry(snapshot.Id, snapshot.ChapterNumber, snapshot.OriginalText, characters, analysis));
            if (_entries.Count > _capacity) _entries.RemoveLast();
            return analysis;
        }
    }
}
