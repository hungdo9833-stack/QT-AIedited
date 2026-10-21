using System.Text.Json.Serialization;

namespace QuickTranslator.AiEditor.Core.Models;

public enum CharacterGender { Unknown, Male, Female, Other }
public enum PronounSuggestionKind { SafeReplacement, Ambiguous, Valid, Protected }
public enum StorySetting { Unspecified, Modern, Historical }

public sealed record CharacterRelationship
{
    public Guid TargetCharacterId { get; init; }
    public string Relationship { get; init; } = "";
    public string AddressingNotes { get; init; } = "";
    // Optional V1 directional fields. TargetCharacterId/Relationship remain for old project files.
    public Guid SpeakerCharacterId { get; init; }
    public Guid ListenerCharacterId { get; init; }
    public string SelfReference { get; init; } = "";
    public string AddresseeReference { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Locked { get; init; }
    // Optional chapter range (Addressing Ledger step 4). Null = the whole book, as before.
    public int? EffectiveFromChapter { get; init; }
    public int? EffectiveToChapter { get; init; }

    [JsonIgnore]
    public Guid EffectiveListenerCharacterId => ListenerCharacterId == Guid.Empty ? TargetCharacterId : ListenerCharacterId;

    /// <summary>True when the rule has no range, or the (known) chapter lies inside it. A ranged rule never applies to an unknown chapter.</summary>
    public bool AppliesTo(int? chapter) => EffectiveFromChapter is null && EffectiveToChapter is null ||
        chapter is int c && c >= (EffectiveFromChapter ?? 1) && c <= (EffectiveToChapter ?? int.MaxValue);

    public Guid EffectiveSpeakerCharacterId(Guid ownerCharacterId) =>
        SpeakerCharacterId == Guid.Empty ? ownerCharacterId : SpeakerCharacterId;

    [JsonIgnore]
    public bool HasDirectionalAddressing => !string.IsNullOrWhiteSpace(SelfReference) ||
        !string.IsNullOrWhiteSpace(AddresseeReference) || SpeakerCharacterId != Guid.Empty || ListenerCharacterId != Guid.Empty;
}

public sealed record CharacterRelationshipPreset(
    string Label,
    string Relationship,
    string SelfReference,
    string AddresseeReference,
    string ReverseRelationship,
    string ReverseSelfReference,
    string ReverseAddresseeReference)
{
    public override string ToString() => Label;

    public static IReadOnlyList<CharacterRelationshipPreset> Defaults { get; } =
    [
        new("Chồng → Vợ (anh – em)", "Chồng - Vợ", "anh", "em", "Vợ - Chồng", "em", "anh"),
        new("Vợ → Chồng (em – anh)", "Vợ - Chồng", "em", "anh", "Chồng - Vợ", "anh", "em"),
        new("Huynh → Muội (huynh – muội)", "Huynh - Muội", "huynh", "muội", "Muội - Huynh", "muội", "huynh"),
        new("Tỷ → Đệ (tỷ – đệ)", "Tỷ - Đệ", "tỷ", "đệ", "Đệ - Tỷ", "đệ", "tỷ"),
        new("Sư phụ → Đồ đệ (vi sư – đồ nhi)", "Sư phụ - Đồ đệ", "vi sư", "đồ nhi", "Đồ đệ - Sư phụ", "đồ nhi", "sư phụ"),
        new("Chủ thượng → Thuộc hạ (bản tọa – ngươi)", "Chủ - Thuộc hạ", "bản tọa", "ngươi", "Thuộc hạ - Chủ", "thuộc hạ", "chủ thượng"),
        new("Ngang hàng cổ trang (ta – ngươi)", "Ngang hàng", "ta", "ngươi", "Ngang hàng", "ta", "ngươi"),
        new("Ngang hàng hiện đại (tôi – cậu)", "Bạn bè", "tôi", "cậu", "Bạn bè", "tôi", "cậu")
    ];
}

public static class CharacterRelationshipSync
{
    /// <summary>
    /// Creates or updates an explicitly confirmed reverse relationship (B -> A) on the target listener profile.
    /// Never infers unconfirmed fields and NEVER overwrites a Locked relationship rule on the target profile (Adjustment A).
    /// </summary>
    public static bool TryApplyConfirmedReverseRule(
        CharacterProject project,
        Guid speakerCharacterId,
        Guid listenerCharacterId,
        string reverseRelationship,
        string reverseSelfReference,
        string reverseAddresseeReference,
        bool enabled,
        bool locked,
        string addressingNotes,
        out string? warning)
    {
        ArgumentNullException.ThrowIfNull(project);
        warning = null;
        if (speakerCharacterId == Guid.Empty || listenerCharacterId == Guid.Empty || speakerCharacterId == listenerCharacterId)
        {
            warning = "Nhân vật nói và nhân vật nghe của quy tắc ngược không hợp lệ.";
            return false;
        }
        CharacterProfile? speaker = project.Profiles.SingleOrDefault(p => p.Id == speakerCharacterId);
        CharacterProfile? listener = project.Profiles.SingleOrDefault(p => p.Id == listenerCharacterId);
        if (speaker is null || listener is null)
        {
            warning = "Không tìm thấy hồ sơ nhân vật đích trong dự án.";
            return false;
        }
        string selfRef = (reverseSelfReference ?? "").Trim();
        string addrRef = (reverseAddresseeReference ?? "").Trim();
        string relName = (reverseRelationship ?? "").Trim();
        if (string.IsNullOrWhiteSpace(selfRef) || string.IsNullOrWhiteSpace(addrRef))
        {
            warning = $"Quy tắc chiều ngược ({listener.VietnameseName} → {speaker.VietnameseName}) cần đủ Tự xưng và Gọi đối phương.";
            return false;
        }

        int existingIndex = listener.Relationships.FindIndex(r => r.EffectiveListenerCharacterId == speaker.Id);
        if (existingIndex >= 0 && listener.Relationships[existingIndex].Locked)
        {
            warning = $"Quy tắc {listener.VietnameseName} → {speaker.VietnameseName} đang Khóa (Locked); giữ nguyên không ghi đè.";
            return false;
        }

        var reverseRule = new CharacterRelationship
        {
            TargetCharacterId = speaker.Id,
            SpeakerCharacterId = listener.Id,
            ListenerCharacterId = speaker.Id,
            Relationship = string.IsNullOrWhiteSpace(relName) ? "đã xác nhận" : relName,
            SelfReference = selfRef,
            AddresseeReference = addrRef,
            Enabled = enabled,
            Locked = locked,
            AddressingNotes = (addressingNotes ?? "").Trim()
        };

        if (existingIndex >= 0) listener.Relationships[existingIndex] = reverseRule;
        else listener.Relationships.Add(reverseRule);
        listener.Validate();
        return true;
    }
}

/// <summary>Narrative pronoun valid from a chapter (inclusive) to an optional end chapter (inclusive; null = open).</summary>
public sealed record NarrativePronounPhase(int FromChapter, int? ToChapter, string Primary, string Secondary);

public sealed class CharacterProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ChineseName { get; set; } = "";
    public string VietnameseName { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public CharacterGender Gender { get; set; }
    public List<CharacterRelationship> Relationships { get; set; } = [];
    public string PrimaryNarrativePronoun { get; set; } = "";
    public string SecondaryNarrativePronoun { get; set; } = "";
    public List<string> DisallowedNarrativePronouns { get; set; } = [];
    public string AddressingNotes { get; set; } = "";
    public bool NormalizationEnabled { get; set; } = true;
    public bool NarrativePronounLocked { get; set; }
    /// <summary>Optional chapter-specific narrative pronouns; Primary/SecondaryNarrativePronoun stay the default.</summary>
    public List<NarrativePronounPhase> NarrativePhases { get; set; } = [];

    /// <summary>Narrative pronoun for a chapter: the phase covering it, otherwise the default primary pronoun.</summary>
    public string NarrativePronounFor(int? chapter) =>
        (chapter is int c ? NarrativePhases.FirstOrDefault(p => c >= p.FromChapter && c <= (p.ToChapter ?? int.MaxValue))?.Primary : null) ?? PrimaryNarrativePronoun;
    [JsonIgnore]
    public string GenderLabel => Gender switch
    {
        CharacterGender.Male => "Nam", CharacterGender.Female => "Nữ", CharacterGender.Other => "Khác", _ => "Chưa xác định"
    };

    [JsonIgnore]
    public string RelationshipSummary
    {
        get
        {
            if (Relationships is null || Relationships.Count == 0)
                return string.IsNullOrWhiteSpace(AddressingNotes) ? "Chưa có" : AddressingNotes;
            int active = Relationships.Count(r => r.Enabled);
            int locked = Relationships.Count(r => r.Locked);
            var parts = Relationships.Take(3).Select(r =>
                !string.IsNullOrWhiteSpace(r.SelfReference) && !string.IsNullOrWhiteSpace(r.AddresseeReference)
                    ? $"{(string.IsNullOrWhiteSpace(r.Relationship) ? "Đàm thoại" : r.Relationship)} ({r.SelfReference}→{r.AddresseeReference}{(r.Locked ? " 🔒" : "")})"
                    : r.Relationship);
            string head = $"{Relationships.Count} quan hệ ({active} bật, {locked} khóa): ";
            return head + string.Join(", ", parts) + (Relationships.Count > 3 ? "…" : "");
        }
    }

    public void ApplyGenderDefaults()
    {
        if (Gender == CharacterGender.Female)
        {
            if (string.IsNullOrWhiteSpace(PrimaryNarrativePronoun)) PrimaryNarrativePronoun = "nàng";
            if (string.IsNullOrWhiteSpace(SecondaryNarrativePronoun)) SecondaryNarrativePronoun = "nàng ấy";
            if (DisallowedNarrativePronouns.Count == 0) DisallowedNarrativePronouns = ["bà ấy", "cô ấy", "cô ta", "bà", "cô", "chị ấy"];
        }
        else if (Gender == CharacterGender.Male)
        {
            if (string.IsNullOrWhiteSpace(PrimaryNarrativePronoun)) PrimaryNarrativePronoun = "hắn";
            if (string.IsNullOrWhiteSpace(SecondaryNarrativePronoun)) SecondaryNarrativePronoun = "hắn ta";
            if (DisallowedNarrativePronouns.Count == 0) DisallowedNarrativePronouns = ["ông ấy", "anh ấy", "cậu ấy", "ông", "anh", "cậu"];
        }
    }

    public void Validate()
    {
        if (ChineseName is null || PrimaryNarrativePronoun is null || SecondaryNarrativePronoun is null || AddressingNotes is null ||
            Aliases is null || DisallowedNarrativePronouns is null || Relationships is null)
            throw new ArgumentException("Hồ sơ nhân vật có trường null; dữ liệu chưa hợp lệ để phục hồi/lưu.");
        if (Id == Guid.Empty || string.IsNullOrWhiteSpace(VietnameseName) || VietnameseName.Length > 200 ||
            ChineseName.Length > 200 || !Enum.IsDefined(Gender) || PrimaryNarrativePronoun.Length > 80 ||
            SecondaryNarrativePronoun.Length > 80 || AddressingNotes.Length > 2000 || Aliases.Count > 100 ||
            DisallowedNarrativePronouns.Count > 100 || Relationships.Count > 100)
            throw new ArgumentException("Hồ sơ nhân vật thiếu tên hoặc vượt giới hạn cho phép.");
        if (NormalizationEnabled && (string.IsNullOrWhiteSpace(PrimaryNarrativePronoun) || DisallowedNarrativePronouns.Count == 0))
            throw new ArgumentException($"Nhân vật {VietnameseName} cần đại từ chính và danh sách cách gọi cần tránh.");
        if (Aliases.Any(string.IsNullOrWhiteSpace) || DisallowedNarrativePronouns.Any(string.IsNullOrWhiteSpace) ||
            Aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Aliases.Count ||
            DisallowedNarrativePronouns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != DisallowedNarrativePronouns.Count ||
            Relationships.Any(r => r is null || r.EffectiveListenerCharacterId == Guid.Empty ||
                (string.IsNullOrWhiteSpace(r.Relationship) && !r.HasDirectionalAddressing) ||
                r.SelfReference is null || r.AddresseeReference is null || r.Relationship is null || r.AddressingNotes is null ||
                r.SelfReference.Length > 80 || r.AddresseeReference.Length > 80 || r.Relationship.Length > 200 ||
                r.AddressingNotes.Length > 2000 ||
                ((r.HasDirectionalAddressing || r.Enabled || r.Locked) &&
                 (string.IsNullOrWhiteSpace(r.SelfReference) || string.IsNullOrWhiteSpace(r.AddresseeReference)))))
            throw new ArgumentException($"Bí danh, đại từ hoặc quan hệ của {VietnameseName} không hợp lệ.");
        if (NarrativePronounLocked && string.IsNullOrWhiteSpace(PrimaryNarrativePronoun))
            throw new ArgumentException($"Nhân vật {VietnameseName} cần đại từ trần thuật chính trước khi khóa.");
        NarrativePhases ??= [];
        if (NarrativePhases.Count > 20 || NarrativePhases.Any(p => p is null || p.FromChapter < 1 || p.ToChapter < p.FromChapter ||
                string.IsNullOrWhiteSpace(p.Primary) || p.Primary.Length > 80 || p.Secondary is null || p.Secondary.Length > 80) ||
            NarrativePhases.OrderBy(p => p.FromChapter).Zip(NarrativePhases.OrderBy(p => p.FromChapter).Skip(1), (a, b) => a.ToChapter is null || a.ToChapter >= b.FromChapter).Any(x => x))
            throw new ArgumentException($"Giai đoạn đại từ kể của {VietnameseName} không hợp lệ hoặc chồng lấn.");
        if (Relationships.Any(r => r.EffectiveFromChapter < 1 || r.EffectiveToChapter < 1 || r.EffectiveToChapter < r.EffectiveFromChapter))
            throw new ArgumentException($"Khoảng chương của quan hệ {VietnameseName} không hợp lệ.");
    }
}

public sealed class CharacterProject
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Bộ truyện mới";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public bool NormalizeAfterTranslation { get; set; } = true;
    public bool ConsistencyLockEnabled { get; set; }
    /// <summary>Story era for dialogue register: modern books never get archaic addressing (ngươi, huynh…).</summary>
    public StorySetting Setting { get; set; }
    public List<CharacterProfile> Profiles { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1 || Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 300 || !Enum.IsDefined(Setting) ||
            Profiles is null || Profiles.Count > 1000 || Profiles.Any(p => p is null))
            throw new ArgumentException("Dự án nhân vật không hợp lệ.");
        foreach (var profile in Profiles) profile.Validate();
        if (Profiles.Select(p => p.Id).Distinct().Count() != Profiles.Count ||
            Profiles.Select(p => p.VietnameseName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Profiles.Count)
            throw new ArgumentException("ID hoặc tên nhân vật bị trùng trong cùng dự án.");
        var ids = Profiles.Select(p => p.Id).ToHashSet();
        if (Profiles.SelectMany(p => p.Relationships).Any(r => !ids.Contains(r.EffectiveListenerCharacterId)))
            throw new ArgumentException("Quan hệ đang tham chiếu nhân vật không thuộc dự án.");
        if (Profiles.Any(owner => owner.Relationships.Any(r =>
                r.SpeakerCharacterId != Guid.Empty && r.SpeakerCharacterId != owner.Id)))
            throw new ArgumentException("Quan hệ hai chiều có người nói không khớp hồ sơ sở hữu.");
    }
}

