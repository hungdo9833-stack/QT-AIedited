using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

// Preserve legacy enum values for existing audit readers; Category is authoritative for routing.
public enum MentionHypothesisKind { KnownName, ContextualName, AliasHypothesis, TitleHypothesis }
public sealed record CensusSource(Guid ChapterId, int ChapterNumber, string OriginalText);
public sealed record CharacterMention(Guid ChapterId, int ChapterNumber, int StartUtf16, int LengthUtf16, string SourceSha256)
{
    public CorpusEvidence NavigationEvidence(string name) => new()
    {
        ChapterId = ChapterId, StartUtf16 = StartUtf16, LengthUtf16 = LengthUtf16, SourceSha256 = SourceSha256,
        Excerpt = name, IsCurrent = true, PriorityReason = "CPU mention hypothesis, not an AI finding or approved identity"
    };
}
public sealed record CharacterMentionCandidate(string ChineseName, MentionHypothesisKind Kind,
    IReadOnlyList<CharacterMention> Mentions, bool HasProfile, bool HasReview,
    MentionCandidateCategory Category = MentionCandidateCategory.PersonCandidate, int TotalMentions = 0,
    int? FirstObservedChapter = null, int? LastObservedChapter = null, string OccurrenceFingerprint = "",
    IReadOnlyList<Guid>? MentionedChapterIds = null, string Signals = "")
{
    public int Frequency => TotalMentions > 0 ? TotalMentions : Mentions.Count;
    public int FirstChapter => FirstObservedChapter ?? Mentions.Min(x => x.ChapterNumber);
    public int LastChapter => LastObservedChapter ?? Mentions.Max(x => x.ChapterNumber);
    public bool MentionsSampled => Frequency > Mentions.Count;
}
public sealed record MissingIdentityAuditItem(string ChineseName, MentionHypothesisKind Kind, int Frequency,
    int FirstChapter, int LastChapter, IReadOnlyList<CharacterMention> Mentions);
public sealed record CharacterCensusReport(int SchemaVersion, Guid ProjectId, long SurveyRevision,
    int ActiveChapters, long ScannedUtf16, IReadOnlyDictionary<Guid, string> SourceHashes,
    IReadOnlyList<CharacterMentionCandidate> Candidates, IReadOnlyList<MissingIdentityAuditItem> MissingIdentities);

