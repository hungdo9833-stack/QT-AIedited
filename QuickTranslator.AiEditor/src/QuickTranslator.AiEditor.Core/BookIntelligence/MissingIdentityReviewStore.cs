using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public enum MissingIdentityDecisionKind { NotCharacter, LinkExisting, VerifyNewCharacter }
public enum MissingIdentityLinkKind { Identity, AliasOrTitle }
public sealed record MissingIdentityDecision(Guid Id, string ChineseName, MissingIdentityDecisionKind Kind,
    DateTimeOffset DecidedAt, string Note, IReadOnlyList<CharacterMention> Mentions,
    Guid? TargetCharacterId, MissingIdentityLinkKind? LinkKind, string TargetNameSnapshot,
    string TargetFingerprint, Guid? SupersedesId, string SourceFingerprint = "", int Frequency = 0,
    IReadOnlyDictionary<Guid, string>? SourceScope = null);
public sealed class MissingIdentityReviewState
{
    public int SchemaVersion { get; set; } = 1;
    public Guid ProjectId { get; set; }
    public long Revision { get; set; }
    public List<MissingIdentityDecision> History { get; set; } = [];
    [JsonIgnore] public IEnumerable<MissingIdentityDecision> Latest => History.GroupBy(x => x.ChineseName, StringComparer.Ordinal).Select(x => x.Last());
}

