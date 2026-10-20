using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.Addressing;

/// <summary>Reverted: a "Hoàn thành" was undone; the proposal is pending again.</summary>
public enum AddressingDecision { Accepted, Rejected, Deferred, Reverted }

/// <summary>Append-only decision (human or "Hoàn thành"). Applied describes exactly what was written to the Character Project.</summary>
public sealed record AddressingReview(Guid Id, Guid ProposalId, AddressingDecision Decision, DateTimeOffset At, string Note, string Applied, long ResourceRevision);

/// <summary>What the reviewer accepts for a character, possibly edited. Name forms must come from the proposal.</summary>
public sealed record CharacterAcceptance(CharacterGender Gender, string VietnameseName, string NarrativePronoun, string SecondaryPronoun,
    IReadOnlyList<AddressingNameForm> NameForms, int PhaseIndex, string Note = "");

/// <summary>What the reviewer accepts for a directed pair, possibly edited.</summary>
public sealed record PairAcceptance(string SelfReference, string AddresseeReference, string Relationship, int PhaseIndex, string Note = "");

/// <summary>One line of a "Hoàn thành" result: what was written, or why it was held back.</summary>
public sealed record AddressingAutoItem(Guid ProposalId, AddressingTaskKind Kind, string Subject, string? Listener, string Detail);

/// <summary>Result of <see cref="AddressingReviewService.CompleteAsync"/> (or its preview): nothing in Held blocks completion.</summary>
public sealed record AddressingCompletion(IReadOnlyList<AddressingAutoItem> Applied, IReadOnlyList<AddressingAutoItem> Held, ProjectResources Resources, Guid? ApplyId);

/// <summary>The Character Project changed after "Hoàn thành": undoing would also discard those edits.</summary>
public sealed class AddressingUndoConflictException(string message) : InvalidOperationException(message);

/// <summary>
/// Addressing Ledger step 3. Two ways to write verified proposals into the Character Project, sharing one set of rules:
/// one by one (the review window) or all at once ("Hoàn thành", the default for automatic translation). Writes go through
/// ProjectResourceStore (revision-checked); locked or conflicting data is never overwritten; "Hoàn thành" is undoable.
/// </summary>
public sealed class AddressingReviewService(ProjectSession session)
{
    private readonly AddressingLedgerStore _ledger = new(session);
    private readonly ProjectResourceStore _resources = new(session);
    private readonly AddressingApplyStore _applies = new(session);
    private const string AutoNote = "Tự động · Hoàn thành";

    public Task<AddressingLedgerState> LoadAsync(CancellationToken ct = default) => _ledger.LoadAsync(ct);

    public async Task<ProjectResources> AcceptCharacterAsync(Guid proposalId, CharacterAcceptance edit, long expectedResourceRevision, CancellationToken ct = default)
    {
        var proposal = await PendingVerified(proposalId, AddressingTaskKind.Character, ct).ConfigureAwait(false);
        var resources = await Fresh(expectedResourceRevision, ct).ConfigureAwait(false);
        string applied = ApplyCharacter(resources, proposal, edit, automatic: false);
        return await SaveAndRecord(resources, proposal, applied, edit.Note, ct).ConfigureAwait(false);
    }

    public async Task<ProjectResources> AcceptPairAsync(Guid proposalId, PairAcceptance edit, long expectedResourceRevision, CancellationToken ct = default)
    {
        var proposal = await PendingVerified(proposalId, AddressingTaskKind.Pair, ct).ConfigureAwait(false);
        var resources = await Fresh(expectedResourceRevision, ct).ConfigureAwait(false);
        string applied = ApplyPair(resources, proposal, edit, automatic: false);
        return await SaveAndRecord(resources, proposal, applied, edit.Note, ct).ConfigureAwait(false);
    }

    // ---------- "Hoàn thành": apply everything verified, hold back only what cannot be decided safely ----------

    /// <summary>What "Hoàn thành" would write now, without writing anything.</summary>
    public async Task<AddressingCompletion> PreviewCompletionAsync(CancellationToken ct = default)
    {
        var state = await _ledger.LoadAsync(ct).ConfigureAwait(false);
        var resources = await _resources.LoadAsync(ct).ConfigureAwait(false);
        var (applied, held, result) = Complete(state, resources);
        return new(applied.Select(x => x.Item).ToArray(), held, result, null);
    }

