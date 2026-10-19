namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record SemanticAuditedFinding(SemanticFinding Finding, bool EvidenceValid, bool CanPromote, IReadOnlyList<string> Issues);
public sealed record SemanticAudit(IReadOnlyList<SemanticAuditedFinding> Findings, IReadOnlyList<string> Issues, IReadOnlyList<Guid> UnprocessedCpuIds,
    IReadOnlyList<Guid>? ReviewedEvidenceIds = null)
{
    public bool Validated => Issues.Count == 0 && UnprocessedCpuIds.Count == 0 && Findings.All(x => x.EvidenceValid && x.Issues.Count == 0);
}

/// <summary>Independent mechanical audit, NOT a claim that the model's semantics are true.</summary>
public static class SemanticQualityAuditor
{
    private static readonly System.Text.RegularExpressions.Regex NonactualCue = new("想要|希望|打算|计划|提出|请求|建议|如果|假如|要是|并未|没有|尚未|不曾|不会",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    public static SemanticAudit Audit(SemanticResponse response, SemanticSurveyRequest request, IReadOnlyList<CensusSource> sources)
    {
        var issues = new List<string>(); var output = new List<SemanticAuditedFinding>(); var e = request.Evidence;
        var evidence = request.Units.Select(x => x.Evidence).ToDictionary(x => x.Id);
        var source = sources.SingleOrDefault(x => x.ChapterId == e.ChapterId);
        bool sourceCurrent = source is not null && source.ChapterNumber == e.ChapterNumber && e.ProjectId == request.ProjectId &&
            CorpusIntegrityAuditor.SourceHash(source.OriginalText) == e.SourceSha256;
        if (!sourceCurrent) issues.Add("StaleOrForeignEvidence");
        if (evidence.Values.Any(x => x.ProjectId != request.ProjectId || x.ChapterId != e.ChapterId || x.ChapterNumber != request.ChapterNumber || x.SourceSha256 != e.SourceSha256 ||
            x.StartUtf16 < 0 || x.LengthUtf16 <= 0 || (long)x.StartUtf16 + x.LengthUtf16 > (source?.OriginalText.Length ?? 0))) issues.Add("StaleOrForeignEvidence");
        if (response.ProjectId != request.ProjectId || response.ChapterId != e.ChapterId || response.SchemaVersion != request.WireVersion) issues.Add("ResponseScopeMismatch");
        if (response.ReviewedEvidenceIds.Distinct().Count() != response.ReviewedEvidenceIds.Count || response.ReviewedEvidenceIds.Any(x => !evidence.ContainsKey(x))) issues.Add("ForeignOrDuplicateCoverage");
        bool scopeValid = issues.Count == 0;
        var reviewed = scopeValid ? response.ReviewedEvidenceIds.ToHashSet() : [];
        if (reviewed.Count != evidence.Count) issues.Add("IncompleteEvidenceCoverage");
        var known = request.AllowedIdentities.ToDictionary(x => x.EntityId); var cpu = request.CpuCandidates.Select(x => x.Id).ToHashSet();
        var localDefs = response.Findings.SelectMany(Identities).Where(x => x.EntityId is null && x.LocalId.Length > 0).GroupBy(x => x.LocalId)
            .ToDictionary(x => x.Key, x => x.Select(i => (i.Name, i.Kind)).Distinct().Count());
        var processed = new HashSet<Guid>();
        foreach (var reported in response.Findings)
        {
            var finding = sourceCurrent ? AnchorSpans(reported, evidence, source!.OriginalText) : reported;
            var errors = new List<string>();
            var reviewIssues = new List<string>();
            bool eligible = true;
            if (finding.SourceEvidenceIds.Count == 0 || finding.SourceEvidenceIds.Distinct().Count() != finding.SourceEvidenceIds.Count ||
                finding.SourceEvidenceIds.Any(id => !evidence.ContainsKey(id) || !reviewed.Contains(id)) || finding.SupportSpans.Count == 0 ||
                !finding.SourceEvidenceIds.ToHashSet().SetEquals(finding.SupportSpans.Select(x => x.EvidenceId))) errors.Add("MissingOrForeignSupport");
            foreach (var span in finding.SupportSpans)
            {
                long end = (long)span.StartUtf16 + span.LengthUtf16;
                if (!sourceCurrent || !evidence.TryGetValue(span.EvidenceId, out var proof) || span.StartUtf16 < proof.StartUtf16 || end > (long)proof.StartUtf16 + proof.LengthUtf16 ||
                    span.LengthUtf16 is < 1 or > 240 || end > source!.OriginalText.Length || Splits(source.OriginalText, span.StartUtf16) || Splits(source.OriginalText, (int)end) ||
                    source.OriginalText.Substring(span.StartUtf16, span.LengthUtf16) != span.Text) errors.Add("SupportSpanMismatch");
            }
            string quotes = string.Join("\n", finding.SupportSpans.Select(x => x.Text));
            if (request.WireVersion == 2)
            {
                // Request-local IDs must not silently join homonyms from independent excerpts.
                foreach (var identity in Identities(finding).Where(x => x.EntityId is null && x.LocalId.Length > 0))
                {
                    var occurrences = response.Findings.Where(f => Identities(f).Any(i => i.EntityId is null && i.LocalId == identity.LocalId)).ToArray();
                    var ranges = occurrences.SelectMany(f => f.SourceEvidenceIds).Distinct().ToArray();
                    if (ranges.Length > 1 && !occurrences.Any(f => ranges.All(f.SourceEvidenceIds.Contains))) reviewIssues.Add("CrossEvidenceIdentityNeedsReview");
                }
                if (finding.FindingType is SemanticFindingType.RelationshipExtraction or SemanticFindingType.DirectionalAddressing)
                {
                    var a = finding.FindingType == SemanticFindingType.RelationshipExtraction ? finding.SubjectIdentity : finding.SpeakerIdentity;
                    var b = finding.FindingType == SemanticFindingType.RelationshipExtraction ? finding.TargetIdentity : finding.ListenerIdentity;
                    if (a is not null && b is not null && !finding.SupportSpans.Any(s => s.Text.Contains(a.Name, StringComparison.Ordinal) && s.Text.Contains(b.Name, StringComparison.Ordinal)))
                        reviewIssues.Add("NoJointParticipantProof");
                }
                if (finding.FindingType == SemanticFindingType.RelationshipExtraction && !HasRelationCue(finding.RelationshipType, quotes))
                    reviewIssues.Add("CooccurrenceIsNotRelationshipProof");
                if (finding.FindingType == SemanticFindingType.DirectionalAddressing &&
                    (!quotes.Contains(finding.SelfReference, StringComparison.Ordinal) || !quotes.Contains(finding.AddresseeReference, StringComparison.Ordinal)))
                    reviewIssues.Add("UnquotedDirectionalAddressing");
            }
            foreach (var identity in Identities(finding))
            {
                if (identity.EntityId is Guid id)
                {
                    if (identity.Status != SemanticResolution.Existing || identity.LocalId.Length != 0 || !known.TryGetValue(id, out var allowed) ||
                        !allowed.Names.Contains(identity.Name, StringComparer.Ordinal)) errors.Add("UnknownOrLabelResolvedIdentity");
                }
                else if (identity.Status == SemanticResolution.NewIdentity)
                {
                    if (identity.LocalId.Length is < 1 or > 64 || !identity.LocalId.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ||
                        identity.Name.Length == 0 || !quotes.Contains(identity.Name, StringComparison.Ordinal) || localDefs.GetValueOrDefault(identity.LocalId) != 1)
                        errors.Add("UnprovedOrConflictingLocalIdentity");
                }
                else { eligible = false; if (identity.Status == SemanticResolution.Existing) errors.Add("MissingExistingId"); }
            }
            if (finding.EffectiveChapter is <= 0 || finding.RevealedChapter is <= 0 || finding.EffectiveToChapter is <= 0 ||
                finding.EffectiveChapter > request.ChapterNumber || finding.RevealedChapter > request.ChapterNumber ||
                finding.EffectiveToChapter is int to && finding.EffectiveChapter > to) errors.Add("InvalidOrFutureTimeline");
            // A historical event is allowed with an unknown effective date, never default it to today.
            if (finding.FindingType == SemanticFindingType.EventVerification)
            {
                if (finding.EventType is null && finding.EventActuality != SemanticActuality.NotAnEvent) errors.Add("MissingEventType");
                if (finding.EventActuality is not (SemanticActuality.ActualCompleted or SemanticActuality.HistoricalReference) || finding.VerifiedParticipants.Count == 0) eligible = false;
                if (finding.CpuCandidateIds.Any(id => !cpu.Contains(id))) errors.Add("ForeignCpuCandidate");
                if (finding.CpuCandidateIds.Any(id => !request.Units.Any(u => u.CpuCandidates.Any(c => c.Id == id) && finding.SourceEvidenceIds.Contains(u.Evidence.Id))))
                    errors.Add("CpuCandidateEvidenceMismatch");
                if (finding.EventActuality == SemanticActuality.ActualCompleted && NonactualCue.IsMatch(quotes)) errors.Add("ActualityContradictionNeedsReview");
                if (finding.EventType == TranslationImpactEventType.RevealIdentity && finding.EventActuality == SemanticActuality.ActualCompleted &&
                    quotes.Contains("其实是", StringComparison.Ordinal) && !new[] { "身份", "本名", "真名", "化名" }.Any(x => quotes.Contains(x, StringComparison.Ordinal))) errors.Add("ExplanationIsNotProofOfIdentityReveal");
                if (finding.EventActuality == SemanticActuality.HistoricalReference && finding.EffectiveChapter == request.ChapterNumber &&
                    new[] { "去年", "以前", "当年", "曾经" }.Any(x => quotes.Contains(x, StringComparison.Ordinal))) errors.Add("HistoricalStartGuessedFromMention");
            }
            else if (finding.CpuCandidateIds.Count > 0) errors.Add("EventCoverageRequiresEventFinding");
            if (finding.AffectedFactIds.Any(id => !request.ContextFactIds.Contains(id))) errors.Add("UnknownAffectedFact");
            if (finding.FindingType == SemanticFindingType.RelationshipExtraction && (finding.SubjectIdentity is null || finding.TargetIdentity is null ||
                finding.RelationshipType == KnowledgeRelationType.Unknown || Same(finding.SubjectIdentity, finding.TargetIdentity))) errors.Add("InvalidDirectedRelation");
            if (finding.FindingType == SemanticFindingType.RelationshipExtraction && finding.EventActuality is not (SemanticActuality.ActualCompleted or SemanticActuality.HistoricalReference)) eligible = false;
            if (finding.FindingType == SemanticFindingType.DirectionalAddressing && (finding.SpeakerIdentity is null || finding.ListenerIdentity is null ||
                Same(finding.SpeakerIdentity, finding.ListenerIdentity) || finding.SelfReference.Length == 0 || finding.AddresseeReference.Length == 0)) eligible = false;
            if (finding.FindingType is SemanticFindingType.CharacterIdentification or SemanticFindingType.AliasAndIdentityResolution &&
                (finding.SubjectIdentity is null || finding.Value.Length == 0 || !quotes.Contains(finding.Value, StringComparison.Ordinal))) errors.Add("UnprovedNameOrAlias");
            if (finding.FindingType == SemanticFindingType.GapAndContradictionAudit || finding.IdentityResolutionStatus is SemanticResolution.Unresolved or SemanticResolution.Ambiguous) eligible = false;
            if (finding.SubjectIdentity?.EntityId is Guid subject && finding.FindingType is SemanticFindingType.RelationshipExtraction or SemanticFindingType.CharacterIdentification)
            {
                var context = (request.ContextFacts ?? []).Select(x => new EntityMemoryFact { Id = x.Id, SubjectId = x.SubjectId, TargetId = x.TargetId, Kind = x.Kind,
                    Value = x.Value, RelationType = x.RelationType, SelfReference = x.SelfReference, AddresseeReference = x.AddresseeReference, Timeline = x.Timeline }).ToList();
                Guid draftId = EntityMemoryIdentity.StableId(request.ProjectId, "audit-draft:" + EntityMemoryIdentity.Fingerprint(finding));
                var draft = new EntityMemoryFact { Id = draftId, SubjectId = subject, TargetId = finding.TargetIdentity?.EntityId,
                    Kind = finding.FindingType == SemanticFindingType.RelationshipExtraction ? MemoryFactKind.Relationship : MemoryFactKind.PrimaryName,
                    Value = finding.Value, RelationType = finding.RelationshipType, Timeline = new(finding.EffectiveChapter, finding.EffectiveToChapter, request.ChapterNumber, finding.RevealedChapter) };
                context.Add(draft); var times = context.ToDictionary(x => x.Id, x => x.Timeline);
                if (EntityMemoryConflicts.FindFacts(context, id => times[id]).Any(x => x.LeftId == draftId || x.RightId == draftId)) reviewIssues.Add("ContradictionWithConfirmedMemory");
            }
            if (errors.Count == 0 && scopeValid) foreach (Guid id in finding.CpuCandidateIds) processed.Add(id);
            // Persist refs/diagnostics, not repeated source quotations. Navigation rehydrates text.
            output.Add(new(finding with { SupportSpans = finding.SupportSpans.Select(x => x with { Text = "" }).ToArray() },
                errors.Count == 0 && scopeValid, eligible && errors.Count == 0 && scopeValid && reviewIssues.Count == 0, errors.Concat(reviewIssues).Distinct().ToArray()));
        }
        return new(output, issues.Distinct().ToArray(), cpu.Except(processed).ToArray(), reviewed.ToArray());
    }
    public static IEnumerable<SemanticIdentity> Identities(SemanticFinding f) => new[] { f.SubjectIdentity, f.TargetIdentity, f.SpeakerIdentity, f.ListenerIdentity }
        .OfType<SemanticIdentity>().Concat(f.VerifiedParticipants);
    private static bool Same(SemanticIdentity a, SemanticIdentity b) => a.EntityId is Guid id && b.EntityId == id || a.EntityId is null && a.LocalId.Length > 0 && a.LocalId == b.LocalId;
    private static bool HasRelationCue(KnowledgeRelationType type, string quotes) => (type switch
    {
        KnowledgeRelationType.Parent or KnowledgeRelationType.Child or KnowledgeRelationType.BiologicalFather or KnowledgeRelationType.BiologicalMother or KnowledgeRelationType.BiologicalChild => new[] { "父", "母", "爹", "娘", "爸", "妈", "儿子", "女儿", "亲生" },
        KnowledgeRelationType.AdoptiveParent or KnowledgeRelationType.AdoptiveChild => ["养父", "养母", "养子", "养女", "收养"],
        KnowledgeRelationType.FullSibling or KnowledgeRelationType.PaternalHalfSibling or KnowledgeRelationType.MaternalHalfSibling or KnowledgeRelationType.UnknownSibling or KnowledgeRelationType.SwornSibling => ["兄", "弟", "姐", "妹", "结拜", "同父", "同母"],
        KnowledgeRelationType.Grandfather or KnowledgeRelationType.Grandmother or KnowledgeRelationType.Grandchild => ["爷", "奶", "祖", "外公", "外婆", "孙"],
        KnowledgeRelationType.Spouse => ["夫妻", "结婚", "丈夫", "妻", "老公", "老婆", "夫人", "夫君"],
        KnowledgeRelationType.RomanticPartner => ["恋", "爱", "男友", "女友", "情人"],
        KnowledgeRelationType.Teacher or KnowledgeRelationType.Disciple => ["师", "徒"],
        KnowledgeRelationType.Superior or KnowledgeRelationType.Subordinate => ["上司", "下属", "主子", "主人", "部下", "老板"],
        _ => Array.Empty<string>()
    }).Any(x => quotes.Contains(x, StringComparison.Ordinal));
    private static readonly System.Text.RegularExpressions.Regex LineBreak = new("\r\n|\n|\r",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    /// <summary>
    /// Models quote reliably but cannot count UTF-16 offsets. The proof is the quoted text: locate it
    /// verbatim inside the span's declared evidence window and take the occurrence nearest the reported
    /// offset. Only line breaks are tolerated (see <see cref="QuotePattern"/>). The located range and the
    /// exact source text replace the model's numbers. An unlocatable quote keeps the reported span and
    /// fails the SupportSpanMismatch check; nothing is ever located outside the declared evidence.
    /// </summary>
    internal static SemanticFinding AnchorSpans(SemanticFinding finding, IReadOnlyDictionary<Guid, MemoryEvidence> evidence, string text) =>
        finding with { SupportSpans = finding.SupportSpans.Select(span => AnchorSpan(span, evidence, text)).ToArray() };
    private static SemanticSupport AnchorSpan(SemanticSupport span, IReadOnlyDictionary<Guid, MemoryEvidence> evidence, string text)
    {
        if (span.Text.Length == 0 || !evidence.TryGetValue(span.EvidenceId, out var proof) || proof.StartUtf16 < 0 || proof.LengthUtf16 <= 0 ||
            (long)proof.StartUtf16 + proof.LengthUtf16 > text.Length) return span;
        // Already exact at the reported range: keep it unchanged.
        if (span.LengthUtf16 == span.Text.Length && span.StartUtf16 >= proof.StartUtf16 &&
            (long)span.StartUtf16 + span.LengthUtf16 <= (long)proof.StartUtf16 + proof.LengthUtf16 &&
            string.CompareOrdinal(text, span.StartUtf16, span.Text, 0, span.LengthUtf16) == 0) return span;
        try
        {
            if (LineBreak.Replace(span.Text, "").Length == 0) return span;
            string pattern = QuotePattern(span.Text);
            string window = text.Substring(proof.StartUtf16, proof.LengthUtf16);
            System.Text.RegularExpressions.Match? best = null;
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(window, pattern,
                System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
                if (match.Length > 0 && (best is null || Math.Abs(proof.StartUtf16 + (long)match.Index - span.StartUtf16) < Math.Abs(proof.StartUtf16 + (long)best.Index - span.StartUtf16)))
                    best = match;
            return best is null ? span : span with { StartUtf16 = proof.StartUtf16 + best.Index, LengthUtf16 = best.Length, Text = best.Value };
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { return span; }
    }
    /// <summary>
    /// Every quoted character must match verbatim and in order. A line break written in the quote must
    /// exist in the source (any CRLF/LF/CR style). A source line break the quote omits is allowed only
    /// BETWEEN two quoted characters (hard-wrapped web novels). Spaces, tabs and full-width spaces are
    /// never skipped, and a surrogate pair is never split.
    /// </summary>
    internal static string QuotePattern(string quote)
    {
        const string Break = "(?:\r\n|\n|\r)";
        return string.Join(Break + "+", LineBreak.Split(quote).Select(segment =>
        {
            var units = new List<string>();
            for (int i = 0; i < segment.Length; i++)
                units.Add(char.IsHighSurrogate(segment[i]) && i + 1 < segment.Length && char.IsLowSurrogate(segment[i + 1])
                    ? segment.Substring(i++, 2) : segment[i].ToString());
            return string.Join(Break + "*", units.Select(System.Text.RegularExpressions.Regex.Escape));
        }));
    }
    private static bool Splits(string s, int n) => n > 0 && n < s.Length && char.IsHighSurrogate(s[n - 1]) && char.IsLowSurrogate(s[n]);
}
