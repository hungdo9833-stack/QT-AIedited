using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.Addressing;

public enum AddressingDecision { Accepted, Rejected, Deferred }

/// <summary>Append-only human decision. Applied describes exactly what was written to the Character Project.</summary>
public sealed record AddressingReview(Guid Id, Guid ProposalId, AddressingDecision Decision, DateTimeOffset At, string Note, string Applied, long ResourceRevision);

/// <summary>What the reviewer accepts for a character, possibly edited. Name forms must come from the proposal.</summary>
public sealed record CharacterAcceptance(CharacterGender Gender, string VietnameseName, string NarrativePronoun, string SecondaryPronoun,
    IReadOnlyList<AddressingNameForm> NameForms, int PhaseIndex, string Note = "");

/// <summary>What the reviewer accepts for a directed pair, possibly edited.</summary>
public sealed record PairAcceptance(string SelfReference, string AddresseeReference, string Relationship, int PhaseIndex, string Note = "");

/// <summary>
/// Addressing Ledger step 3: explicit human review. Only verified proposals can be accepted; acceptance writes the
/// Character Project through ProjectResourceStore (revision-checked) and never overwrites locked or conflicting data.
/// </summary>
public sealed class AddressingReviewService(ProjectSession session)
{
    private readonly AddressingLedgerStore _ledger = new(session);
    private readonly ProjectResourceStore _resources = new(session);

    public Task<AddressingLedgerState> LoadAsync(CancellationToken ct = default) => _ledger.LoadAsync(ct);

