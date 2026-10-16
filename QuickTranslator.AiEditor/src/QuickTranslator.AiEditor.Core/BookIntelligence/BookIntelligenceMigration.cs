namespace QuickTranslator.AiEditor.Core.BookIntelligence;

internal static class BookIntelligenceMigration
{
    public static BookIntelligenceState ToCurrent(BookIntelligenceState state)
    {
        if (state.SchemaVersion == BookIntelligenceState.CurrentSchemaVersion) return state;
        if (state.SchemaVersion != 1)
            throw new InvalidDataException($"Không hỗ trợ schema AI Book Intelligence {state.SchemaVersion}.");

        state.ScanMode = IntelligenceScanMode.FastAdaptive;
        state.LowPriorityAuditRate = 0.15;
        state.AnalysisConcurrency = 1;
        state.Metrics ??= new IntelligenceAnalysisMetrics();
        state.CandidateIdentities ??= [];
        state.RelationshipGraph ??= [];
        state.DerivedRelationships ??= [];
        state.AddressingTimeline ??= [];
        state.SceneContexts ??= [];
        state.PendingResourceCommits ??= [];
        foreach (CorpusEvidence evidence in state.Evidence)
        {
            evidence.Priority = EvidencePriority.High;
            evidence.PriorityReason = "Migrated Session 1 signal evidence";
            evidence.SemanticGroup = evidence.Kind.ToString().ToLowerInvariant();
            evidence.CandidateNames ??= [];
        }
        foreach (IntelligenceCharacterSuggestion suggestion in state.Characters)
        {
            var identity = new CandidateIdentity
            {
                ChineseName = suggestion.ChineseName,
                VietnameseName = suggestion.VietnameseName,
                Aliases = suggestion.Aliases.ToList(),
                EvidenceHistory = suggestion.EvidenceIds.Distinct().ToList(),
                ProposedExistingCharacterId = suggestion.PossibleExistingCharacterId,
                Status = suggestion.Status switch
                {
                    IntelligenceReviewStatus.Merged => CandidateIdentityStatus.ConfirmedIdentityMerge,
                    IntelligenceReviewStatus.Rejected => CandidateIdentityStatus.Rejected,
                    _ when suggestion.PossibleExistingCharacterId is not null => CandidateIdentityStatus.ProposedAliasLink,
                    _ => CandidateIdentityStatus.Candidate
                }
            };
            suggestion.CandidateIdentityId = identity.Id;
            state.CandidateIdentities.Add(identity);
        }
        state.SchemaVersion = BookIntelligenceState.CurrentSchemaVersion;
        state.AnalyzerVersion = "2";
        return state;
    }
}