    /// <summary>
    /// Writes every verified, undecided proposal in one revision-checked save and switches the consistency lock on, so the
    /// translation uses the result. The previous Character Project is kept for <see cref="UndoCompletionAsync"/>.
    /// </summary>
    public async Task<AddressingCompletion> CompleteAsync(long expectedResourceRevision, CancellationToken ct = default)
    {
        var state = await _ledger.LoadAsync(ct).ConfigureAwait(false);
        var before = await Fresh(expectedResourceRevision, ct).ConfigureAwait(false);
        var (applied, held, result) = Complete(state, before);
        if (applied.Count == 0) return new([], held, before, null);
        ProjectResources saved;
        try { saved = await _resources.SaveAsync(result, ct).ConfigureAwait(false); }
        catch (ArgumentException ex) { throw new InvalidOperationException("Hồ sơ sau khi áp dụng không hợp lệ: " + ex.Message, ex); }
        var record = new AddressingApplyRecord(Guid.NewGuid(), DateTimeOffset.UtcNow, before.Revision, saved.Revision, applied.Select(x => x.Item.ProposalId).ToArray(), before);
        await _applies.AppendAsync(record, ct).ConfigureAwait(false);
        await _ledger.UpdateAsync(s =>
        {
            foreach (var (item, text) in applied) s.Reviews.Add(new(Guid.NewGuid(), item.ProposalId, AddressingDecision.Accepted, DateTimeOffset.UtcNow, AutoNote, text, saved.Revision));
        }, CancellationToken.None).ConfigureAwait(false);
        return new(applied.Select(x => x.Item).ToArray(), held, saved, record.Id);
    }

    /// <summary>
    /// Restores the Character Project as it was before the latest "Hoàn thành" and makes its proposals pending again. If the
    /// project was edited since, this throws <see cref="AddressingUndoConflictException"/> unless <paramref name="discardLaterEdits"/>.
    /// </summary>
    public async Task<ProjectResources> UndoCompletionAsync(bool discardLaterEdits = false, CancellationToken ct = default)
    {
        var record = (await _applies.LoadAsync(ct).ConfigureAwait(false)).Records.LastOrDefault(r => r.UndoneAt is null && r.Before is not null)
            ?? throw new InvalidOperationException("Không có lần Hoàn thành nào để hoàn tác.");
        var current = await _resources.LoadAsync(ct).ConfigureAwait(false);
        if (current.Revision != record.AfterRevision && !discardLaterEdits)
            throw new AddressingUndoConflictException("Hồ sơ nhân vật đã được sửa sau khi Hoàn thành. Hoàn tác sẽ bỏ cả những sửa đổi đó.");
        var restored = record.Before!.Clone(); restored.Revision = current.Revision;
        var saved = await _resources.SaveAsync(restored, ct).ConfigureAwait(false);
        await _applies.MarkUndoneAsync(record.Id, ct).ConfigureAwait(false);
        await _ledger.UpdateAsync(s =>
        {
            foreach (var id in record.ProposalIds) s.Reviews.Add(new(Guid.NewGuid(), id, AddressingDecision.Reverted, DateTimeOffset.UtcNow, "Hoàn tác Hoàn thành", "", saved.Revision));
        }, CancellationToken.None).ConfigureAwait(false);
        return saved;
    }

    /// <summary>True when a "Hoàn thành" can be undone.</summary>
    public async Task<bool> CanUndoCompletionAsync(CancellationToken ct = default) =>
        (await _applies.LoadAsync(ct).ConfigureAwait(false)).Records.Any(r => r.UndoneAt is null && r.Before is not null);

