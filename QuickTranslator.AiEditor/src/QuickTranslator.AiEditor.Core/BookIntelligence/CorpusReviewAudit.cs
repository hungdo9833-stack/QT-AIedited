using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record CorpusAuditReviewItem(string Group, string Title, string Reason,
    Guid ChapterId, int StartUtf16, IReadOnlyList<CorpusEvidence> Evidence, string? MissingIdentityName = null);
public sealed record CorpusReviewAudit(CorpusIntegrityReport Integrity, CharacterCensusReport Census,
    IReadOnlyList<CorpusAuditReviewItem> Items, int IdentityReviewsPending = 0)
{
    public bool ManualReviewComplete => Integrity.ManualReviewComplete && IdentityReviewsPending == 0;
    public bool Complete => Integrity.AiSurveyComplete && ManualReviewComplete;
    public bool AccuracyVerified => false;
    public string Summary => $"Corpus: {(Integrity.CorpusProcessed ? "xử lý xong" : Integrity.Status.ToString())} · " +
        $"AI survey: {(Integrity.AiSurveyComplete ? "xong" : "chưa xong")} · Manual review: {(ManualReviewComplete ? "xong" : "NeedsReview")} · " +
        $"Accuracy: chưa được kiểm chứng · active {Integrity.IndexedChapters}/{Integrity.ActiveChapters} chương · " +
        $"current {Integrity.Current} · analyzed {Integrity.Analyzed} · blocked {Integrity.Blocked} · missing {Integrity.Missing} · " +
        $"stale {Integrity.Stale} · skipped {Integrity.Skipped} · unverified {Integrity.Unverified} · Identity review pending {IdentityReviewsPending}. Không tự accept.";

    public Task SaveDiagnosticsAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        if (session.Manifest.ProjectId != Integrity.ProjectId) throw new InvalidOperationException("Audit belongs to another project.");
        return SimpleFileStorage.WriteAsync(session.Resolve("Settings/abi3-corpus-audit.v1.json"),
            JsonSerializer.SerializeToUtf8Bytes(new Diagnostic(1, Integrity, Complete, Summary), DocumentJson.Options),
            bytes => JsonSerializer.Deserialize<Diagnostic>(bytes, DocumentJson.Options)
                ?? throw new InvalidDataException("Empty audit diagnostic."), cancellationToken);
    }
    private sealed record Diagnostic(int SchemaVersion, CorpusIntegrityReport Integrity, bool Complete, string Summary);

    public static CorpusReviewAudit Build(CorpusIntegrityReport integrity, CharacterCensusReport census, BookIntelligenceState state,
        MissingIdentityReviewState? identityReviews = null, ProjectResources? resources = null)
    {
        if (integrity.ProjectId != census.ProjectId || state.ProjectId != census.ProjectId ||
            integrity.StateRevision != state.Revision || census.SurveyRevision != state.Revision)
            throw new InvalidOperationException("Audit snapshots do not match current survey revision.");
        var items = new List<CorpusAuditReviewItem>();
        if (identityReviews is not null && (identityReviews.ProjectId != state.ProjectId || resources?.ProjectId != state.ProjectId))
            throw new InvalidOperationException("Identity decisions/resources belong to another project.");
        var latest = identityReviews?.Latest.ToDictionary(x => x.ChineseName, StringComparer.Ordinal) ?? [];
        int identityPending = 0;
        foreach (var missing in census.MissingIdentities)
        {
            if (latest.ContainsKey(missing.ChineseName)) continue; // handled with provenance below
            identityPending++;
            items.Add(new("Missing identity", $"{missing.ChineseName} · {missing.Frequency} lượt · chương {missing.FirstChapter}–{missing.LastChapter}",
                $"PersonCandidate: ứng viên CPU chưa có hồ sơ/review. Không khẳng định danh tính. Hiển thị tối đa {CharacterMentionCensus.MaximumStoredMentions} vị trí; tần suất đếm toàn nguồn.",
                missing.Mentions[0].ChapterId, missing.Mentions[0].StartUtf16,
                missing.Mentions.Select(x => x.NavigationEvidence(missing.ChineseName)).ToArray(), missing.ChineseName));
        }
        foreach (var candidate in census.Candidates.Where(x => x.Category != MentionCandidateCategory.PersonCandidate))
        {
            bool identityLike = candidate.Category is MentionCandidateCategory.AliasCandidate or MentionCandidateCategory.TitleCandidate;
            if (latest.ContainsKey(candidate.ChineseName) && candidate.Category != MentionCandidateCategory.AddressingTerm) continue;
            if (identityLike && !candidate.HasProfile && !candidate.HasReview) identityPending++;
            var first = candidate.Mentions[0];
            items.Add(new(CandidateGroup(candidate.Category), $"{candidate.ChineseName} · {candidate.Category} · {candidate.Frequency} lượt · chương {candidate.FirstChapter}–{candidate.LastChapter}",
                $"{candidate.Signals}. Chỉ là kiểm kê nguyên tác, không tự gộp hay đưa vào prompt dịch. " +
                (candidate.Category == MentionCandidateCategory.AddressingTerm ? "Từ xưng hô được giữ lại để kiểm tra ngữ cảnh, không phải tên riêng. " : "") +
                $"Hiển thị {candidate.Mentions.Count}/{candidate.Frequency} vị trí; fingerprint gắn toàn bộ lượt xuất hiện và nguồn.",
                first.ChapterId, first.StartUtf16, candidate.Mentions.Select(x => x.NavigationEvidence(candidate.ChineseName)).ToArray(),
                identityLike || candidate.Category == MentionCandidateCategory.NoiseOrUncertain ? candidate.ChineseName : null));
        }
        foreach (var decision in latest.Values)
        {
            var candidate = census.Candidates.SingleOrDefault(x => x.ChineseName == decision.ChineseName);
            bool current = MissingIdentityReviewStore.IsCurrent(decision, census, resources!);
            bool queued = current && decision.Kind == MissingIdentityDecisionKind.VerifyNewCharacter;
            if (candidate is not null && (!current || queued)) identityPending++;
            string group = !current && candidate is not null ? CandidateGroup(candidate.Category) : queued || !current ? "Unverified" : "Identity đã xử lý";
            var mentions = candidate?.Mentions ?? decision.Mentions;
            // Retired decision provenance is readable but never used as current navigation evidence.
            var evidence = candidate?.Mentions.Select(x => x.NavigationEvidence(decision.ChineseName)).ToArray() ?? [];
            string status = !current ? "Cần xác minh lại nguồn/nhân vật" : queued ? "Chờ xác minh nhân vật mới" : "Đã xử lý audit";
            string history = string.Join("\n", identityReviews!.History.Where(x => x.ChineseName == decision.ChineseName)
                .Select(x => $"{x.DecidedAt:O} · {x.Kind} · {x.LinkKind} · {x.TargetNameSnapshot} ({x.TargetCharacterId}) · {x.Note} · id={x.Id}"));
            items.Add(new(group, decision.ChineseName + " · " + status,
                status + ". Liên kết chỉ dùng cho audit, không sửa Characters/bí danh dịch.\n" + history,
                mentions[0].ChapterId, mentions[0].StartUtf16, evidence, candidate is null ? null : decision.ChineseName));
        }
        foreach (var chapter in integrity.Chapters.Where(x => x.Active && !x.Indexed || !x.Active && x.Current > 0))
            foreach (var issue in chapter.Issues)
                items.Add(new("Stale index", $"Chương {chapter.ChapterNumber} · {issue.Code} · UTF-16 {issue.StartUtf16}+{issue.LengthUtf16}",
                    issue.Reason, chapter.ChapterId, issue.StartUtf16, []));
        var active = integrity.Chapters.Where(x => x.Active && x.Indexed).Select(x => x.ChapterId).ToHashSet();
        foreach (var evidence in state.Evidence.Where(x => x.IsCurrent && active.Contains(x.ChapterId)))
        {
            if (evidence.AiBlocked)
                items.Add(new("Blocked excerpts", $"{evidence.ChapterId:D} · UTF-16 {evidence.StartUtf16} · {evidence.AiBlockReason}",
                    "Provider block: chưa phân tích, không retry/fallback để vượt chặn. Cần kiểm tra thủ công.", evidence.ChapterId, evidence.StartUtf16, [evidence]));
            else if (!evidence.AiAnalyzed)
                items.Add(new("Unverified", $"{evidence.ChapterId:D} · UTF-16 {evidence.StartUtf16} · " +
                    (CorpusIntegrityAuditor.Eligible(evidence, state.ScanMode) ? "Pending analysis" : "Skipped by scan policy"),
                    "Current evidence chưa phân tích. Indexed/Skipped không có nghĩa Verified/Complete.", evidence.ChapterId, evidence.StartUtf16, [evidence]));
        }
        var map = state.Evidence.Where(x => x.IsCurrent && active.Contains(x.ChapterId)).ToDictionary(x => x.Id);
        void Pending(string title, IEnumerable<Guid> ids)
        {
            var evidence = ids.Where(map.ContainsKey).Select(id => map[id]).ToArray();
            if (evidence.Length > 0) items.Add(new("Unverified", title, "Đề xuất AI chưa được người dùng duyệt.",
                evidence[0].ChapterId, evidence[0].StartUtf16, evidence));
        }
        foreach (var item in state.Characters.Where(x => x.Status is IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater)) Pending(item.ChineseName, item.EvidenceIds);
        foreach (var item in state.Relationships.Where(x => x.Status is IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater)) Pending(item.SpeakerChineseName + " → " + item.ListenerChineseName, item.EvidenceIds);
        foreach (var item in state.Glossary.Where(x => x.Status is IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater)) Pending(item.Chinese, item.EvidenceIds);
        return new(integrity, census, items, identityPending);
    }
    private static string CandidateGroup(MentionCandidateCategory category) => category switch
    {
        MentionCandidateCategory.PersonCandidate => "Missing identity", MentionCandidateCategory.AliasCandidate => "Alias candidates",
        MentionCandidateCategory.TitleCandidate => "Title candidates", MentionCandidateCategory.AddressingTerm => "Addressing terms", _ => "Noise / Uncertain"
    };
}
