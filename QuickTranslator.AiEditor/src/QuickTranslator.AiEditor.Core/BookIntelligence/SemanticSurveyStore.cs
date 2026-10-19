using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record SemanticBatch(Guid Id, Guid ProjectId, Guid EvidenceId, string BackendSignature, ModelOptions Options,
    string ContextFingerprint, string DependencyFingerprint, IReadOnlyList<Guid> ContextFactIds, IReadOnlyList<SemanticKnownIdentity> AllowedIdentities,
    IReadOnlyList<SemanticCpuHint> CpuCandidates, string PromptVersion, string SchemaVersion);
public sealed record SemanticAttempt(Guid Id, Guid BatchId, Guid? ReservationId, SemanticBatchStatus Status, DateTimeOffset At,
    int ReservedTokens, int? InputTokens, int? OutputTokens, string ErrorCode, string ResponseFingerprint);
public sealed record SemanticProposal(Guid Id, Guid BatchId, SemanticAuditedFinding Audit);
public sealed record SemanticReview(Guid Id, Guid ProposalId, MemoryReviewStatus Status, DateTimeOffset At, string Note,
    MemoryTimeline Timeline, Guid? MemoryItemId = null, Guid? MemoryDecisionId = null, Guid? RevertsId = null);
public sealed class SemanticSurveyState
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public Guid ProjectId { get; set; }
    [JsonRequired] public long Revision { get; set; }
    [JsonRequired] public List<MemoryEvidence> Evidence { get; set; } = [];
    [JsonRequired] public List<SemanticBatch> Batches { get; set; } = [];
    [JsonRequired] public List<SemanticAttempt> Attempts { get; set; } = [];
    [JsonRequired] public List<SemanticProposal> Proposals { get; set; } = [];
    [JsonRequired] public List<SemanticReview> Reviews { get; set; } = [];
    public SemanticBatchStatus Status(Guid batch) => Attempts.LastOrDefault(x => x.BatchId == batch)?.Status ?? SemanticBatchStatus.Pending;
    public void Validate()
    {
        void Require([DoesNotReturnIf(false)] bool value) { if (!value) throw new InvalidDataException("Invalid semantic survey sidecar."); }
        bool Hash(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
        Require(SchemaVersion == 1 && ProjectId != Guid.Empty && Revision >= 0 && Evidence is not null && Batches is not null && Attempts is not null && Proposals is not null && Reviews is not null);
        Require(Evidence!.All(x => x is not null && x.Id != Guid.Empty && x.ProjectId == ProjectId && x.ChapterId != Guid.Empty && x.ChapterNumber > 0 && Hash(x.SourceSha256) && x.StartUtf16 >= 0 && x.LengthUtf16 > 0));
        var proof = Evidence.ToDictionary(x => x.Id); var batches = Batches!.ToDictionary(x => x.Id);
        foreach (var batch in Batches) { Require(batch.ProjectId == ProjectId && proof.ContainsKey(batch.EvidenceId) && batch.BackendSignature.Length is > 0 and <= 200 &&
            Hash(batch.ContextFingerprint) && Hash(batch.DependencyFingerprint) && batch.ContextFactIds is not null && batch.ContextFactIds.Count <= 16 &&
            batch.AllowedIdentities is not null && batch.AllowedIdentities.Count <= 8 && batch.CpuCandidates is not null && batch.CpuCandidates.Count <= 16 && batch.PromptVersion.Length > 0 && batch.SchemaVersion.Length > 0); batch.Options.Validate(); }
        var reserved = new HashSet<Guid>(); var completed = new HashSet<Guid>();
        var attemptsById = Attempts!.ToDictionary(x => x.Id);
        foreach (var attempt in Attempts!)
        {
            Require(batches.ContainsKey(attempt.BatchId) && Enum.IsDefined(attempt.Status) && attempt.At != default && attempt.ReservedTokens >= 0 &&
                attempt.InputTokens is null or >= 0 && attempt.OutputTokens is null or >= 0 && attempt.ErrorCode.Length <= 240 && (attempt.ResponseFingerprint.Length == 0 || Hash(attempt.ResponseFingerprint)));
            if (attempt.ReservationId is Guid reservation) Require(reserved.Contains(reservation) && completed.Add(reservation) && attemptsById[reservation].BatchId == attempt.BatchId && attempt.Status != SemanticBatchStatus.Pending);
            else { Require(attempt.Status == SemanticBatchStatus.Pending); reserved.Add(attempt.Id); }
        }
        foreach (var proposal in Proposals!)
        {
            Require(batches.ContainsKey(proposal.BatchId) && proposal.Audit is not null && proposal.Audit.Finding is not null && proposal.Audit.Issues is not null);
            var f = proposal.Audit.Finding; Require(f.SupportSpans.All(x => x.Text == "") && f.SourceEvidenceIds.Count <= 8 && f.SupportSpans.Count <= 8 && f.Reason.Length <= 1000);
        }
        foreach (var review in Reviews!) Require(Proposals.Any(x => x.Id == review.ProposalId) && review.At != default && review.Note.Length is > 0 and <= 2000 &&
            review.Status is MemoryReviewStatus.Proposed or MemoryReviewStatus.Accepted or MemoryReviewStatus.Confirmed or MemoryReviewStatus.Rejected &&
            (review.Status != MemoryReviewStatus.Confirmed || review.MemoryItemId is not null && review.MemoryDecisionId is not null) &&
            (review.RevertsId is null || Reviews.TakeWhile(x => x.Id != review.Id).Any(x => x.Id == review.RevertsId && x.ProposalId == review.ProposalId)));
        var ids = Evidence.Select(x => x.Id).Concat(Batches.Select(x => x.Id)).Concat(Attempts.Select(x => x.Id)).Concat(Proposals.Select(x => x.Id)).Concat(Reviews.Select(x => x.Id)).ToArray();
        Require(ids.All(x => x != Guid.Empty) && ids.Distinct().Count() == ids.Length);
    }
}

