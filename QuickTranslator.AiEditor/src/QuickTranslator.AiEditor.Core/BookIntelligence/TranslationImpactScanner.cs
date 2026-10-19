using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record MemoryScanProgress(int ProcessedChapters, int TotalChapters, int RescannedChapters, int EventCandidates, long Revision);

/// <summary>CPU hints only; does not infer truth, reveal dates, speaker/listener, or event effects.</summary>
public sealed class TranslationImpactScanner
{
    public const string ProcessorVersion = "cpu-impact-1";
    private static Regex Pattern(string text) => new(text, RegexOptions.Compiled | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly (TranslationImpactEventType Type, Regex Trigger)[] Triggers =
    [
        (TranslationImpactEventType.RevealIdentity, Pattern(@"真实身份|真正的身份|原来(?:竟)?是|其实(?:就)?是|本名(?:叫|是|为)")),
        (TranslationImpactEventType.NameOrAliasChange, Pattern(@"改名(?:为|叫)?|更名|化名|以后叫我")),
        (TranslationImpactEventType.TitleGrantedOrRevoked, Pattern(@"封为|册封|被任命为|免去|撤去|剥夺.{0,8}(?:称号|头衔)|不再担任")),
        (TranslationImpactEventType.RelationshipEstablished, Pattern(@"结婚|成婚|订婚|结为|拜.{0,8}为师|认.{0,8}为(?:父|母|子|女)|收.{0,8}为徒")),
        (TranslationImpactEventType.RelationshipEnded, Pattern(@"离婚|断绝.{0,8}关系|解除婚约|逐出师门|恩断义绝")),
        (TranslationImpactEventType.FamilyRoleChanged, Pattern(@"认祖归宗|过继|收养|亲生父亲|亲生母亲|养父|养母")),
        (TranslationImpactEventType.AddressingChanged, Pattern(@"改口(?:叫|称)|以后(?:叫|称)|不再叫|(?:叫|称)我.{0,4}(?:吧|即可)")),
        (TranslationImpactEventType.CharacterStateChanged, Pattern(@"夺舍|灵魂.{0,8}(?:交换|转移)|意识.{0,8}(?:转移|附身)|失忆|恢复记忆"))
    ];

    public async Task<EntityMemoryState> ScanProjectAsync(ProjectSession session, ProjectRepository repository,
        IProgress<MemoryScanProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        session = await EntityMemoryProjectInputs.ReloadSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(session, repository, cancellationToken).ConfigureAwait(false);
        var resources = await new ProjectResourceStore(session).LoadAsync(cancellationToken).ConfigureAwait(false);
        var survey = await new BookIntelligenceStore(session).LoadAsync(cancellationToken).ConfigureAwait(false);
        var audit = await new MissingIdentityReviewStore(session).LoadAsync(cancellationToken).ConfigureAwait(false);
        return await ScanAsync(new(session), sources, resources, survey, audit, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntityMemoryState> ScanAsync(EntityMemoryStore store, IReadOnlyList<CensusSource> sources, ProjectResources resources,
        BookIntelligenceState survey, MissingIdentityReviewState audit, IProgress<MemoryScanProgress>? progress = null,
        CancellationToken cancellationToken = default, int checkpointBatchSize = 16)
    {
        if (checkpointBatchSize is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(checkpointBatchSize));
        var census = CharacterMentionCensus.Scan(resources.ProjectId, survey.Revision, sources, resources, survey,
            cancellationToken, audit.Latest.Select(x => x.ChineseName).ToArray());
        var current = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        // One idempotent import step; no writes to any legacy object or file.
        current = await store.UpdateAsync(current.Revision, s => EntityMemoryProjection.Import(s, resources, survey, census, audit), cancellationToken).ConfigureAwait(false);
        var checkpoints = current.Checkpoints.ToDictionary(x => x.ChapterId);
        var pending = sources.OrderBy(x => x.ChapterNumber).ThenBy(x => x.ChapterId).Where(source =>
            !checkpoints.TryGetValue(source.ChapterId, out var c) || c.SourceSha256 != census.SourceHashes[source.ChapterId] ||
            c.ChapterNumber != source.ChapterNumber || c.ProcessorVersion != ProcessorVersion).ToArray();
        int rescanned = 0, processed = sources.Count - pending.Length;
        foreach (var batch in pending.Chunk(checkpointBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            current = await store.UpdateAsync(current.Revision, s =>
            {
                foreach (var source in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    AppendChapter(s, source, census.SourceHashes[source.ChapterId], survey, cancellationToken);
                    s.Checkpoints.RemoveAll(x => x.ChapterId == source.ChapterId);
                    s.Checkpoints.Add(new(source.ChapterId, source.ChapterNumber, census.SourceHashes[source.ChapterId], ProcessorVersion));
                }
            }, cancellationToken).ConfigureAwait(false);
            rescanned += batch.Length; processed += batch.Length;
            progress?.Report(new(processed, sources.Count, rescanned, current.Events.Count, current.Revision));
        }
        progress?.Report(new(sources.Count, sources.Count, rescanned, current.Events.Count, current.Revision));
        return current;
    }

    internal static void AppendChapter(EntityMemoryState state, CensusSource source, string hash, BookIntelligenceState survey,
        CancellationToken cancellationToken = default)
    {
        var builder = new EntityMemoryBuilder(state);
        // Reuse corpus indexing's bounded 900-unit scale, without touching its checkpoint/evidence.
        for (int start = 0; start < source.OriginalText.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int end = Math.Min(source.OriginalText.Length, start + 900);
            if (end < source.OriginalText.Length && char.IsHighSurrogate(source.OriginalText[end - 1]) && char.IsLowSurrogate(source.OriginalText[end])) end--;
            string window = source.OriginalText.Substring(start, end - start);
            Guid? proof = null;
            foreach (var (type, trigger) in Triggers)
            {
                if (!trigger.IsMatch(window)) continue;
                var corpus = survey.Chapters.FirstOrDefault(x => x.ChapterId == source.ChapterId)?.Evidence.FirstOrDefault(x =>
                    x.IsCurrent && x.SourceSha256 == hash && x.StartUtf16 <= start && (long)x.StartUtf16 + x.LengthUtf16 >= end);
                proof ??= builder.Evidence(source.ChapterId, source.ChapterNumber, hash, start, end - start, corpus?.Id);
                // Lexical co-occurrence is a review hint, not roles or coreference. The reviewer
                // must explicitly refine participants/effects before a CPU event can be confirmed.
                var ids = state.Entities.Where(x => x.Kind != MemoryEntityKind.AddressingCatalogue && window.Contains(x.Label, StringComparison.Ordinal))
                    .Select(x => x.Id).Distinct().Order().ToArray();
                builder.Event(new() { Type = type, EntityIds = ids, EvidenceIds = [proof.Value], Timeline = new(DiscoveredAtChapter: source.ChapterNumber),
                    InitialStatus = MemoryReviewStatus.Proposed, Confidence = 0.2,
                    Provenance = new(MemoryOrigin.CpuTrigger, $"{ProcessorVersion}:{source.ChapterId}:{start}:{type}",
                        "CPU keyword candidate only; one per type/window, not a complete semantic event. Repeated keywords coalesced. No inferred reveal, effect or speaker/listener.") });
            }
            start = end;
        }
    }
}