    private static (List<(AddressingAutoItem Item, string Text)> Applied, List<AddressingAutoItem> Held, ProjectResources Result) Complete(
        AddressingLedgerState state, ProjectResources before)
    {
        var resources = before.Clone();
        var applied = new List<(AddressingAutoItem, string)>(); var held = new List<AddressingAutoItem>();
        // One answer per character and per directed pair: the latest. A newer answer supersedes an older one even when it
        // is invalid or says NotThisPair (it was asked with better evidence), so nothing stale is applied.
        var latest = state.Proposals.Where(p => p.Kind != AddressingTaskKind.Scene)
            .GroupBy(p => (p.Kind, p.Subject, p.Listener)).Select(g => (Latest: g.Last(), All: g.ToArray())).ToArray();
        bool Decided(AddressingProposal[] all, out string why)
        {
            var decisions = all.Select(p => state.LatestReview(p.Id)?.Decision).ToArray();
            why = decisions.Contains(AddressingDecision.Accepted) ? "đã ghi vào hồ sơ trước đó" : decisions.Contains(AddressingDecision.Rejected) ? "bạn đã bác bỏ" : "";
            return why.Length > 0;
        }
        AddressingAutoItem Item(AddressingProposal p, string detail) => new(p.Id, p.Kind, p.Subject, p.Listener, detail);
        var characters = latest.Where(x => x.Latest.Kind == AddressingTaskKind.Character).ToArray();
        // A name form claimed by two characters (宁小姐: either sister) is never given to one of them.
        var shared = characters.Where(x => x.Latest.EvidenceValid).SelectMany(x => x.Latest.NameForms.Select(f => f.Zh).Where(z => z != x.Latest.Subject).Distinct())
            .GroupBy(z => z, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var (p, all) in characters.Concat(latest.Where(x => x.Latest.Kind == AddressingTaskKind.Pair)))
        {
            if (Decided(all, out string why)) { if (why != "đã ghi vào hồ sơ trước đó") held.Add(Item(p, why)); continue; }
            if (!p.EvidenceValid)
            {
                held.Add(Item(p, p.Status is "Ambiguous" or "NotThisPair" or "NotACharacter" ? "AI: " + p.Status : "chưa qua kiểm chứng (" + string.Join(", ", p.Issues) + ")"));
                continue;
            }
            var trial = resources.Clone();
            try
            {
                string text;
                if (p.Kind == AddressingTaskKind.Character)
                {
                    var (edit, dropped) = AutoCharacter(trial, p, shared);
                    text = ApplyCharacter(trial, p, edit, automatic: true);
                    held.AddRange(dropped.Select(d => Item(p, d)));
                }
                else
                {
                    var phase = p.Phases[0];
                    text = ApplyPair(trial, p, new(phase.SelfVi, phase.AddressVi, p.Relationship, 0), automatic: true);
                }
                trial.Validate();
                resources = trial; applied.Add((Item(p, text), text));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or InvalidDataException) { held.Add(Item(p, ex.Message)); }
        }
        // "Hoàn thành" means: translate with these rules. The lock is part of what undo restores.
        if (applied.Count > 0) resources.Characters.ConsistencyLockEnabled = true;
        return (applied, held, resources);
    }

    /// <summary>
    /// The acceptance "Hoàn thành" uses for a character: the first phase, the AI's Vietnamese name unless the glossary already
    /// has one, and only name forms that are unambiguous and not owned by another profile (dropped ones are reported).
    /// </summary>
    private static (CharacterAcceptance Edit, List<string> Dropped) AutoCharacter(ProjectResources resources, AddressingProposal p, IReadOnlySet<string> shared)
    {
        var dropped = new List<string>();
        var profile = Find(resources.Characters.Profiles, p.Subject);
        string name = resources.Terms.FirstOrDefault(t => t.Chinese == (profile?.ChineseName ?? p.Subject))?.Vietnamese
            ?? p.NameForms.FirstOrDefault(f => f.Zh == p.Subject)?.Vi.Trim() ?? profile?.VietnameseName ?? "";
        if (name.Length == 0) throw new InvalidOperationException("thiếu tên tiếng Việt");
        var forms = new List<AddressingNameForm>();
        foreach (var form in p.NameForms.Where(f => f.Zh != p.Subject))
        {
            var owner = resources.Characters.Profiles.FirstOrDefault(x => x != profile && (x.ChineseName == form.Zh || x.Aliases.Contains(form.Zh, StringComparer.Ordinal)));
            var term = resources.Terms.FirstOrDefault(t => t.Chinese == form.Zh);
            if (shared.Contains(form.Zh)) dropped.Add($"cách gọi {form.Zh} bỏ qua: có thể là nhiều nhân vật");
            else if (owner is not null) dropped.Add($"cách gọi {form.Zh} bỏ qua: đã thuộc hồ sơ {owner.ChineseName}");
            else if (AddressingAiSurvey.HasHan(form.Vi)) dropped.Add($"cách gọi {form.Zh} bỏ qua: tên Việt có chữ Hán");
            else if (BookIntelligence.ChineseMentionDiscovery.IsTitleForm(form.Zh)) dropped.Add($"cách gọi {form.Zh} bỏ qua: danh xưng, có thể chỉ nhiều người");
            else forms.Add(term is null ? form : form with { Vi = term.Vietnamese }); // The glossary wins over the AI.
        }
        var gender = p.Gender switch { "male" => CharacterGender.Male, "female" => CharacterGender.Female, _ => CharacterGender.Unknown };
        var phase = p.Phases[0];
        return (new(gender, name, phase.NarrativeVi, phase.SecondaryVi, forms, 0, AutoNote), dropped);
    }

    // ---------- shared write rules ----------