/// <summary>Independent append-only staging journal; no Character registry, source excerpts or keys.</summary>
public sealed class SemanticSurveyStore(ProjectSession session)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    public string PathName => session.Resolve("Settings/abi3-semantic-survey.v1.json");
    public Task<SemanticSurveyState> LoadAsync(CancellationToken ct = default) => SimpleFileStorage.ReadOptionalAsync(PathName, Decode, () => new() { ProjectId = session.Manifest.ProjectId }, ct);
    public IDisposable AcquireRunLease()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        return new FileStream(PathName + ".run.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public Task<SemanticSurveyState> UpdateAsync(long revision, Action<SemanticSurveyState> update, CancellationToken ct = default) =>
        UpdateAsync(revision, s => { update(s); return Task.CompletedTask; }, ct);
    public async Task<SemanticSurveyState> UpdateAsync(long revision, Func<SemanticSurveyState, Task> update, CancellationToken ct = default)
    {
        var gate = Gates.GetOrAdd(PathName, _ => new(1, 1)); await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
            using var lease = new FileStream(PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var before = await LoadAsync(ct).ConfigureAwait(false); if (before.Revision != revision) throw new InvalidOperationException("Semantic revision changed; reload.");
            var after = JsonSerializer.Deserialize<SemanticSurveyState>(Serialize(before), DocumentJson.Options)!;
            await update(after).ConfigureAwait(false); after.Validate();
            if (after.ProjectId != before.ProjectId || after.Revision != revision) throw new InvalidOperationException("Cannot replace semantic identity/revision.");
            Preserve(before.Evidence, after.Evidence); Preserve(before.Batches, after.Batches); Preserve(before.Attempts, after.Attempts); Preserve(before.Proposals, after.Proposals); Preserve(before.Reviews, after.Reviews);
            byte[] oldBytes = Serialize(before), bytes = Serialize(after); if (oldBytes.AsSpan().SequenceEqual(bytes)) return after;
            after.Revision++; await SimpleFileStorage.WriteAsync(PathName, Serialize(after), Decode, ct).ConfigureAwait(false); return after;
        }
        finally { gate.Release(); }
    }
    private static void Preserve<T>(IReadOnlyList<T> old, IReadOnlyList<T> next)
    {
        if (next.Count < old.Count || old.Where((x, i) => EntityMemoryIdentity.Fingerprint(x) != EntityMemoryIdentity.Fingerprint(next[i])).Any()) throw new InvalidOperationException("Semantic history is append-only.");
    }
    public static byte[] Serialize(SemanticSurveyState state)
    {
        var buffer = new ArrayBufferWriter<byte>(); using var writer = new Utf8JsonWriter(buffer); JsonSerializer.Serialize(writer, state, DocumentJson.Options); writer.Flush(); return buffer.WrittenSpan.ToArray();
    }
    private SemanticSurveyState Decode(byte[] bytes)
    {
        var result = JsonSerializer.Deserialize<SemanticSurveyState>(bytes, DocumentJson.Options) ?? throw new InvalidDataException("Empty semantic file.");
        result.Validate(); if (result.ProjectId != session.Manifest.ProjectId) throw new InvalidDataException("Semantic file belongs to another Project."); return result;
    }
}