/// <summary>Human decisions only; this sidecar is not entity memory or a translation alias store.</summary>
public sealed class MissingIdentityReviewStore(ProjectSession session)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public string PathName => session.Resolve("Settings/abi3-missing-identity-review.v1.json");

    public Task<MissingIdentityReviewState> LoadAsync(CancellationToken cancellationToken = default) =>
        SimpleFileStorage.ReadOptionalAsync(PathName, Decode, () => new MissingIdentityReviewState
        { ProjectId = session.Manifest.ProjectId }, cancellationToken);

    public async Task<MissingIdentityReviewState> DecideAsync(CharacterCensusReport observed, string chineseName,
        MissingIdentityDecisionKind kind, long expectedRevision, long expectedResourceRevision,
        Guid? targetCharacterId = null, MissingIdentityLinkKind? linkKind = null, string note = "",
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind) || observed.ProjectId != session.Manifest.ProjectId)
            throw new ArgumentException("Invalid Missing Identity decision/project.");
        var gate = Gates.GetOrAdd(PathName, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            // Fail closed if another app instance is deciding on this Project.
            // Keep the empty lease file; deleting it would race another opener.
            using var lease = new FileStream(PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var journal = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (journal.Revision != expectedRevision) throw new InvalidOperationException("Identity review changed; refresh before deciding.");
            var resources = await new ProjectResourceStore(session).LoadAsync(cancellationToken).ConfigureAwait(false);
            if (resources.Revision != expectedResourceRevision) throw new InvalidOperationException("Characters changed; refresh before deciding.");
            var state = await new BookIntelligenceStore(session).LoadAsync(cancellationToken).ConfigureAwait(false);
            var current = await CharacterMentionCensus.ScanAsync(session, new ProjectRepository(), resources, state, cancellationToken,
                journal.Latest.Select(x => x.ChineseName).ToArray()).ConfigureAwait(false);
            var before = observed.Candidates.SingleOrDefault(x => x.ChineseName == chineseName);
            var candidate = current.Candidates.SingleOrDefault(x => x.ChineseName == chineseName);
            if (before is null || candidate is null || CharacterMentionCensus.Fingerprint(before) != CharacterMentionCensus.Fingerprint(candidate))
                throw new InvalidOperationException("Candidate source changed/disappeared; refresh and review again.");
            CharacterProfile? target = null;
            if (kind == MissingIdentityDecisionKind.LinkExisting)
            {
                if (targetCharacterId is null || linkKind is null || !Enum.IsDefined(linkKind.Value))
                    throw new ArgumentException("Choose an existing character and explicit identity/alias link kind.");
                target = resources.Characters.Profiles.SingleOrDefault(x => x.Id == targetCharacterId)
                    ?? throw new InvalidOperationException("Target character no longer exists.");
            }
            else if (targetCharacterId is not null || linkKind is not null)
                throw new ArgumentException("Only a link decision can reference a target character.");
            var previous = journal.Latest.SingleOrDefault(x => x.ChineseName == chineseName);
            journal.History.Add(new(Guid.NewGuid(), chineseName, kind, DateTimeOffset.UtcNow, note.Trim(),
                candidate.Mentions.ToArray(), targetCharacterId, linkKind, target?.VietnameseName ?? "",
                target is null ? "" : Fingerprint(target), previous?.Id, CharacterMentionCensus.Fingerprint(candidate), candidate.Frequency,
                (candidate.MentionedChapterIds ?? candidate.Mentions.Select(x => x.ChapterId).Distinct().ToArray())
                    .ToDictionary(id => id, id => current.SourceHashes[id])));
            journal.Revision++;
            await SimpleFileStorage.WriteAsync(PathName, JsonSerializer.SerializeToUtf8Bytes(journal, DocumentJson.Options),
                Decode, cancellationToken).ConfigureAwait(false);
            return journal;
        }
        finally { gate.Release(); }
    }

    public static bool IsCurrent(MissingIdentityDecision decision, CharacterCensusReport census, ProjectResources resources)
    {
        var candidate = census.Candidates.SingleOrDefault(x => x.ChineseName == decision.ChineseName);
        string fingerprint = string.IsNullOrEmpty(decision.SourceFingerprint)
            ? CharacterMentionCensus.Fingerprint(decision.Mentions) : decision.SourceFingerprint;
        if (candidate is null || fingerprint != CharacterMentionCensus.Fingerprint(candidate)) return false;
        if (decision.Kind != MissingIdentityDecisionKind.LinkExisting) return true;
        var target = resources.Characters.Profiles.SingleOrDefault(x => x.Id == decision.TargetCharacterId);
        return target is not null && Fingerprint(target) == decision.TargetFingerprint;
    }

    private static string Fingerprint(CharacterProfile profile) => CorpusIntegrityAuditor.SourceHash(
        profile.Id.ToString("D") + "|" + profile.ChineseName + "|" + string.Join("|", profile.Aliases.Order(StringComparer.Ordinal)));

    private MissingIdentityReviewState Decode(byte[] bytes)
    {
        var value = JsonSerializer.Deserialize<MissingIdentityReviewState>(bytes, DocumentJson.Options)
            ?? throw new InvalidDataException("Empty identity review sidecar.");
        if (value.SchemaVersion != 1 || value.ProjectId != session.Manifest.ProjectId || value.Revision < 0 || value.History is null ||
            value.History.Select(x => x.Id).Distinct().Count() != value.History.Count || value.History.Any(x =>
                x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.ChineseName) || !Enum.IsDefined(x.Kind) ||
                x.Mentions is null || x.Mentions.Count == 0 || x.Mentions.Any(m => m.ChapterId == Guid.Empty ||
                    m.StartUtf16 < 0 || m.LengthUtf16 <= 0 || string.IsNullOrWhiteSpace(m.SourceSha256)) ||
                (!string.IsNullOrEmpty(x.SourceFingerprint) && (x.Frequency < x.Mentions.Count || x.SourceScope is null ||
                    x.Mentions.Any(m => !x.SourceScope.TryGetValue(m.ChapterId, out string? hash) || hash != m.SourceSha256))) ||
                (x.Kind == MissingIdentityDecisionKind.LinkExisting &&
                    (x.TargetCharacterId is null || x.TargetCharacterId == Guid.Empty || x.LinkKind is null ||
                     !Enum.IsDefined(x.LinkKind.Value) || string.IsNullOrWhiteSpace(x.TargetFingerprint)))))
            throw new InvalidDataException("Invalid identity review sidecar; no decisions were applied.");
        return value;
    }
}
