using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed class EntityMemoryReviewService(ProjectSession session, ProjectRepository repository)
{
    public async Task<EntityMemoryState> ReviewAsync(Guid targetId, MemoryReviewStatus status, MemoryTimeline timeline,
        long expectedRevision, string note, CancellationToken cancellationToken = default)
    {
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(session, repository, cancellationToken).ConfigureAwait(false);
        return await new EntityMemoryStore(session).UpdateAsync(expectedRevision,
            state => AppendDecision(state, sources, targetId, status, timeline, note), cancellationToken).ConfigureAwait(false);
    }

    internal static void AppendDecision(EntityMemoryState state, IReadOnlyList<CensusSource> sources, Guid id,
        MemoryReviewStatus status, MemoryTimeline timeline, string note, Guid? reverts = null)
    {
        if (status is not (MemoryReviewStatus.Accepted or MemoryReviewStatus.Confirmed or MemoryReviewStatus.Rejected or MemoryReviewStatus.Ambiguous or MemoryReviewStatus.Proposed) ||
            string.IsNullOrWhiteSpace(note) || note.Length > 2000) throw new ArgumentException("Choose an explicit review action and a nonempty audit note.");
        var query = new EntityMemoryQuery(state, sources);
        timeline.Validate();
        if (status == MemoryReviewStatus.Confirmed)
        {
            if (!query.IsFresh(id) || timeline.EffectiveFromChapter is null || timeline.RevealedAtChapter is null)
                throw new InvalidOperationException("Confirmation needs current source, effective start and an explicit reveal chapter.");
            int firstProof = query.EvidenceIds(id).Select(e => state.Evidence.Single(x => x.Id == e).ChapterNumber).Min();
            if (timeline.RevealedAtChapter < firstProof) throw new InvalidOperationException("Reveal cannot precede all cited source evidence. Attach earlier proof rather than guessing.");
            timeline = timeline with { DiscoveredAtChapter = timeline.DiscoveredAtChapter ?? query.Timeline(id).DiscoveredAtChapter ?? firstProof };
            if (state.Events.SingleOrDefault(x => x.Id == id) is { } item) ValidateEvent(state, item);
        }
        var last = state.Reviews.LastOrDefault(x => x.TargetId == id);
        if (last is not null && last.Status == status && last.Timeline == timeline && last.Note == note.Trim() && reverts is null) return;
        var decision = new MemoryReviewDecision(Guid.NewGuid(), id, status, DateTimeOffset.UtcNow, note.Trim(), timeline,
            EntityMemoryIdentity.DefinitionFingerprint(state, id), reverts);
        var proposed = EntityMemoryStore.Clone(state); proposed.Reviews.Add(decision); proposed.Validate();
        if (status == MemoryReviewStatus.Confirmed && !new EntityMemoryQuery(proposed, sources).Confirmed(id))
            throw new InvalidOperationException("Conflicting/stale dependency; resolve the conflict before confirming. Nothing was saved.");
        state.Reviews.Add(decision);
    }

    private static void ValidateEvent(EntityMemoryState state, TranslationImpactEvent item)
    {
        if (item.Provenance.Origin == MemoryOrigin.CpuTrigger) throw new InvalidOperationException("Refine the CPU event's participants and semantics explicitly before confirming it.");
        if (item.EntityIds.Count == 0 || item.EvidenceIds.Count == 0 || item.Effect != MemoryEventEffect.None && item.AffectedFactIds.Count == 0)
            throw new InvalidOperationException("Select the involved identities and affected facts explicitly; CPU keywords cannot supply them.");
        if (item.Effect == MemoryEventEffect.End && item.Type is TranslationImpactEventType.RevealIdentity or TranslationImpactEventType.RelationshipEstablished ||
            item.Effect == MemoryEventEffect.Activate && item.Type == TranslationImpactEventType.RelationshipEnded)
            throw new InvalidOperationException("Event effect contradicts its type.");
        foreach (Guid id in item.AffectedFactIds)
        {
            var fact = state.Facts.Single(x => x.Id == id);
            if (!item.EntityIds.Contains(fact.SubjectId) || fact.TargetId is Guid target && !item.EntityIds.Contains(target))
                throw new InvalidOperationException("Affected fact participants do not match event identities.");
            bool compatible = item.Type switch
            {
                TranslationImpactEventType.RevealIdentity => fact.Kind is MemoryFactKind.PrimaryName or MemoryFactKind.Alias or MemoryFactKind.IdentityAssociation,
                TranslationImpactEventType.NameOrAliasChange => fact.Kind is MemoryFactKind.PrimaryName or MemoryFactKind.Alias or MemoryFactKind.Nickname,
                TranslationImpactEventType.TitleGrantedOrRevoked => fact.Kind == MemoryFactKind.Title,
                TranslationImpactEventType.RelationshipEstablished or TranslationImpactEventType.RelationshipEnded => fact.Kind == MemoryFactKind.Relationship,
                TranslationImpactEventType.FamilyRoleChanged => fact.Kind is MemoryFactKind.FamilyAddressing or MemoryFactKind.Relationship,
                TranslationImpactEventType.AddressingChanged => fact.Kind == MemoryFactKind.Addressing,
                _ => fact.Kind is MemoryFactKind.CharacterState or MemoryFactKind.IdentityAssociation
            };
            if (!compatible) throw new InvalidOperationException("Event type cannot alter this kind of fact.");
        }
    }

    public async Task<EntityMemoryState> RollbackAsync(Guid decisionId, long expectedRevision, string note, CancellationToken cancellationToken = default)
    {
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(session, repository, cancellationToken).ConfigureAwait(false);
        return await new EntityMemoryStore(session).UpdateAsync(expectedRevision, state =>
        {
            var decision = state.Reviews.Single(x => x.Id == decisionId);
            if (state.Reviews.Last(x => x.TargetId == decision.TargetId).Id != decisionId) throw new InvalidOperationException("Only the latest decision can be rolled back safely.");
            // Undo is a demotion, never an implicit re-confirmation of older facts.
            AppendDecision(state, sources, decision.TargetId, MemoryReviewStatus.Proposed, decision.Timeline, note, decisionId);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntityMemoryState> RefineEventAsync(Guid eventId, TranslationImpactEventType type, MemoryEventEffect effect,
        IReadOnlyList<Guid> entities, IReadOnlyList<Guid> affectedFacts, MemoryTimeline timeline, MemoryReviewStatus status,
        long expectedRevision, string note, CancellationToken cancellationToken = default)
    {
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(session, repository, cancellationToken).ConfigureAwait(false);
        return await new EntityMemoryStore(session).UpdateAsync(expectedRevision, state =>
        {
            var old = state.Events.Single(x => x.Id == eventId);
            var updated = new EntityMemoryBuilder(state).Event(old with { Type = type, Effect = effect, EntityIds = entities.Distinct().Order().ToArray(),
                AffectedFactIds = affectedFacts.Distinct().Order().ToArray(), Timeline = timeline, InitialStatus = MemoryReviewStatus.Proposed,
                Provenance = new(MemoryOrigin.User, "event-refinement:" + eventId, "Explicit human refinement, not keyword confirmation.", eventId) });
            AppendDecision(state, sources, updated.Id, status, timeline, note);
            AppendDecision(state, sources, old.Id, MemoryReviewStatus.Rejected, old.Timeline, "Superseded by explicit event review " + updated.Id);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EntityMemoryState> PromoteAuditAliasAsync(Guid factId, MemoryFactKind kind, MemoryTimeline timeline,
        long expectedRevision, string note, CancellationToken cancellationToken = default)
    {
        if (kind is not (MemoryFactKind.Alias or MemoryFactKind.Title)) throw new ArgumentException("Choose alias or title explicitly.");
        var currentSession = await EntityMemoryProjectInputs.ReloadSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(currentSession, repository, cancellationToken).ConfigureAwait(false);
        var journal = await new MissingIdentityReviewStore(currentSession).LoadAsync(cancellationToken).ConfigureAwait(false);
        var resources = await new ProjectResourceStore(currentSession).LoadAsync(cancellationToken).ConfigureAwait(false);
        var survey = await new BookIntelligenceStore(currentSession).LoadAsync(cancellationToken).ConfigureAwait(false);
        var census = CharacterMentionCensus.Scan(session.Manifest.ProjectId, survey.Revision, sources, resources, survey,
            cancellationToken, journal.Latest.Select(x => x.ChineseName).ToArray());
        return await new EntityMemoryStore(session).UpdateAsync(expectedRevision, state =>
        {
            var audit = state.Facts.Single(x => x.Id == factId);
            var decision = journal.Latest.SingleOrDefault(x => x.Id == audit.Provenance.LegacyId);
            if (audit.Provenance.Origin != MemoryOrigin.AuditOnly || decision is null ||
                !MissingIdentityReviewStore.IsCurrent(decision, census, resources)) throw new InvalidOperationException("Audit link changed or is stale; re-review it first.");
            var target = state.Entities.Single(x => x.Id == audit.TargetId);
            if (target.Kind != MemoryEntityKind.Character) throw new InvalidOperationException("Alias promotion needs a separately existing character, not a body/consciousness/candidate merge.");
            var alias = new EntityMemoryBuilder(state).Fact(new() { SubjectId = target.Id, Kind = kind, Value = decision.ChineseName,
                EvidenceIds = audit.EvidenceIds, InputFactIds = [audit.Id], Timeline = timeline, Confidence = 1,
                Provenance = new(MemoryOrigin.User, $"audit-promotion:{audit.Id}:{kind}", "Separate human confirmation of a temporal alias/title; no Project Resources or prompt write.", decision.Id) });
            AppendDecision(state, sources, alias.Id, MemoryReviewStatus.Confirmed, timeline, note);
        }, cancellationToken).ConfigureAwait(false);
    }
}

public static class EntityMemoryProjectInputs
{
    public static async Task<ProjectSession> ReloadSessionAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        var manifest = await ProjectRepository.ReadManifestAsync(session.ManifestPath, cancellationToken).ConfigureAwait(false);
        if (manifest.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Manifest now belongs to another Project.");
        return new ProjectSession(session.ManifestPath, manifest); // read-only; no OpenAsync/synchronization writes
    }
    public static async Task<IReadOnlyList<CensusSource>> LoadSourcesAsync(ProjectSession session, ProjectRepository repository, CancellationToken cancellationToken = default)
    {
        session = await ReloadSessionAsync(session, cancellationToken).ConfigureAwait(false);
        var result = new List<CensusSource>();
        foreach (var entry in session.Manifest.Chapters.Where(x => x.DeletedAt is null).OrderBy(x => x.Number).ThenBy(x => x.ChapterId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var chapter = (await repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken).ConfigureAwait(false)).Chapter;
            if (chapter.Id != entry.ChapterId || chapter.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Memory source chapter belongs to another Project.");
            result.Add(new(entry.ChapterId, entry.Number, chapter.OriginalText ?? ""));
        }
        if (result.Select(x => x.ChapterId).Distinct().Count() != result.Count || result.Select(x => x.ChapterNumber).Distinct().Count() != result.Count)
            throw new InvalidDataException("Duplicate active chapter identity/number.");
        return result;
    }
}
