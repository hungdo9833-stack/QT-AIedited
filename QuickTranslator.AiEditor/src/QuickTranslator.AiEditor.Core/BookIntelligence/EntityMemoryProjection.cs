using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

/// <summary>Append-only, idempotent construction. Version keys include the exact assertion/proof.</summary>
internal sealed class EntityMemoryBuilder(EntityMemoryState state)
{
    private readonly Dictionary<Guid, MemoryEntity> _entities = state.Entities.ToDictionary(x => x.Id);
    private readonly Dictionary<Guid, MemoryEvidence> _evidence = state.Evidence.ToDictionary(x => x.Id);
    private readonly Dictionary<Guid, EntityMemoryFact> _facts = state.Facts.ToDictionary(x => x.Id);
    private readonly Dictionary<Guid, TranslationImpactEvent> _events = state.Events.ToDictionary(x => x.Id);
    public Guid Entity(string key, MemoryEntityKind kind, string label, Guid? legacyId = null)
    {
        Guid id = EntityMemoryIdentity.StableId(state.ProjectId, "entity:" + key);
        if (!_entities.ContainsKey(id)) { var value = new MemoryEntity(id, state.ProjectId, kind, label, legacyId); state.Entities.Add(value); _entities.Add(id, value); }
        return id;
    }
    public Guid Evidence(Guid chapter, int number, string hash, int start, int length, Guid? corpusId = null)
    {
        Guid id = EntityMemoryIdentity.StableId(state.ProjectId, $"evidence:{chapter:D}:{number}:{hash}:{start}:{length}:{corpusId}");
        if (!_evidence.ContainsKey(id)) { var value = new MemoryEvidence(id, state.ProjectId, chapter, number, hash, start, length, corpusId); state.Evidence.Add(value); _evidence.Add(id, value); }
        return id;
    }
    public EntityMemoryFact Fact(EntityMemoryFact draft)
    {
        draft = draft with { ProjectId = state.ProjectId, Id = Guid.Empty };
        string lineage = "fact:" + draft.Provenance.Key;
        string key = lineage + "@" + EntityMemoryIdentity.Fingerprint(draft);
        var fact = draft with { Id = EntityMemoryIdentity.StableId(state.ProjectId, key), Provenance = draft.Provenance with { Key = key, LineageKey = lineage } };
        if (_facts.TryGetValue(fact.Id, out var old)) return old;
        state.Facts.Add(fact); _facts.Add(fact.Id, fact); return fact;
    }
    public TranslationImpactEvent Event(TranslationImpactEvent draft)
    {
        draft = draft with { ProjectId = state.ProjectId, Id = Guid.Empty };
        string lineage = "event:" + draft.Provenance.Key;
        string key = lineage + "@" + EntityMemoryIdentity.Fingerprint(draft);
        var item = draft with { Id = EntityMemoryIdentity.StableId(state.ProjectId, key), Provenance = draft.Provenance with { Key = key, LineageKey = lineage } };
        if (_events.TryGetValue(item.Id, out var old)) return old;
        state.Events.Add(item); _events.Add(item.Id, item); return item;
    }
}

