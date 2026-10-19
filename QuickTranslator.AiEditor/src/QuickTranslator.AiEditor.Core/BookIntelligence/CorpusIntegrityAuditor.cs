using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public enum CorpusCompletion { Incomplete, NeedsReindex, Indexed, Analyzed, Blocked, Skipped, Stale, Verified, Complete }
public sealed record CorpusIntegrityIssue(string Code, int StartUtf16, int LengthUtf16, string Reason);
public sealed record ChapterIntegrityAudit(Guid ChapterId, int ChapterNumber, bool Active,
    string SourceSha256, int SourceLengthUtf16, IReadOnlyList<CorpusIntegrityIssue> Issues,
    int Current, int Analyzed, int Blocked, int Skipped, int Unverified, int Stale)
{
    public bool Indexed => Active && Issues.Count == 0;
    public CorpusCompletion Status => !Active ? CorpusCompletion.Stale : Issues.Count > 0
        ? CorpusCompletion.NeedsReindex : Blocked > 0 ? CorpusCompletion.Blocked
        : Skipped > 0 ? CorpusCompletion.Skipped : Unverified > 0 ? CorpusCompletion.Indexed : CorpusCompletion.Analyzed;
}
public sealed record CorpusIntegrityReport(Guid ProjectId, long StateRevision,
    IReadOnlyList<ChapterIntegrityAudit> Chapters, int PendingReviews)
{
    public int ActiveChapters => Chapters.Count(x => x.Active);
    public int IndexedChapters => Chapters.Count(x => x.Indexed);
    public int Current => Chapters.Where(x => x.Active).Sum(x => x.Current);
    public int Analyzed => Chapters.Where(x => x.Active).Sum(x => x.Analyzed);
    public int Blocked => Chapters.Where(x => x.Active).Sum(x => x.Blocked);
    public int Skipped => Chapters.Where(x => x.Active).Sum(x => x.Skipped);
    public int Unverified => Chapters.Where(x => x.Active).Sum(x => x.Unverified);
    public int Missing => Chapters.Where(x => x.Active).Sum(x => x.Issues.Count(i => i.Code is "Gap" or "MissingIndex" or "Unreadable"));
    public int Stale => Chapters.Sum(x => x.Stale);
    public bool NeedsReindex => Chapters.Any(x => x.Active && !x.Indexed || !x.Active && x.Current > 0);
    public bool Verified => ActiveChapters > 0 && !NeedsReindex && Blocked == 0 && Skipped == 0 && Unverified == 0 && PendingReviews == 0;
    public bool Complete => Verified;
    public CorpusCompletion Status => NeedsReindex ? CorpusCompletion.NeedsReindex
        : ActiveChapters == 0 ? CorpusCompletion.Incomplete : Blocked > 0 ? CorpusCompletion.Blocked
        : Skipped > 0 ? CorpusCompletion.Skipped : Unverified > 0 ? CorpusCompletion.Indexed
        : Verified ? CorpusCompletion.Complete : CorpusCompletion.Analyzed;
    public string Summary => $"{Status}{(Verified ? " / Verified" : " / NeedsReview")} · active {IndexedChapters}/{ActiveChapters} chương · " +
        $"current {Current} · analyzed {Analyzed} · blocked {Blocked} · missing {Missing} · stale {Stale} · skipped {Skipped} · unverified {Unverified}";
}

