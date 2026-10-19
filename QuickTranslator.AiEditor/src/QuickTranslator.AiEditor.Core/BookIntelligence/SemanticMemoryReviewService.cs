using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record SemanticPromotion(MemoryTimeline Timeline, string Note, IReadOnlyDictionary<string, Guid> ExplicitBindings);

public sealed class SemanticMemoryReviewService(ProjectSession session, ProjectRepository repository)
{
    public async Task<SemanticSurveyState> ReviewAsync(Guid proposalId, MemoryReviewStatus status, SemanticPromotion edit, long expectedRevision, CancellationToken ct = default)
    {
        if (status is not (MemoryReviewStatus.Accepted or MemoryReviewStatus.Rejected or MemoryReviewStatus.Confirmed) || string.IsNullOrWhiteSpace(edit.Note) || edit.Note.Length > 2000)
            throw new ArgumentException("Choose an explicit action with an audit note.");
        edit.Timeline.Validate(); var store = new SemanticSurveyStore(session);
        // Hold the semantic journal lease across memory CAS; a failed second-file save is
        // recoverable by deterministic IDs. Never roll memory back by overwriting history.
        return await store.UpdateAsync(expectedRevision, async state =>
        {
            var proposal = state.Proposals.Single(x => x.Id == proposalId); var batch = state.Batches.Single(x => x.Id == proposal.BatchId);
            var last = state.Reviews.LastOrDefault(x => x.ProposalId == proposalId);
            if (last?.Status == status && last.Timeline == edit.Timeline && last.Note == edit.Note) return;
            if (last?.Status == MemoryReviewStatus.Confirmed) throw new InvalidOperationException("Undo the confirmed memory decision explicitly before changing this review.");
            Guid? itemId = null, decisionId = null;
            if (status == MemoryReviewStatus.Confirmed)
            {
                if (!proposal.Audit.EvidenceValid || !proposal.Audit.CanPromote) throw new InvalidOperationException("Invalid/unresolved/nonactual evidence cannot become Confirmed. Keep it in review or reject it.");
                var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(session, repository, ct).ConfigureAwait(false);
                var memoryStore = new EntityMemoryStore(session); var memory = await memoryStore.LoadAsync(ct).ConfigureAwait(false);
                var proof = state.Evidence.Single(x => x.Id == batch.EvidenceId);
                var proofs = batch.Keys.Select(k => state.Evidence.Single(e => e.Id == k.EvidenceId)).ToArray();
                if (proofs.Any(e => !SemanticDependencies.Current(batch, e, memory, sources))) throw new InvalidOperationException("Semantic source/context is stale; re-survey the affected evidence.");
                if (edit.Timeline.EffectiveFromChapter is null || edit.Timeline.RevealedAtChapter is null || edit.Timeline.RevealedAtChapter > proof.ChapterNumber || edit.Timeline.EffectiveFromChapter > proof.ChapterNumber)
                    throw new InvalidOperationException("Confirm source-backed effective/reveal chapters, not guessed future dates.");
                var original = proposal.Audit.Finding; var text = sources.Single(x => x.ChapterId == proof.ChapterId).OriginalText;
                var hydrated = original with { SupportSpans = original.SupportSpans.Select(x =>
                {
                    if (x.StartUtf16 < 0 || x.LengthUtf16 is < 1 or > 240 || (long)x.StartUtf16 + x.LengthUtf16 > text.Length) throw new InvalidDataException("Invalid stored support range.");
                    return x with { Text = text.Substring(x.StartUtf16, x.LengthUtf16) };
                }).ToArray() };
                int wireVersion = batch.SchemaVersion == SemanticSurveyContract.MultiEvidenceVersion ? 2 : 1;
                var response = SemanticSurveyContract.Parse(System.Text.Json.JsonSerializer.Serialize(new SemanticResponse(wireVersion, state.ProjectId, proof.ChapterId, proofs.Select(x => x.Id).ToArray(), [hydrated]), SemanticSurveyContract.JsonOptions));
                var request = new SemanticSurveyRequest(batch.Id, state.ProjectId, proof.ChapterNumber, batch.BackendSignature, proof, batch.ContextFingerprint, batch.DependencyFingerprint,
                    "", 0, 0, batch.AllowedIdentities, batch.ContextFactIds, batch.CpuCandidates,
                    new EntityMemoryQuery(memory, sources).AtChapter(proof.ChapterNumber).Entities.SelectMany(x => x.Facts).Where(x => batch.ContextFactIds.Contains(x.Id)).ToArray());
                if (wireVersion == 2) request = request with { Members = proofs.Select(e => request with { Evidence = e }).ToArray() };
                var checkedFinding = SemanticQualityAuditor.Audit(response, request, sources).Findings.Single();
                if (!checkedFinding.EvidenceValid || !checkedFinding.CanPromote) throw new InvalidOperationException("Stored finding failed independent revalidation.");
                var f = checkedFinding.Finding; var identities = SemanticQualityAuditor.Identities(f).ToArray();
                if (edit.ExplicitBindings.Keys.Any(key => !identities.Any(i => i.EntityId is null && i.LocalId == key))) throw new InvalidOperationException("Unknown local binding.");
                memory = await memoryStore.UpdateAsync(memory.Revision, m =>
                {
                    if (!SemanticDependencies.Current(batch, proof, m, sources)) throw new InvalidOperationException("Semantic context changed during review.");
                    var builder = new EntityMemoryBuilder(m);
                    Guid Resolve(SemanticIdentity? identity)
                    {
                        if (identity is null || identity.Status is SemanticResolution.Ambiguous or SemanticResolution.Unresolved) throw new InvalidOperationException("Identity is unresolved.");
                        if (identity.EntityId is Guid existing) { if (!m.Entities.Any(x => x.Id == existing)) throw new InvalidOperationException("Unknown identity ID."); return existing; }
                        if (edit.ExplicitBindings.TryGetValue(identity.LocalId, out Guid selected))
                        {
                            var target = m.Entities.Single(x => x.Id == selected);
                            if (target.Kind != identity.Kind) throw new InvalidOperationException("Body/mind/candidate facets cannot be silently merged by a binding.");
                            return selected;
                        }
                        return builder.Entity($"semantic-local:{batch.Id}:{identity.LocalId}", identity.Kind, identity.Name);
                    }
                    Guid[] evidence = f.SupportSpans.Select(span =>
                    {
                        var origin = proofs.Single(x => x.Id == span.EvidenceId);
                        return builder.Evidence(origin.ChapterId, origin.ChapterNumber, origin.SourceSha256, span.StartUtf16, span.LengthUtf16);
                    }).Distinct().ToArray();
                    var timeline = edit.Timeline with { DiscoveredAtChapter = proof.ChapterNumber };
                    var provenance = new MemoryProvenance(MemoryOrigin.User, "semantic-confirmation:" + proposal.Id,
                        "Explicit human confirmation of a mechanically audited semantic hypothesis. " + edit.Note, proposal.Id,
                        RuleId: batch.DependencyFingerprint);
                    if (f.FindingType == SemanticFindingType.EventVerification)
                    {
                        provenance = provenance with { Key = provenance.Key + ":deps=" + string.Join(',', batch.ContextFactIds.Select(x => x.ToString("N"))) };
                        Guid[] participants = f.VerifiedParticipants.Select(Resolve).Distinct().Order().ToArray();
                        var item = builder.Event(new() { Type = f.EventType!.Value, EntityIds = participants, Timeline = timeline,
                            Effect = f.EventEffect, AffectedFactIds = f.AffectedFactIds, EvidenceIds = evidence, Confidence = f.Confidence, Provenance = provenance });
                        itemId = item.Id;
                    }
                    else
                    {
                        Guid subject = Resolve(f.FindingType == SemanticFindingType.DirectionalAddressing ? f.SpeakerIdentity : f.SubjectIdentity);
                        Guid? target = f.FindingType == SemanticFindingType.DirectionalAddressing ? Resolve(f.ListenerIdentity) : f.TargetIdentity is null ? null : Resolve(f.TargetIdentity);
                        MemoryFactKind kind = f.FindingType switch { SemanticFindingType.CharacterIdentification => MemoryFactKind.PrimaryName,
                            SemanticFindingType.RelationshipExtraction => MemoryFactKind.Relationship, SemanticFindingType.DirectionalAddressing => MemoryFactKind.Addressing,
                            _ when f.FactKind is MemoryFactKind.Alias or MemoryFactKind.Nickname or MemoryFactKind.Title or MemoryFactKind.FamilyAddressing or MemoryFactKind.IdentityAssociation => f.FactKind,
                            _ => throw new InvalidOperationException("Unsupported alias/identity assertion; not an automatic identity merge.") };
                        var fact = builder.Fact(new() { SubjectId = subject, TargetId = target, Kind = kind, Value = f.Value.Length > 0 ? f.Value : kind.ToString(),
                            RelationType = f.RelationshipType, AssociationKind = kind == MemoryFactKind.IdentityAssociation ? MemoryAssociationKind.PossibleIdentity : MemoryAssociationKind.Unspecified,
                            SelfReference = f.SelfReference, AddresseeReference = f.AddresseeReference, Timeline = timeline, EvidenceIds = evidence,
                            InputFactIds = batch.ContextFactIds, Confidence = f.Confidence, Provenance = provenance }); itemId = fact.Id;
                    }
                    EntityMemoryReviewService.AppendDecision(m, sources, itemId.Value, MemoryReviewStatus.Confirmed, timeline, edit.Note);
                    decisionId = m.Reviews.Last(x => x.TargetId == itemId).Id;
                }, ct).ConfigureAwait(false);
            }
            state.Reviews.Add(new(Guid.NewGuid(), proposalId, status, DateTimeOffset.UtcNow, edit.Note, edit.Timeline, itemId, decisionId));
        }, ct).ConfigureAwait(false);
    }
    public async Task<SemanticSurveyState> UndoAsync(Guid proposalId, long expectedRevision, string note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note) || note.Length > 2000) throw new ArgumentException("Audit note is required.");
        return await new SemanticSurveyStore(session).UpdateAsync(expectedRevision, async state =>
        {
            var last = state.Reviews.Last(x => x.ProposalId == proposalId);
            var memory = await new EntityMemoryStore(session).LoadAsync(ct).ConfigureAwait(false);
            var owned = memory.Facts.Where(x => x.Provenance.LegacyId == proposalId && x.Provenance.Key.StartsWith("fact:semantic-confirmation:", StringComparison.Ordinal)).Select(x => x.Id)
                .Concat(memory.Events.Where(x => x.Provenance.LegacyId == proposalId && x.Provenance.Key.StartsWith("event:semantic-confirmation:", StringComparison.Ordinal)).Select(x => x.Id)).ToHashSet();
            if (last.MemoryDecisionId is Guid decision && last.MemoryItemId is Guid linked) owned.Add(linked);
            // A process can stop between the two atomic files. Undo must find human-owned
            // memory by proposal provenance even when the journal never recorded the link.
            foreach (Guid item in owned)
            {
                var latest = memory.Reviews.LastOrDefault(x => x.TargetId == item);
                if (latest is not null && latest.Status != MemoryReviewStatus.Proposed)
                    memory = await new EntityMemoryReviewService(session, repository).RollbackAsync(latest.Id, memory.Revision, note, ct).ConfigureAwait(false);
            }
            state.Reviews.Add(new(Guid.NewGuid(), proposalId, MemoryReviewStatus.Proposed, DateTimeOffset.UtcNow, note, last.Timeline, RevertsId: last.Id));
        }, ct).ConfigureAwait(false);
    }
}