    public async Task<ProjectResources> AcceptCharacterAsync(Guid proposalId, CharacterAcceptance edit, long expectedResourceRevision, CancellationToken ct = default)
    {
        var proposal = await PendingVerified(proposalId, AddressingTaskKind.Character, ct).ConfigureAwait(false);
        string narrative = Term(edit.NarrativePronoun, "Đại từ kể chính"), secondary = edit.SecondaryPronoun.Trim(), vietnamese = edit.VietnameseName.Trim();
        if (vietnamese.Length is 0 or > 200) throw new InvalidOperationException("Cần tên tiếng Việt của nhân vật (tối đa 200 ký tự).");
        if (secondary.Length > 80) throw new InvalidOperationException("Đại từ kể phụ quá dài.");
        Vietnamese(vietnamese, "Tên tiếng Việt"); Vietnamese(secondary, "Đại từ kể phụ");
        foreach (var form in edit.NameForms) Vietnamese(form.Vi.Trim(), "Tên tiếng Việt của cách gọi " + form.Zh);
        var phase = Phase(proposal, edit.PhaseIndex);
        var allowed = proposal.NameForms.Select(f => f.Zh).ToHashSet(StringComparer.Ordinal);
        var forms = edit.NameForms.Select(f => f with { Zh = f.Zh.Trim(), Vi = f.Vi.Trim() }).ToArray();
        if (forms.Any(f => !allowed.Contains(f.Zh) || f.Vi.Length is 0 or > 200) || forms.Select(f => f.Zh).Distinct(StringComparer.Ordinal).Count() != forms.Length)
            throw new InvalidOperationException("Chỉ giữ lại cách gọi có trong đề xuất, mỗi cách gọi một lần và có tên tiếng Việt.");

        var resources = await Fresh(expectedResourceRevision, ct).ConfigureAwait(false);
        var profiles = resources.Characters.Profiles;
        var profile = Find(profiles, proposal.Subject);
        bool created = profile is null;
        profile ??= new CharacterProfile { ChineseName = proposal.Subject };
        foreach (var form in forms.Where(f => f.Zh != profile.ChineseName))
        {
            var owner = profiles.FirstOrDefault(p => p != profile && (p.ChineseName == form.Zh || p.Aliases.Contains(form.Zh, StringComparer.Ordinal)));
            if (owner is not null) throw new InvalidOperationException($"Cách gọi {form.Zh} đã thuộc hồ sơ {owner.VietnameseName}; không tự gộp hai nhân vật.");
        }
        foreach (var form in forms.Where(f => f.Zh != profile.ChineseName))
        {
            var term = resources.Terms.FirstOrDefault(t => t.Chinese == form.Zh);
            if (term is not null && term.Vietnamese != form.Vi) throw new InvalidOperationException($"Glossary đã dịch {form.Zh} là \"{term.Vietnamese}\"; sửa glossary trước nếu muốn đổi.");
        }
        var fullTerm = resources.Terms.FirstOrDefault(t => t.Chinese == profile.ChineseName);
        if (fullTerm is not null && fullTerm.Vietnamese != vietnamese) throw new InvalidOperationException($"Glossary đã dịch {profile.ChineseName} là \"{fullTerm.Vietnamese}\"; tên Việt phải khớp.");

        profile.VietnameseName = vietnamese; profile.Gender = edit.Gender;
        profile.PrimaryNarrativePronoun = narrative; profile.SecondaryNarrativePronoun = secondary;
        if (profile.DisallowedNarrativePronouns.Count == 0) { var defaults = new CharacterProfile { Gender = edit.Gender }; defaults.ApplyGenderDefaults(); profile.DisallowedNarrativePronouns = defaults.DisallowedNarrativePronouns; }
        profile.DisallowedNarrativePronouns.RemoveAll(x => string.Equals(x, narrative, StringComparison.OrdinalIgnoreCase) || string.Equals(x, secondary, StringComparison.OrdinalIgnoreCase));
        profile.NormalizationEnabled = profile.DisallowedNarrativePronouns.Count > 0;
        profile.NarrativePronounLocked = true;
        foreach (var form in forms.Where(f => f.Zh != profile.ChineseName && !profile.Aliases.Contains(f.Zh, StringComparer.Ordinal))) profile.Aliases.Add(form.Zh);
        foreach (var form in forms.Where(f => f.Zh != profile.ChineseName && resources.Terms.All(t => t.Chinese != f.Zh))) resources.Terms.Add(new(form.Zh, form.Vi));
        // Several phases become chapter-ranged narrative pronouns; the reviewed phase uses the edited values and is also the default.
        profile.NarrativePhases = proposal.Phases.Count < 2 ? [] : proposal.Phases.Select((p, i) => i == edit.PhaseIndex
            ? new NarrativePronounPhase(p.FromChapter, p.ToChapter == 0 ? null : p.ToChapter, narrative, secondary)
            : new NarrativePronounPhase(p.FromChapter, p.ToChapter == 0 ? null : p.ToChapter, Term(p.NarrativeVi, "Đại từ kể của giai đoạn"), Vietnamese(p.SecondaryVi.Trim(), "Đại từ kể phụ của giai đoạn"))).ToList();
        if (created) profiles.Add(profile);

        string applied = $"{(created ? "Tạo" : "Cập nhật")} hồ sơ {proposal.Subject}={vietnamese}; {edit.Gender}; kể: {narrative}" + (secondary.Length > 0 ? "/" + secondary : "") +
            $" (khóa, ch.{phase.FromChapter}–{(phase.ToChapter == 0 ? "…" : phase.ToChapter)}); bí danh: {string.Join(", ", forms.Where(f => f.Zh != profile.ChineseName).Select(f => f.Zh + "=" + f.Vi))}";
        return await SaveAndRecord(resources, proposal, applied, edit.Note, ct).ConfigureAwait(false);
    }