/// <summary>Reads the manifest and source, never infers active chapters or completion from a survey JSON alone.</summary>
public sealed class CorpusIntegrityAuditor(ProjectRepository repository)
{
    public async Task<CorpusIntegrityReport> AuditAsync(ProjectSession session, BookIntelligenceState state,
        CancellationToken cancellationToken = default)
    {
        if (state.ProjectId != session.Manifest.ProjectId) throw new InvalidOperationException("Corpus belongs to another project.");
        var result = new List<ChapterIntegrityAudit>();
        var groups = session.Manifest.Chapters.Where(x => x.DeletedAt is null).GroupBy(x => x.ChapterId).ToArray();
        foreach (var group in groups.OrderBy(x => x.First().Number).ThenBy(x => x.Key))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = group.First();
            try
            {
                var chapter = (await repository.LoadChapterAsync(session, entry.ChapterId, cancellationToken).ConfigureAwait(false)).Chapter;
                var index = state.Chapters.Where(x => x.ChapterId == entry.ChapterId).ToArray();
                var audit = Inspect(entry.ChapterId, entry.Number, chapter.OriginalText ?? "", index.FirstOrDefault(), state.ScanMode);
                var issues = audit.Issues.ToList();
                if (group.Count() != 1 || index.Length > 1) issues.Add(new("DuplicateChapter", 0, 0, "Duplicate ChapterId in manifest/index."));
                if (chapter.Id != entry.ChapterId || chapter.ProjectId != state.ProjectId)
                    issues.Add(new("ChapterIdentity", 0, 0, "ChapterDocument identity does not match manifest."));
                result.Add(audit with { Issues = issues });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException)
            {
                result.Add(new(entry.ChapterId, entry.Number, true, "", 0,
                    [new("Unreadable", 0, 0, $"Cannot validate ChapterDocument ({ex.GetType().Name}); reindex pending.")], 0, 0, 0, 0, 0, 0));
            }
        }
        var activeIds = groups.Select(x => x.Key).ToHashSet();
        foreach (var index in state.Chapters.Where(x => !activeIds.Contains(x.ChapterId)))
            result.Add(new(index.ChapterId, index.ChapterNumber, false, index.SourceSha256, index.SourceLengthUtf16,
                [new("InactiveChapter", 0, 0, session.Manifest.Chapters.Any(x => x.ChapterId == index.ChapterId)
                    ? "Chapter deleted in manifest; retained for audit only." : "Chapter absent from manifest; retained for audit only.")],
                index.Evidence.Count(x => x.IsCurrent), 0, 0, 0, 0, index.Evidence.Count(x => !x.IsCurrent)));
        return new(state.ProjectId, state.Revision, result, state.PendingCount);
    }

    public static ChapterIntegrityAudit Inspect(Guid id, int number, string source, CorpusChapterIndex? index,
        IntelligenceScanMode mode = IntelligenceScanMode.FullSemantic)
    {
        var issues = new List<CorpusIntegrityIssue>();
        string hash = SourceHash(source);
        if (index is null) return new(id, number, true, hash, source.Length,
            [new("MissingIndex", 0, source.Length, "Active chapter has no index; indexing interrupted or pending.")], 0, 0, 0, 0, 0, 0);
        if (index.ChapterId != id) issues.Add(new("ChapterIdentity", 0, 0, "Index belongs to another chapter."));
        if (index.SourceSha256 != hash) issues.Add(new("SourceChangedOrHashMismatch", 0, source.Length,
            "OriginalText hash differs from index; source changed or saved hash is invalid (cannot distinguish from state alone)."));
        if (index.SourceLengthUtf16 != source.Length) issues.Add(new("SourceLength", 0, source.Length, "Source length differs from index."));
        int cursor = 0;
        var current = index.Evidence.Where(x => x.IsCurrent).OrderBy(x => x.StartUtf16).ToArray();
        foreach (var evidence in current)
        {
            if (evidence.ChapterId != id) issues.Add(new("EvidenceIdentity", evidence.StartUtf16, evidence.LengthUtf16, "Evidence belongs to another chapter."));
            if (evidence.SourceSha256 != hash) issues.Add(new("EvidenceHash", evidence.StartUtf16, evidence.LengthUtf16, "Evidence is not fresh for OriginalText."));
            long end = (long)evidence.StartUtf16 + evidence.LengthUtf16;
            if (evidence.StartUtf16 < 0 || evidence.LengthUtf16 <= 0 || end > source.Length)
            { issues.Add(new("Range", evidence.StartUtf16, evidence.LengthUtf16, "Invalid UTF-16 range.")); continue; }
            if (evidence.StartUtf16 > cursor) issues.Add(new("Gap", cursor, evidence.StartUtf16 - cursor, "Missing current coverage; reconciliation required even when hash is unchanged."));
            if (evidence.StartUtf16 < cursor) issues.Add(new("Overlap", evidence.StartUtf16, cursor - evidence.StartUtf16, "Overlapping current evidence."));
            if (SplitsSurrogate(source, evidence.StartUtf16) || SplitsSurrogate(source, (int)end))
                issues.Add(new("SurrogateBoundary", evidence.StartUtf16, evidence.LengthUtf16, "Window splits a non-BMP Unicode character."));
            if (!source.AsSpan(evidence.StartUtf16, evidence.LengthUtf16).SequenceEqual(evidence.Excerpt))
                issues.Add(new("ExcerptMismatch", evidence.StartUtf16, evidence.LengthUtf16, "Excerpt does not equal OriginalText at this position."));
            if (evidence.AiBlocked && evidence.AiAnalyzed)
                issues.Add(new("BlockedAnalyzed", evidence.StartUtf16, evidence.LengthUtf16, "Provider block is not successful analysis."));
            cursor = Math.Max(cursor, (int)end);
        }
        if (cursor < source.Length) issues.Add(new("Gap", cursor, source.Length - cursor, "Missing current source tail; reindex pending/interrupted."));
        int blocked = current.Count(x => x.AiBlocked);
        int analyzed = current.Count(x => x.AiAnalyzed && !x.AiBlocked);
        int skipped = current.Count(x => !x.AiAnalyzed && !x.AiBlocked && !Eligible(x, mode));
        return new(id, number, true, hash, source.Length, issues, current.Length, analyzed, blocked, skipped,
            current.Length - analyzed - blocked - skipped, index.Evidence.Count(x => !x.IsCurrent));
    }

    public static bool Eligible(CorpusEvidence evidence, IntelligenceScanMode mode) => mode == IntelligenceScanMode.FullSemantic ||
        evidence.Priority is EvidencePriority.High or EvidencePriority.Medium || evidence.IsAuditSample || evidence.AuditExpanded;
    public static string SourceHash(string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    private static bool SplitsSurrogate(string source, int position) => position > 0 && position < source.Length &&
        char.IsHighSurrogate(source[position - 1]) && char.IsLowSurrogate(source[position]);
}