    /// <summary>
    /// Writes one character proposal into <paramref name="resources"/>, or throws (nothing usable is half-written by callers,
    /// which work on a copy). Automatic mode never changes a locked profile or replaces an existing narrative pronoun.
    /// </summary>
    private static string ApplyCharacter(ProjectResources resources, AddressingProposal proposal, CharacterAcceptance edit, bool automatic)
    {
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

        var profiles = resources.Characters.Profiles;
        var profile = Find(profiles, proposal.Subject);
        bool created = profile is null;
        if (automatic && profile is not null)
        {
            if (profile.NarrativePronounLocked) throw new InvalidOperationException($"hồ sơ {profile.ChineseName} đã khóa; không ghi đè");
            if (profile.PrimaryNarrativePronoun.Trim().Length > 0 && !string.Equals(profile.PrimaryNarrativePronoun.Trim(), narrative, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"hồ sơ {profile.ChineseName} đã có đại từ kể \"{profile.PrimaryNarrativePronoun}\"; không ghi đè");
            if (profile.Gender != CharacterGender.Unknown && profile.Gender != edit.Gender)
                throw new InvalidOperationException($"hồ sơ {profile.ChineseName} đã có giới tính {profile.Gender}; không ghi đè");
        }
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

        return $"{(created ? "Tạo" : "Cập nhật")} hồ sơ {proposal.Subject}={vietnamese}; {edit.Gender}; kể: {narrative}" + (secondary.Length > 0 ? "/" + secondary : "") +
            $" (khóa, ch.{phase.FromChapter}–{(phase.ToChapter == 0 ? "…" : phase.ToChapter)}); bí danh: {string.Join(", ", forms.Where(f => f.Zh != profile.ChineseName).Select(f => f.Zh + "=" + f.Vi))}";
    }

    /// <summary>Writes one directed pair. Automatic mode never replaces an existing rule for the same direction, locked or not.</summary>
    private static string ApplyPair(ProjectResources resources, AddressingProposal proposal, PairAcceptance edit, bool automatic)
    {
        string self = Term(edit.SelfReference, "Tự xưng"), address = Term(edit.AddresseeReference, "Gọi người");
        string relationship = Vietnamese(edit.Relationship.Trim().Length > 0 ? edit.Relationship.Trim() : "Xưng hô", "Quan hệ");
        if (relationship.Length > 200) throw new InvalidOperationException("Nhãn quan hệ quá dài.");
        _ = Phase(proposal, edit.PhaseIndex); // Validates the reviewed phase index.
        var speaker = Find(resources.Characters.Profiles, proposal.Subject) ?? throw new InvalidOperationException($"Chưa có hồ sơ {proposal.Subject}. Hãy duyệt nhân vật này trước.");
        var listener = Find(resources.Characters.Profiles, proposal.Listener!) ?? throw new InvalidOperationException($"Chưa có hồ sơ {proposal.Listener}. Hãy duyệt nhân vật này trước.");
        if (speaker == listener) throw new InvalidOperationException("Người nói và người nghe là cùng một hồ sơ.");
        var existing = speaker.Relationships.Where(r => r.EffectiveListenerCharacterId == listener.Id && r.EffectiveSpeakerCharacterId(speaker.Id) == speaker.Id).ToArray();
        if (automatic && existing.Length > 0)
            throw new InvalidOperationException($"{speaker.ChineseName} → {listener.ChineseName} đã có quy tắc xưng hô; không ghi đè");
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
                Enabled = true, Locked = true, AddressingNotes = automatic ? "Sổ xưng hô (tự động)" : "Sổ xưng hô (đã duyệt)",
                EffectiveFromChapter = proposal.Phases.Count > 1 ? x.p.FromChapter : null,
                EffectiveToChapter = proposal.Phases.Count > 1 && x.p.ToChapter != 0 ? x.p.ToChapter : null
            };
        }).ToArray();
        speaker.Relationships.AddRange(rules);
        return $"{proposal.Subject} → {proposal.Listener}: " + string.Join("; ", rules.Select(r =>
            (r.EffectiveFromChapter is null ? "" : $"ch.{r.EffectiveFromChapter}–{(r.EffectiveToChapter?.ToString() ?? "…")} ") + $"xưng {r.SelfReference} / gọi {r.AddresseeReference}")) + $" ({relationship}, khóa)";
    }

    public Task<AddressingLedgerState> RejectAsync(Guid proposalId, string note = "", CancellationToken ct = default) => Decide(proposalId, AddressingDecision.Rejected, note, ct);
    public Task<AddressingLedgerState> DeferAsync(Guid proposalId, string note = "", CancellationToken ct = default) => Decide(proposalId, AddressingDecision.Deferred, note, ct);

    /// <summary>Explicit switch; accepting proposals one by one never turns the translation lock on by itself ("Hoàn thành" does).</summary>
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