/// <summary>Full-source CPU hypotheses. Counts/fingerprints are exact; navigation samples are bounded.
/// Does not merge identities, approve resources or contribute to translation prompts.</summary>
public static class CharacterMentionCensus
{
    public const int MaximumStoredMentions = 128;
    public const int MaximumStoredUncertainMentions = 8;
    public static CharacterCensusReport Scan(Guid projectId, long surveyRevision, IReadOnlyList<CensusSource> sources,
        ProjectResources resources, BookIntelligenceState state, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? reviewNames = null)
    {
        if (projectId != resources.ProjectId || projectId != state.ProjectId) throw new InvalidOperationException("Census belongs to another project.");
        if (sources.Select(x => x.ChapterId).Distinct().Count() != sources.Count) throw new InvalidDataException("Duplicate census chapter identity.");
        var profiles = resources.Characters.Profiles.SelectMany(x => new[] { x.ChineseName }.Concat(x.Aliases)).Where(Useful).ToHashSet(StringComparer.Ordinal);
        var primary = resources.Characters.Profiles.Select(x => x.ChineseName).Where(Useful).ToHashSet(StringComparer.Ordinal);
        var profileAliases = resources.Characters.Profiles.SelectMany(x => x.Aliases).Where(Useful).ToHashSet(StringComparer.Ordinal);
        var reviewed = state.Characters.SelectMany(x => new[] { x.ChineseName }.Concat(x.Aliases))
            .Concat(state.CandidateIdentities.Where(x => x.Status == CandidateIdentityStatus.Rejected).SelectMany(x => new[] { x.ChineseName }.Concat(x.Aliases)))
            .Where(Useful).ToHashSet(StringComparer.Ordinal);
        var names = new Dictionary<string, MentionSignal>(StringComparer.Ordinal);
        foreach (string name in profiles.Concat(reviewed)) names.TryAdd(name, MentionSignal.Known);
        foreach (string name in (reviewNames ?? []).Where(Useful)) names.TryAdd(name, MentionSignal.ReviewSeed);
        foreach (string name in state.CandidateIdentities.SelectMany(x => new[] { x.ChineseName }.Concat(x.Aliases)).Where(Useful)) names.TryAdd(name, MentionSignal.Known);
        foreach (string term in ChineseMentionDiscovery.Addressing) names.TryAdd(term, MentionSignal.Addressing);
        var hashes = new Dictionary<Guid, string>();
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hashes.Add(source.ChapterId, CorpusIntegrityAuditor.SourceHash(source.OriginalText));
            ChineseMentionDiscovery.Discover(source.OriginalText, names, cancellationToken);
        }
        foreach (var source in sources) ChineseMentionDiscovery.MarkClauseStarts(source.OriginalText, names, cancellationToken);
        var trie = new Node();
        foreach (string name in names.Keys)
        {
            Node node = trie;
            foreach (char c in name)
            {
                if (!node.Children.TryGetValue(c, out Node? next)) node.Children.Add(c, next = new());
                node = next;
            }
            node.Name = name;
        }
        var occurrences = names.ToDictionary(x => x.Key, x => new MentionOccurrenceAccumulator(
            ChineseMentionDiscovery.Classify(x.Key, x.Value, 3, primary.Contains(x.Key), profileAliases.Contains(x.Key)) == MentionCandidateCategory.NoiseOrUncertain
                ? MaximumStoredUncertainMentions : MaximumStoredMentions), StringComparer.Ordinal);
        try
        {
            foreach (var source in sources.OrderBy(x => x.ChapterNumber).ThenBy(x => x.ChapterId))
                for (int start = 0; start < source.OriginalText.Length; start++)
                {
                    if ((start & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    Node node = trie;
                    for (int i = start; i < source.OriginalText.Length && node.Children.TryGetValue(source.OriginalText[i], out Node? next); i++)
                    {
                        node = next;
                        if (node.Name is string name) occurrences[name].Add(source.ChapterId, source.ChapterNumber, start, name.Length, hashes[source.ChapterId]);
                    }
                }
            var candidates = names.Where(x => occurrences[x.Key].Count > 0).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x =>
            {
                var hits = occurrences[x.Key];
                var category = ChineseMentionDiscovery.Classify(x.Key, x.Value, hits.Count, primary.Contains(x.Key), profileAliases.Contains(x.Key));
                var kind = category == MentionCandidateCategory.TitleCandidate ? MentionHypothesisKind.TitleHypothesis :
                    category == MentionCandidateCategory.AliasCandidate ? MentionHypothesisKind.AliasHypothesis :
                    x.Value.HasFlag(MentionSignal.Known) || x.Value.HasFlag(MentionSignal.ReviewSeed) ? MentionHypothesisKind.KnownName : MentionHypothesisKind.ContextualName;
                return new CharacterMentionCandidate(x.Key, kind, hits.Samples, profiles.Contains(x.Key), reviewed.Contains(x.Key),
                    category, hits.Count, hits.FirstChapter, hits.LastChapter, hits.Finish(), hits.Chapters, x.Value.ToString());
            }).ToArray();
            // Reject grammatical expansions of a separately evidenced spelling, not identities.
            // Keep the uncertain row/provenance; never merge it with the shorter candidate.
            var anchored = candidates.Where(x => x.Category is MentionCandidateCategory.PersonCandidate or MentionCandidateCategory.AliasCandidate or MentionCandidateCategory.TitleCandidate)
                .Select(x => x.ChineseName).ToHashSet(StringComparer.Ordinal);
            candidates = candidates.Select(x => !primary.Contains(x.ChineseName) && !profileAliases.Contains(x.ChineseName) &&
                ChineseMentionDiscovery.IsGrammaticalExtension(x.ChineseName, anchored)
                    ? x with { Category = MentionCandidateCategory.NoiseOrUncertain, Signals = x.Signals + ", GrammaticalExtension" } : x).ToArray();
            candidates = candidates.Select(x => x.Category == MentionCandidateCategory.NoiseOrUncertain && x.Mentions.Count > MaximumStoredUncertainMentions
                ? x with { Mentions = x.Mentions.Take(MaximumStoredUncertainMentions).ToArray() } : x).ToArray();
            var missing = candidates.Where(x => x.Category == MentionCandidateCategory.PersonCandidate && !x.HasProfile && !x.HasReview)
                .Select(x => new MissingIdentityAuditItem(x.ChineseName, x.Kind, x.Frequency, x.FirstChapter, x.LastChapter, x.Mentions))
                .OrderByDescending(x => x.Frequency).ThenBy(x => x.ChineseName, StringComparer.Ordinal).ToArray();
            return new(2, projectId, surveyRevision, sources.Count, sources.Sum(x => (long)x.OriginalText.Length), hashes, candidates, missing);
        }
        finally { foreach (var accumulator in occurrences.Values) accumulator.Dispose(); }
    }