    public async Task<ProjectResources> AcceptPairAsync(Guid proposalId, PairAcceptance edit, long expectedResourceRevision, CancellationToken ct = default)
    {
        var proposal = await PendingVerified(proposalId, AddressingTaskKind.Pair, ct).ConfigureAwait(false);
        string self = Term(edit.SelfReference, "Tự xưng"), address = Term(edit.AddresseeReference, "Gọi người");
        string relationship = Vietnamese(edit.Relationship.Trim().Length > 0 ? edit.Relationship.Trim() : "Xưng hô", "Quan hệ");
        if (relationship.Length > 200) throw new InvalidOperationException("Nhãn quan hệ quá dài.");
        _ = Phase(proposal, edit.PhaseIndex); // Validates the reviewed phase index.
        var resources = await Fresh(expectedResourceRevision, ct).ConfigureAwait(false);
        var speaker = Find(resources.Characters.Profiles, proposal.Subject) ?? throw new InvalidOperationException($"Chưa có hồ sơ {proposal.Subject}. Hãy duyệt nhân vật này trước.");
        var listener = Find(resources.Characters.Profiles, proposal.Listener!) ?? throw new InvalidOperationException($"Chưa có hồ sơ {proposal.Listener}. Hãy duyệt nhân vật này trước.");
        if (speaker == listener) throw new InvalidOperationException("Người nói và người nghe là cùng một hồ sơ.");
        var existing = speaker.Relationships.Where(r => r.EffectiveListenerCharacterId == listener.Id && r.EffectiveSpeakerCharacterId(speaker.Id) == speaker.Id).ToArray();
        var locked = existing.FirstOrDefault(r => r.Locked && (r.SelfReference != self || r.AddresseeReference != address));
        if (locked is not null)
            throw new InvalidOperationException($"{speaker.VietnameseName} → {listener.VietnameseName} đã có quy tắc khóa ({locked.SelfReference}/{locked.AddresseeReference}); không ghi đè. Mở khóa trong hồ sơ nếu muốn thay.");
        speaker.Relationships.RemoveAll(r => existing.Contains(r));
        // One phase = a whole-book rule. Several phases = one chapter-ranged rule per phase (reviewed phase uses the edits).
        var rules = proposal.Phases.Select((p, i) => (p, i)).Where(x => proposal.Phases.Count > 1 || x.i == edit.PhaseIndex).Select(x =>
        {
            bool reviewed = x.i == edit.PhaseIndex;
            return new CharacterRelationship
            {
                TargetCharacterId = listener.Id, ListenerCharacterId = listener.Id, SpeakerCharacterId = speaker.Id, Relationship = relationship,
                SelfReference = reviewed ? self : Term(x.p.SelfVi, "Tự xưng của giai đoạn"), AddresseeReference = reviewed ? address : Term(x.p.AddressVi, "Gọi người của giai đoạn"),
                Enabled = true, Locked = true, AddressingNotes = "Sổ xưng hô (đã duyệt)",
                EffectiveFromChapter = proposal.Phases.Count > 1 ? x.p.FromChapter : null,
                EffectiveToChapter = proposal.Phases.Count > 1 && x.p.ToChapter != 0 ? x.p.ToChapter : null
            };
        }).ToArray();
        speaker.Relationships.AddRange(rules);
        string applied = $"{proposal.Subject} → {proposal.Listener}: " + string.Join("; ", rules.Select(r =>
            (r.EffectiveFromChapter is null ? "" : $"ch.{r.EffectiveFromChapter}–{(r.EffectiveToChapter?.ToString() ?? "…")} ") + $"xưng {r.SelfReference} / gọi {r.AddresseeReference}")) + $" ({relationship}, khóa)";
        return await SaveAndRecord(resources, proposal, applied, edit.Note, ct).ConfigureAwait(false);
    }

    public Task<AddressingLedgerState> RejectAsync(Guid proposalId, string note = "", CancellationToken ct = default) => Decide(proposalId, AddressingDecision.Rejected, note, ct);
    public Task<AddressingLedgerState> DeferAsync(Guid proposalId, string note = "", CancellationToken ct = default) => Decide(proposalId, AddressingDecision.Deferred, note, ct);

    /// <summary>Explicit switch; accepting proposals never turns the translation lock on by itself.</summary>
    public async Task<ProjectResources> SetConsistencyLockAsync(bool enabled, long expectedResourceRevision, CancellationToken ct = default)
    {
        var resources = await Fresh(expectedResourceRevision, ct).ConfigureAwait(false);
        resources.Characters.ConsistencyLockEnabled = enabled;
        return await _resources.SaveAsync(resources, ct).ConfigureAwait(false);
    }