public sealed record PronounChange
{
    public int StartUtf16 { get; init; }
    public int Length { get; init; }
    public string Original { get; init; } = "";
    public string Replacement { get; init; } = "";
    public Guid CharacterId { get; init; }
    public string Reason { get; init; } = "";
}

public sealed class PronounNormalizationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BatchId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool Automatic { get; set; }
    public bool Reverted { get; set; }
    public string BeforeText { get; set; } = "";
    public string AfterText { get; set; } = "";
    public string? PreviousNormalizedText { get; set; }
    public List<PronounChange> Changes { get; set; } = [];
}

public sealed record PronounSuggestion(
    Guid Id, Guid SegmentId, int SegmentOrder, int StartUtf16, int Length, string Original,
    string? Replacement, PronounSuggestionKind Kind, Guid? CharacterId, string CharacterName,
    string Sentence, string SourceText, string Reason, string AnalysedText);

public sealed record PronounScanReport(Guid ChapterId, Guid CharacterProjectId, DateTimeOffset CreatedAt,
    IReadOnlyList<PronounSuggestion> Items)
{
    public int Checked => Items.Count;
    public int Safe => Items.Count(i => i.Kind == PronounSuggestionKind.SafeReplacement);
    public int Ambiguous => Items.Count(i => i.Kind == PronounSuggestionKind.Ambiguous);
    public int Valid => Items.Count(i => i.Kind == PronounSuggestionKind.Valid);
    public int Protected => Items.Count(i => i.Kind == PronounSuggestionKind.Protected);
}
