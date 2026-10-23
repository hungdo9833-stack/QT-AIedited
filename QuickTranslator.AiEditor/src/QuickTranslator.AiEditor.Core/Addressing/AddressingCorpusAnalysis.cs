using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.Addressing;

public static partial class AddressingAiSurvey
{
    /// <summary>
    /// Evidence for one survey invocation. Sources, resources and settings stay fixed for its lifetime;
    /// only verified scene attributions change. Never shared between runs or books.
    /// </summary>
    internal sealed class CorpusAnalysis
    {
        private readonly IReadOnlyList<CensusSource> _sources;
        private readonly IReadOnlyCollection<string> _names;
        private readonly AddressingSettings _settings;
        private readonly CancellationToken _ct;
        internal readonly Identities _identities;
        private readonly AddressingDialogueSurvey.NameMatcher _matcher;
        private readonly IReadOnlyList<DialogueLine> _lines;
        private readonly IReadOnlyList<AddressingTask> _scenes;
        private readonly Dictionary<string, IReadOnlyList<NarrativeCharacter>> _narration = new(StringComparer.Ordinal);
        private IdentityCorpusEvidence? _identityEvidence;
        internal IReadOnlyList<DialogueLine>? _projectedLines;
        private IReadOnlyList<AddressingTask>? _tasks;

        // Compare attribution values, not record/list references: checkpoints reload the ledger each request.
        // Last usable turn wins for duplicate quote keys, just as in WithSceneTurns.
        private (int Chapter, int Start, string Hash, string Speaker, string Listener)[]? _turns;

        internal int DialogueExtractions { get; private set; }
        internal int SceneExtractions { get; private set; }
        internal int ProjectionBuilds { get; private set; }
        internal int NarrationExtractions { get; private set; }

        internal CorpusAnalysis(Guid projectId, IReadOnlyList<CensusSource> sources, ProjectResources resources,
            AddressingSettings settings, CancellationToken ct)
            : this(sources, SurveyNames(projectId, sources, resources, 3, settings.FilterJunkCandidates, ct), settings, ct) { }

        internal CorpusAnalysis(IReadOnlyList<CensusSource> sources, (IReadOnlyList<string> Names, IReadOnlyList<string> Titles) names,
            AddressingSettings settings, CancellationToken ct = default)
        {
            settings.Validate(); ct.ThrowIfCancellationRequested();
            (_sources, _names, _settings, _ct) = (sources, names.Names, settings, ct);
            _identities = Identify(sources, _names);
            _matcher = new(_identities.AllNames);
            _lines = AddressingDialogueSurvey.ExtractAll(sources, _matcher, ct);
            DialogueExtractions++;
            // Keep the title-inclusive scene matcher separate: titles affect longest matching and attribution.
            _scenes = PlanScenesCore(sources, _names, settings, ct, names.Titles, _identities);
            if (settings.MaximumScenes > 0) SceneExtractions++;
        }

        internal IReadOnlyList<NarrativeCharacter> Narration(IReadOnlyCollection<string> titlePeople)
        {
            // Adding a title can change overlapping name matches, so analyze the exact name set, once per set.
            string key = string.Join('\0', titlePeople.Order(StringComparer.Ordinal));
            if (!_narration.TryGetValue(key, out var narration))
            {
                narration = NarrativeReferenceSurvey.Analyze(_sources, [.. _identities.AllNames, .. titlePeople], int.MaxValue, _ct);
                _narration.Add(key, narration);
                NarrationExtractions++;
            }
            return narration;
        }

        internal IdentityCorpusEvidence IdentityEvidence() => _identityEvidence ??=
            AnalyzeIdentityEvidence(_sources, _identities, _matcher, Narration([]), _ct);

        internal AddressingSurveyWork Plan(AddressingLedgerState state)
        {
            _ct.ThrowIfCancellationRequested();
            var turns = state.SceneTurns();
            var attributions = turns.Select(t => (t.ChapterNumber, t.StartUtf16, t.QuoteHash, t.Speaker, t.Listener)).ToArray();
            if (_tasks is null || _turns is null || !_turns.SequenceEqual(attributions))
            {
                _projectedLines = WithSceneTurns(_lines, turns);
                _tasks = [.. PlanTasksCore(_sources, _names, _settings, _ct, turns, this),
                    .. PlanIdentitiesCore(_sources, _names, _settings, _ct, turns, this)];
                _turns = attributions;
                ProjectionBuilds++;
            }
            // Pending work must always use the latest ledger, even when the task evidence is unchanged.
            return new(_scenes, _tasks, state, _settings);
        }
    }
}
