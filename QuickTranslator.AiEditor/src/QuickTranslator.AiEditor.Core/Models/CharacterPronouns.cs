using System.Text.Json.Serialization;

namespace QuickTranslator.AiEditor.Core.Models;

public enum CharacterGender { Unknown, Male, Female, Other }
public enum PronounSuggestionKind { SafeReplacement, Ambiguous, Valid, Protected }

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

    [JsonIgnore]
    public Guid EffectiveListenerCharacterId => ListenerCharacterId == Guid.Empty ? TargetCharacterId : ListenerCharacterId;

    public Guid EffectiveSpeakerCharacterId(Guid ownerCharacterId) =>
        SpeakerCharacterId == Guid.Empty ? ownerCharacterId : SpeakerCharacterId;

    [JsonIgnore]
    public bool HasDirectionalAddressing => !string.IsNullOrWhiteSpace(SelfReference) ||
        !string.IsNullOrWhiteSpace(AddresseeReference) || SpeakerCharacterId != Guid.Empty || ListenerCharacterId != Guid.Empty;
}

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
    [JsonIgnore]
    public string GenderLabel => Gender switch
    {
        CharacterGender.Male => "Nam", CharacterGender.Female => "Nữ", CharacterGender.Other => "Khác", _ => "Chưa xác định"
    };

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
    public List<CharacterProfile> Profiles { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1 || Id == Guid.Empty || string.IsNullOrWhiteSpace(Name) || Name.Length > 300 ||
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
