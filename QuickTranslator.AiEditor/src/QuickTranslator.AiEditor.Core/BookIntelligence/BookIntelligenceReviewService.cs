using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.BookIntelligence;

public sealed record IntelligenceCommitResult(ProjectResources Resources, BookIntelligenceState State);

public sealed class BookIntelligenceReviewService(ProjectSession session)
{
    private readonly ProjectResourceStore _resources = new(session);
    private readonly BookIntelligenceStore _state = new(session);

    public async Task<IntelligenceCommitResult> AcceptCharacterAsync(BookIntelligenceState state, Guid suggestionId,
        CharacterProfile edited, long expectedResourceRevision, CancellationToken cancellationToken = default)
    {
        IntelligenceCharacterSuggestion suggestion = state.Characters.Single(x => x.Id == suggestionId);
        EnsureReviewable(suggestion.Status);
        await EnsureEvidenceCurrentAsync(state, suggestion.EvidenceIds, cancellationToken).ConfigureAwait(false);
        ProjectResources resources = await LoadExpectedAsync(expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        CharacterProfile profile = CharacterProjectStore.Clone(new CharacterProject { Profiles = [edited],
            Id = resources.ProjectId, Name = "validation" }).Profiles.Single();
        profile.Validate();
        if (resources.Characters.Profiles.Any(p => p.Id != profile.Id &&
            (p.VietnameseName.Equals(profile.VietnameseName, StringComparison.OrdinalIgnoreCase) ||
             HasNameCollision(p, profile.ChineseName, profile.Aliases))))
            throw new InvalidOperationException("Tên hoặc bí danh đã thuộc nhân vật hiện có. Hãy dùng Gộp vào nhân vật đã có.");
        resources.Characters.Profiles.Add(profile);
        resources.Validate();
        resources = await _resources.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        suggestion.Status = IntelligenceReviewStatus.Accepted;
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<IntelligenceCommitResult> MergeCharacterAsync(BookIntelligenceState state, Guid suggestionId,
        Guid targetCharacterId, long expectedResourceRevision, CancellationToken cancellationToken = default)
    {
        IntelligenceCharacterSuggestion suggestion = state.Characters.Single(x => x.Id == suggestionId);
        EnsureReviewable(suggestion.Status);
        await EnsureEvidenceCurrentAsync(state, suggestion.EvidenceIds, cancellationToken).ConfigureAwait(false);
        ProjectResources resources = await LoadExpectedAsync(expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        CharacterProfile target = resources.Characters.Profiles.Single(x => x.Id == targetCharacterId);
        IEnumerable<string> proposedNames = new[] { suggestion.ChineseName }.Concat(suggestion.Aliases)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal);
        foreach (string name in proposedNames)
        {
            CharacterProfile? owner = resources.Characters.Profiles.FirstOrDefault(p => p.Id != target.Id &&
                (p.ChineseName == name || p.Aliases.Contains(name, StringComparer.Ordinal)));
            if (owner is not null) throw new InvalidOperationException($"Tên {name} đã thuộc {owner.VietnameseName}; không thể gộp.");
            if (string.IsNullOrWhiteSpace(target.ChineseName)) target.ChineseName = name;
            else if (target.ChineseName != name && !target.Aliases.Contains(name, StringComparer.Ordinal)) target.Aliases.Add(name);
        }
        target.Validate();
        resources = await _resources.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        suggestion.Status = IntelligenceReviewStatus.Merged;
        suggestion.PossibleExistingCharacterId = target.Id;
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<IntelligenceCommitResult> AcceptGlossaryAsync(BookIntelligenceState state, Guid suggestionId,
        string vietnamese, long expectedResourceRevision, CancellationToken cancellationToken = default)
    {
        IntelligenceGlossarySuggestion suggestion = state.Glossary.Single(x => x.Id == suggestionId);
        EnsureReviewable(suggestion.Status);
        await EnsureEvidenceCurrentAsync(state, suggestion.EvidenceIds, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(vietnamese)) throw new ArgumentException("Bản dịch Glossary không được để trống.");
        ProjectResources resources = await LoadExpectedAsync(expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        if (resources.Characters.Profiles.Any(p => p.ChineseName == suggestion.Chinese || p.Aliases.Contains(suggestion.Chinese, StringComparer.Ordinal)))
            throw new InvalidOperationException("Cụm này thuộc hồ sơ nhân vật; không lưu thành Glossary toàn cục.");
        ConfirmedTerm? existing = resources.Terms.SingleOrDefault(x => x.Chinese == suggestion.Chinese);
        if (existing is not null && existing.Vietnamese != vietnamese.Trim())
            throw new InvalidOperationException("Glossary đã có bản dịch khác; không ghi đè chỉnh sửa của người dùng.");
        if (existing is null) resources.Terms.Add(new(suggestion.Chinese, vietnamese.Trim()));
        resources = await _resources.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        suggestion.Vietnamese = vietnamese.Trim();
        suggestion.Status = IntelligenceReviewStatus.Accepted;
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<IntelligenceCommitResult> AcceptRelationshipAsync(BookIntelligenceState state, Guid suggestionId,
        long expectedResourceRevision, CancellationToken cancellationToken = default)
    {
        IntelligenceRelationshipSuggestion suggestion = state.Relationships.Single(x => x.Id == suggestionId);
        EnsureReviewable(suggestion.Status);
        await EnsureEvidenceCurrentAsync(state, suggestion.EvidenceIds, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(suggestion.SelfReference) || string.IsNullOrWhiteSpace(suggestion.AddresseeReference))
            throw new InvalidOperationException("Quan hệ cần được chỉnh và xác nhận đủ cách tự xưng/gọi đối phương trong hồ sơ nhân vật.");
        ProjectResources resources = await LoadExpectedAsync(expectedResourceRevision, cancellationToken).ConfigureAwait(false);
        CharacterProfile speaker = FindCharacter(resources, suggestion.SpeakerChineseName);
        CharacterProfile listener = FindCharacter(resources, suggestion.ListenerChineseName);
        CharacterRelationship? existing = speaker.Relationships.SingleOrDefault(x => x.EffectiveListenerCharacterId == listener.Id);
        if (existing is not null)
            throw new InvalidOperationException(existing.Locked
                ? "Quan hệ hiện có đang khóa; AI không được ghi đè."
                : "Quan hệ đã được người dùng chỉnh; hãy mở hồ sơ nhân vật để đối chiếu, không ghi đè tự động.");
        speaker.Relationships.Add(new CharacterRelationship
        {
            TargetCharacterId = listener.Id, SpeakerCharacterId = speaker.Id, ListenerCharacterId = listener.Id,
            Relationship = suggestion.Relationship, SelfReference = suggestion.SelfReference,
            AddresseeReference = suggestion.AddresseeReference, Enabled = true, Locked = false,
            AddressingNotes = "Đã xác nhận từ AI Book Intelligence: " + suggestion.Reason
        });
        if (!string.IsNullOrWhiteSpace(suggestion.ReverseSelfReference) &&
            !string.IsNullOrWhiteSpace(suggestion.ReverseAddresseeReference))
        {
            CharacterRelationship? reverse = listener.Relationships.SingleOrDefault(x => x.EffectiveListenerCharacterId == speaker.Id);
            if (reverse is not null)
                throw new InvalidOperationException(reverse.Locked
                    ? "Quan hệ chiều ngược đang khóa; giữ nguyên và không ghi đè."
                    : "Quan hệ chiều ngược đã được người dùng chỉnh; không ghi đè tự động.");
            listener.Relationships.Add(new CharacterRelationship
            {
                TargetCharacterId = speaker.Id, SpeakerCharacterId = listener.Id, ListenerCharacterId = speaker.Id,
                Relationship = suggestion.Relationship, SelfReference = suggestion.ReverseSelfReference,
                AddresseeReference = suggestion.ReverseAddresseeReference, Enabled = true, Locked = false,
                AddressingNotes = "Đã xác nhận chiều ngược từ AI Book Intelligence: " + suggestion.Reason
            });
        }
        resources.Validate();
        resources = await _resources.SaveAsync(resources, cancellationToken).ConfigureAwait(false);
        suggestion.Status = IntelligenceReviewStatus.Accepted;
        state = await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return new(resources, state);
    }

    public async Task<BookIntelligenceState> SetReviewStatusAsync(BookIntelligenceState state, Guid id,
        IntelligenceReviewStatus status, CancellationToken cancellationToken = default)
    {
        if (status is IntelligenceReviewStatus.Accepted or IntelligenceReviewStatus.Merged)
            throw new ArgumentException("Trạng thái lưu/gộp chỉ được đặt sau khi Project Resources commit thành công.");
        object item = state.Characters.Cast<object>().Concat(state.Relationships).Concat(state.Glossary)
            .Single(x => x switch
            {
                IntelligenceCharacterSuggestion c => c.Id == id,
                IntelligenceRelationshipSuggestion r => r.Id == id,
                IntelligenceGlossarySuggestion g => g.Id == id,
                _ => false
            });
        switch (item)
        {
            case IntelligenceCharacterSuggestion c: c.Status = status; break;
            case IntelligenceRelationshipSuggestion r: r.Status = status; break;
            case IntelligenceGlossarySuggestion g: g.Status = status; break;
        }
        return await _state.SaveAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectResources> LoadExpectedAsync(long expectedRevision, CancellationToken cancellationToken)
    {
        ProjectResources resources = await _resources.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (resources.Revision != expectedRevision)
            throw new InvalidOperationException("Characters/Glossary đã đổi sau khi mở hàng chờ. Hãy nạp lại đề xuất trước khi lưu.");
        return resources;
    }
    private async Task EnsureEvidenceCurrentAsync(BookIntelligenceState state, IEnumerable<Guid> evidenceIds,
        CancellationToken cancellationToken)
    {
        Dictionary<Guid, CorpusEvidence> evidence = state.Evidence.ToDictionary(x => x.Id);
        CorpusEvidence[] selected = evidenceIds.Distinct().Select(id => evidence.TryGetValue(id, out CorpusEvidence? item)
            ? item : throw new InvalidDataException("Đề xuất tham chiếu chứng cứ không còn trong Knowledge Base.")).ToArray();
        if (selected.Length == 0 || selected.Any(x => !x.IsCurrent))
            throw new InvalidOperationException("Nguyên tác đã đổi sau khi phân tích. Hãy quét lại trước khi lưu đề xuất này.");
        var repository = new ProjectRepository();
        foreach (IGrouping<Guid, CorpusEvidence> chapterEvidence in selected.GroupBy(x => x.ChapterId))
        {
            ChapterDocument chapter = (await repository.LoadChapterAsync(session, chapterEvidence.Key, cancellationToken)
                .ConfigureAwait(false)).Chapter;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chapter.OriginalText ?? "")));
            if (chapterEvidence.Any(x => !string.Equals(x.SourceSha256, hash, StringComparison.Ordinal)))
                throw new InvalidOperationException("Nguyên tác đã đổi sau khi phân tích. Hãy quét lại trước khi lưu đề xuất này.");
        }
    }
    private static CharacterProfile FindCharacter(ProjectResources resources, string chinese) =>
        resources.Characters.Profiles.SingleOrDefault(p => p.ChineseName == chinese || p.Aliases.Contains(chinese, StringComparer.Ordinal))
        ?? throw new InvalidOperationException($"Chưa có hồ sơ đã xác nhận cho {chinese}.");
    private static bool HasNameCollision(CharacterProfile existing, string chinese, IEnumerable<string> aliases) =>
        (!string.IsNullOrWhiteSpace(chinese) && (existing.ChineseName == chinese || existing.Aliases.Contains(chinese, StringComparer.Ordinal))) ||
        aliases.Any(alias => existing.ChineseName == alias || existing.Aliases.Contains(alias, StringComparer.Ordinal));
    private static void EnsureReviewable(IntelligenceReviewStatus status)
    {
        if (status is not (IntelligenceReviewStatus.Pending or IntelligenceReviewStatus.ReviewLater))
            throw new InvalidOperationException("Đề xuất này đã được xử lý.");
    }
}