public static class EntityMemoryProjection
{
    public static void Import(EntityMemoryState memory, ProjectResources resources, BookIntelligenceState survey,
        CharacterCensusReport census, MissingIdentityReviewState audit)
    {
        if (memory.ProjectId != resources.ProjectId || memory.ProjectId != survey.ProjectId || memory.ProjectId != census.ProjectId || memory.ProjectId != audit.ProjectId)
            throw new InvalidOperationException("Cross-project memory projection rejected.");
        var builder = new EntityMemoryBuilder(memory);
        var profiles = new Dictionary<Guid, Guid>(); var candidates = new Dictionary<Guid, Guid>();
        foreach (var profile in resources.Characters.Profiles) profiles.Add(profile.Id, builder.Entity("profile:" + profile.Id, MemoryEntityKind.Character,
            string.IsNullOrWhiteSpace(profile.ChineseName) ? profile.VietnameseName : profile.ChineseName, profile.Id));
        foreach (var candidate in survey.CandidateIdentities) candidates.Add(candidate.Id,
            builder.Entity("candidate:" + candidate.Id, MemoryEntityKind.Candidate, candidate.ChineseName, candidate.Id));
        var mentioned = census.Candidates.ToDictionary(x => x.ChineseName, StringComparer.Ordinal);
        var originalEvidence = survey.Chapters.SelectMany(c => c.Evidence.Select(e => (Chapter: c, Evidence: e))).ToDictionary(x => x.Evidence.Id);
        Guid Handle(Guid id) => profiles.ContainsKey(id) && candidates.ContainsKey(id)
            ? builder.Entity("ambiguous-id:" + id, MemoryEntityKind.UnresolvedMention, "Ambiguous legacy ID " + id, id)
            : profiles.TryGetValue(id, out var p) ? p : candidates.TryGetValue(id, out var c) ? c :
                builder.Entity("unresolved-id:" + id, MemoryEntityKind.UnresolvedMention, "Unresolved " + id, id);
        Guid[] Proof(IEnumerable<Guid> ids) => ids.Distinct().Where(id => originalEvidence.ContainsKey(id) && originalEvidence[id].Evidence.LengthUtf16 > 0 &&
            originalEvidence[id].Evidence.SourceSha256.Length == 64).Select(id =>
        {
            var (chapter, evidence) = originalEvidence[id];
            return builder.Evidence(evidence.ChapterId, chapter.ChapterNumber, evidence.SourceSha256, evidence.StartUtf16, evidence.LengthUtf16, evidence.Id);
        }).ToArray();
        Guid[] Mentions(CharacterMentionCandidate? candidate) => candidate is null ? [] : candidate.Mentions.Take(4)
            .Select(x => builder.Evidence(x.ChapterId, x.ChapterNumber, x.SourceSha256, x.StartUtf16, x.LengthUtf16)).ToArray();
        MemoryProvenance Origin(MemoryOrigin origin, string key, string explanation, Guid? legacy, CharacterMentionCandidate? candidate = null) =>
            new(origin, key, explanation, legacy, CensusName: candidate?.ChineseName ?? "", CensusFingerprint: candidate is null ? "" : CharacterMentionCensus.Fingerprint(candidate));
        void Name(Guid subject, string name, MemoryFactKind kind, MemoryProvenance provenance, MemoryReviewStatus status, Guid[] evidence, int? observed) =>
            builder.Fact(new() { SubjectId = subject, Kind = kind, Value = name, InitialStatus = evidence.Length == 0 && status != MemoryReviewStatus.Rejected ? MemoryReviewStatus.Ambiguous : status,
                EvidenceIds = evidence, Timeline = new(DiscoveredAtChapter: observed), Confidence = 0.3, Provenance = provenance });

        foreach (var profile in resources.Characters.Profiles)
        {
            foreach (string name in new[] { profile.ChineseName }.Concat(profile.Aliases).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal))
            {
                var candidate = mentioned.GetValueOrDefault(name);
                Name(profiles[profile.Id], name, name == profile.ChineseName ? MemoryFactKind.PrimaryName : MemoryFactKind.Alias,
                    Origin(MemoryOrigin.ProjectCharacter, $"profile:{profile.Id}:{name}", "Existing resource assertion; chronology still needs separate confirmation.", profile.Id, candidate),
                    MemoryReviewStatus.Accepted, Mentions(candidate), candidate?.FirstChapter);
            }
        }
        foreach (var candidate in survey.CandidateIdentities)
        {
            var evidence = Proof(candidate.EvidenceHistory);
            Name(candidates[candidate.Id], candidate.ChineseName, MemoryFactKind.PrimaryName,
                Origin(MemoryOrigin.CandidateIdentity, "candidate:" + candidate.Id, "Legacy candidate, not an automatic identity merge.", candidate.Id),
                candidate.Status == CandidateIdentityStatus.Rejected ? MemoryReviewStatus.Rejected : candidate.Status == CandidateIdentityStatus.IdentityConflict ? MemoryReviewStatus.Conflict : MemoryReviewStatus.Proposed,
                evidence, survey.Chapters.Where(c => c.Evidence.Any(e => candidate.EvidenceHistory.Contains(e.Id))).Select(x => (int?)x.ChapterNumber).Min());
            foreach (string alias in candidate.Aliases.Where(x => x != candidate.ChineseName).Distinct(StringComparer.Ordinal))
                Name(candidates[candidate.Id], alias, MemoryFactKind.Alias,
                    Origin(MemoryOrigin.CandidateIdentity, $"candidate:{candidate.Id}:alias:{alias}", "Candidate alias requires independent confirmation.", candidate.Id),
                    MemoryReviewStatus.Proposed, evidence, null);
            if (candidate.ProposedExistingCharacterId is Guid target)
                builder.Fact(new() { SubjectId = candidates[candidate.Id], TargetId = profiles.TryGetValue(target, out var targetHandle) ? targetHandle : Handle(target), Kind = MemoryFactKind.IdentityAssociation,
                    AssociationKind = MemoryAssociationKind.PossibleIdentity, Value = "Possible legacy identity association; never union identities.",
                    EvidenceIds = evidence, Confidence = 0.2, Provenance = Origin(MemoryOrigin.CandidateIdentity, "candidate-link:" + candidate.Id, "No body/consciousness merge.", candidate.Id) });
        }
        foreach (var candidate in census.Candidates.Where(x => x.Category != MentionCandidateCategory.NoiseOrUncertain))
        {
            Guid? legacy = EntityResolutionService.ResolveStableId(survey, resources, candidate.ChineseName); // read-only, never Resolve/merge
            int profileOwners = resources.Characters.Profiles.Count(p => p.ChineseName == candidate.ChineseName || p.Aliases.Contains(candidate.ChineseName, StringComparer.Ordinal));
            bool profileOwns = profileOwners == 1;
            Guid subject = legacy is Guid id && profileOwns && profiles.TryGetValue(id, out var p) ? p :
                legacy is Guid cid && profileOwners == 0 && candidates.TryGetValue(cid, out var c) ? c : builder.Entity("mention:" + candidate.ChineseName,
                candidate.Category == MentionCandidateCategory.AddressingTerm ? MemoryEntityKind.AddressingCatalogue : MemoryEntityKind.UnresolvedMention, candidate.ChineseName);
            var kind = candidate.Category switch { MentionCandidateCategory.AliasCandidate => MemoryFactKind.Nickname,
                MentionCandidateCategory.TitleCandidate => MemoryFactKind.Title, MentionCandidateCategory.AddressingTerm => MemoryFactKind.FamilyAddressing, _ => MemoryFactKind.PrimaryName };
            if (profileOwns && memory.Facts.Any(x => x.SubjectId == subject && x.Kind == kind && x.Value == candidate.ChineseName && x.Provenance.Origin == MemoryOrigin.ProjectCharacter)) continue;
            Name(subject, candidate.ChineseName, kind, Origin(MemoryOrigin.Census, "census:" + candidate.ChineseName,
                "CPU census hypothesis; no new accepted character or translation alias.", null, candidate), profileOwners > 1 ? MemoryReviewStatus.Ambiguous : MemoryReviewStatus.Proposed,
                Mentions(candidate), candidate.FirstChapter);
        }
        foreach (var relation in survey.RelationshipGraph.Where(x => x.RelationType != KnowledgeRelationType.Unknown))
            builder.Fact(new() { SubjectId = Handle(relation.SubjectCharacterId), TargetId = Handle(relation.TargetCharacterId), Kind = MemoryFactKind.Relationship,
                RelationType = relation.RelationType, Value = relation.RelationType.ToString(), EvidenceIds = Proof(relation.EvidenceIds), Confidence = 0.5,
                InitialStatus = relation.Status switch { KnowledgeRelationshipStatus.Accepted or KnowledgeRelationshipStatus.Confirmed => MemoryReviewStatus.Accepted,
                    KnowledgeRelationshipStatus.Rejected => MemoryReviewStatus.Rejected, KnowledgeRelationshipStatus.Conflict => MemoryReviewStatus.Conflict,
                    KnowledgeRelationshipStatus.Ambiguous => MemoryReviewStatus.Ambiguous, _ => MemoryReviewStatus.Proposed },
                Timeline = new(relation.ValidFromChapter, relation.ValidToChapter, relation.FirstObservedChapter, relation.RevealedAtChapter),
                Provenance = Origin(MemoryOrigin.LegacyRelationship, "relation:" + relation.Id, "Existing typed relationship, not automatically confirmed for 2A context.", relation.Id) });
        foreach (var period in survey.AddressingTimeline)
            builder.Fact(new() { SubjectId = Handle(period.SpeakerCharacterId), TargetId = Handle(period.ListenerCharacterId), Kind = MemoryFactKind.Addressing,
                Value = "Directional addressing period", SelfReference = period.SelfReference, AddresseeReference = period.AddresseeReference,
                InitialStatus = period.Approved ? MemoryReviewStatus.Accepted : MemoryReviewStatus.Proposed, Confidence = 0.5, EvidenceIds = Proof(period.EvidenceIds),
                Timeline = new(period.ValidFromChapter, period.ValidToChapter, RevealedAtChapter: period.RevealedAtChapter),
                Provenance = Origin(MemoryOrigin.LegacyAddressing, "addressing:" + period.Id, "Existing AddressingRulePeriod; preserve direction and timing.", period.Id) });
        foreach (var decision in audit.Latest.Where(x => x.Kind == MissingIdentityDecisionKind.LinkExisting))
        {
            Guid subject = builder.Entity("mention:" + decision.ChineseName, MemoryEntityKind.UnresolvedMention, decision.ChineseName);
            builder.Fact(new() { SubjectId = subject, TargetId = Handle(decision.TargetCharacterId!.Value), Kind = MemoryFactKind.IdentityAssociation,
                AssociationKind = MemoryAssociationKind.PossibleIdentity, Value = "Audit-only " + decision.LinkKind, Confidence = 0.2,
                EvidenceIds = decision.Mentions.Take(4).Select(x => builder.Evidence(x.ChapterId, x.ChapterNumber, x.SourceSha256, x.StartUtf16, x.LengthUtf16)).ToArray(),
                Timeline = new(DiscoveredAtChapter: decision.Mentions.Min(x => x.ChapterNumber)),
                Provenance = new(MemoryOrigin.AuditOnly, "audit-link:" + decision.Id, "P0.1/P0.2 LinkExisting is audit-only; explicit separate alias promotion required.",
                    decision.Id, CensusName: decision.ChineseName, CensusFingerprint: string.IsNullOrEmpty(decision.SourceFingerprint) ? CharacterMentionCensus.Fingerprint(decision.Mentions) : decision.SourceFingerprint) });
        }
    }
}
