using System.Text.Json;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record CorpusAuditReviewItem(string Group, string Title, string Reason,
    Guid ChapterId, int StartUtf16, IReadOnlyList<CorpusEvidence> Evidence);
public sealed record CorpusReviewAudit(CorpusIntegrityReport Integrity, CharacterCensusReport Census,
    IReadOnlyList<CorpusAuditReviewItem> Items)
{
    public bool Complete => Integrity.Complete && Census.MissingIdentities.Count == 0;
    public string Summary => (Complete ? Integrity.Summary : Integrity.Summary.Replace("Complete / Verified", "Analyzed / NeedsReview", StringComparison.Ordinal)) +
        $" · Missing identity {Census.MissingIdentities.Count}. CPU hypotheses are unverified; no auto-accept.";

    public Task SaveDiagnosticsAsync(ProjectSession session, CancellationToken cancellationToken = default)
    {
        if (session.Manifest.ProjectId != Integrity.ProjectId) throw new InvalidOperationException("Audit belongs to another project.");
        return SimpleFileStorage.WriteAsync(session.Resolve("Settings/abi3-corpus-audit.v1.json"),
            JsonSerializer.SerializeToUtf8Bytes(new Diagnostic(1, Integrity, Complete, Summary), DocumentJson.Options),
            bytes => JsonSerializer.Deserialize<Diagnostic>(bytes, DocumentJson.Options)
                ?? throw new InvalidDataException("Empty audit diagnostic."), cancellationToken);
    }
    private sealed record Diagnostic(int SchemaVersion, CorpusIntegrityReport Integrity, bool Complete, string Summary);

    public static CorpusReviewAudit Build(CorpusIntegrityReport integrity, CharacterCensusReport census, BookIntelligenceState state)
    {
        if (integrity.ProjectId != census.ProjectId || state.ProjectId != census.ProjectId ||
            integrity.StateRevision != state.Revision || census.SurveyRevision != state.Revision)
            throw new InvalidOperationException("Audit snapshots do not match current survey revision.");
        var items = new List<CorpusAuditReviewItem>();
        foreach (var missing in census.MissingIdentities)
            items.Add(new("Missing identity", $"{missing.ChineseName} · {missing.Frequency} lượt · chương {missing.FirstChapter}–{missing.LastChapter}",
                $"{missing.Kind}: ứng viên CPU chưa có hồ sơ/review. Không khẳng định danh tính, không tự gộp bí danh.",
                missing.Mentions[0].ChapterId, missing.Mentions[0].StartUtf16,
                missing.Mentions.Select(x => x.NavigationEvidence(missing.ChineseName)).ToArray()));
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
        return new(integrity, census, items);
    }
}