    public static async Task<CharacterCensusReport> ScanAsync(ProjectSession session, ProjectRepository repository,
        ProjectResources resources, BookIntelligenceState state, CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? reviewNames = null)
    {
        var sources = new List<CensusSource>();
        foreach (var entry in session.Manifest.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number).ThenBy(x => x.ChapterId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapter = (await repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken).ConfigureAwait(false)).Chapter;
            if (chapter.Id != entry.ChapterId || chapter.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Census chapter identity mismatch.");
            sources.Add(new(entry.ChapterId, entry.Number, chapter.OriginalText ?? ""));
        }
        return Scan(session.Manifest.ProjectId, state.Revision, sources, resources, state, cancellationToken, reviewNames);
    }

    // v2 is derived statistics, not a migration. Keep old v1 files and the P0.1 decision journal.
    // Missing entries reference candidate names rather than serializing mention arrays twice.
    public static Task SaveReportAsync(ProjectSession session, CharacterCensusReport report, CancellationToken cancellationToken = default)
    {
        if (report.SchemaVersion != 2 || report.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Invalid census report.");
        return SimpleFileStorage.WriteAsync(session.Resolve("Settings/abi3-character-census.v2.json"), SerializeReport(report),
            bytes => JsonSerializer.Deserialize<CensusSnapshot>(bytes, DocumentJson.Options) ?? throw new InvalidDataException("Empty census report."), cancellationToken);
    }
    public static byte[] SerializeReport(CharacterCensusReport report) => JsonSerializer.SerializeToUtf8Bytes(new CensusSnapshot(
        report.SchemaVersion, report.ProjectId, report.SurveyRevision, report.ActiveChapters, report.ScannedUtf16, report.SourceHashes,
        report.Candidates, report.MissingIdentities.Select(x => x.ChineseName).ToArray()), DocumentJson.Options);
    private sealed record CensusSnapshot(int SchemaVersion, Guid ProjectId, long SurveyRevision, int ActiveChapters, long ScannedUtf16,
        IReadOnlyDictionary<Guid, string> SourceHashes, IReadOnlyList<CharacterMentionCandidate> Candidates, IReadOnlyList<string> MissingIdentityNames);
    internal static string Fingerprint(IEnumerable<CharacterMention> mentions)
    {
        using var accumulator = new MentionOccurrenceAccumulator();
        foreach (var item in mentions.OrderBy(x => x.ChapterNumber).ThenBy(x => x.ChapterId).ThenBy(x => x.StartUtf16))
            accumulator.Add(item.ChapterId, item.ChapterNumber, item.StartUtf16, item.LengthUtf16, item.SourceSha256);
        return accumulator.Finish();
    }
    internal static string Fingerprint(CharacterMentionCandidate candidate) => string.IsNullOrEmpty(candidate.OccurrenceFingerprint) ? Fingerprint(candidate.Mentions) : candidate.OccurrenceFingerprint;
    private static bool Useful(string name) => !string.IsNullOrWhiteSpace(name) && name.Length >= 2;
    private sealed class Node { public Dictionary<char, Node> Children { get; } = []; public string? Name { get; set; } }
}