    private async Task<AddressingLedgerState> Decide(Guid proposalId, AddressingDecision decision, string note, CancellationToken ct)
    {
        var state = await _ledger.LoadAsync(ct).ConfigureAwait(false);
        if (state.Proposals.All(p => p.Id != proposalId)) throw new InvalidOperationException("Không tìm thấy đề xuất.");
        if (state.LatestReview(proposalId)?.Decision == AddressingDecision.Accepted) throw new InvalidOperationException("Đề xuất đã được ghi vào hồ sơ; sửa trực tiếp trong hồ sơ nhân vật.");
        long revision = (await _resources.LoadAsync(ct).ConfigureAwait(false)).Revision;
        return await _ledger.UpdateAsync(s => s.Reviews.Add(new(Guid.NewGuid(), proposalId, decision, DateTimeOffset.UtcNow, Clip(note), "", revision)), ct).ConfigureAwait(false);
    }

    private async Task<AddressingProposal> PendingVerified(Guid proposalId, AddressingTaskKind kind, CancellationToken ct)
    {
        var state = await _ledger.LoadAsync(ct).ConfigureAwait(false);
        var proposal = state.Proposals.FirstOrDefault(p => p.Id == proposalId) ?? throw new InvalidOperationException("Không tìm thấy đề xuất.");
        if (proposal.Kind != kind) throw new InvalidOperationException("Sai loại đề xuất.");
        if (!proposal.EvidenceValid) throw new InvalidOperationException("Đề xuất chưa qua kiểm chứng (" + string.Join(", ", proposal.Issues) + "); chỉ có thể bác bỏ hoặc để sau.");
        if (state.LatestReview(proposalId)?.Decision == AddressingDecision.Accepted) throw new InvalidOperationException("Đề xuất này đã được ghi vào hồ sơ.");
        return proposal;
    }

    private async Task<ProjectResources> Fresh(long expectedRevision, CancellationToken ct)
    {
        var resources = await _resources.LoadAsync(ct).ConfigureAwait(false);
        if (resources.Revision != expectedRevision) throw new InvalidOperationException("Hồ sơ nhân vật đã đổi sau khi nạp. Hãy nạp lại trước khi duyệt.");
        return resources;
    }

    private async Task<ProjectResources> SaveAndRecord(ProjectResources resources, AddressingProposal proposal, string applied, string note, CancellationToken ct)
    {
        ProjectResources saved;
        try { saved = await _resources.SaveAsync(resources, ct).ConfigureAwait(false); }
        catch (ArgumentException ex) { throw new InvalidOperationException("Hồ sơ sau khi áp dụng không hợp lệ: " + ex.Message, ex); }
        await _ledger.UpdateAsync(s => s.Reviews.Add(new(Guid.NewGuid(), proposal.Id, AddressingDecision.Accepted, DateTimeOffset.UtcNow, Clip(note), applied, saved.Revision)), CancellationToken.None).ConfigureAwait(false);
        return saved;
    }

    private static CharacterProfile? Find(IEnumerable<CharacterProfile> profiles, string name) =>
        profiles.FirstOrDefault(p => p.ChineseName == name) ?? profiles.FirstOrDefault(p => p.Aliases.Contains(name, StringComparer.Ordinal));
    private static AddressingPhase Phase(AddressingProposal proposal, int index) =>
        index >= 0 && index < proposal.Phases.Count ? proposal.Phases[index] : throw new InvalidOperationException("Giai đoạn không hợp lệ.");
    private static string Term(string value, string label)
    {
        string trimmed = value.Trim();
        if (trimmed.Length is 0 or > 80) throw new InvalidOperationException(label + " phải có từ 1 đến 80 ký tự.");
        return Vietnamese(trimmed, label);
    }
    // Whatever is written to the Character Project goes into translations: a Han character there is never valid (also guards proposals verified before this rule).
    private static string Vietnamese(string value, string label)
    {
        if (AddressingAiSurvey.HasHan(value)) throw new InvalidOperationException(label + " phải viết bằng tiếng Việt, không chứa chữ Hán: \"" + value + "\".");
        return value;
    }
    private static string Clip(string note) => note.Length <= 500 ? note : note[..500];
}
