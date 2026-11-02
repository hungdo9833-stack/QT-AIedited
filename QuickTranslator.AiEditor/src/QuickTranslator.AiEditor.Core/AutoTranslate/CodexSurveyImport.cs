using System.Text.Json;
using System.Text.Json.Serialization;
using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.AutoTranslate;

/// <summary>
/// The simple files Codex writes: names and pronouns keyed by Chinese name, no GUIDs. QuickTranslator turns them into
/// a valid <see cref="CharacterProject"/> and glossary, dropping (and reporting) entries it cannot use instead of failing.
/// </summary>
public sealed class CodexCharacterFile
{
    public string Setting { get; set; } = "";
    public List<CodexCharacter> Characters { get; set; } = [];
    public List<CodexRelationship> Relationships { get; set; } = [];
}

public sealed class CodexCharacter
{
    public string ChineseName { get; set; } = "";
    public string VietnameseName { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public string Gender { get; set; } = "";
    public string NarrativePronoun { get; set; } = "";
    public string SecondaryPronoun { get; set; } = "";
    public List<string> AvoidPronouns { get; set; } = [];
    public string Notes { get; set; } = "";
}

public sealed class CodexRelationship
{
    public string Speaker { get; set; } = "";
    public string Listener { get; set; } = "";
    public string Relationship { get; set; } = "";
    public string SelfReference { get; set; } = "";
    public string AddresseeReference { get; set; } = "";
    public string Notes { get; set; } = "";
}

public sealed record CodexGlossaryEntry(string Chinese, string Vietnamese);

public sealed record CodexSurveyImportResult(CharacterProject Characters, IReadOnlyList<ConfirmedTerm> Terms,
    IReadOnlyList<string> Warnings);

public static class CodexSurveyImport
{
    public const int MaxTerms = 1000;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>Throws <see cref="InvalidDataException"/> only when a file is not readable JSON of the expected shape.</summary>
    public static CodexSurveyImportResult Convert(string charactersJson, string glossaryJson, Guid projectId, string projectName)
    {
        CodexCharacterFile file = Parse<CodexCharacterFile>(charactersJson, "characters.json");
        List<CodexGlossaryEntry> glossary = Parse<List<CodexGlossaryEntry>>(glossaryJson, "glossary.json");
        var warnings = new List<string>();
        var project = new CharacterProject
        {
            Id = projectId,
            Name = Clip(string.IsNullOrWhiteSpace(projectName) ? "Bộ truyện mới" : projectName.Trim(), 300),
            Setting = ParseSetting(file.Setting)
        };

        var byName = new Dictionary<string, CharacterProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (CodexCharacter source in file.Characters ?? [])
        {
            if (project.Profiles.Count >= 1000) { warnings.Add("Quá 1.000 nhân vật; phần còn lại bị bỏ qua."); break; }
            if (source is null) continue;
            string vietnamese = Clip(source.VietnameseName?.Trim() ?? "", 200);
            string chinese = Clip(source.ChineseName?.Trim() ?? "", 200);
            if (vietnamese.Length == 0) { warnings.Add($"Bỏ nhân vật “{chinese}”: thiếu tên tiếng Việt."); continue; }
            if (project.Profiles.Any(p => string.Equals(p.VietnameseName, vietnamese, StringComparison.OrdinalIgnoreCase)))
            { warnings.Add($"Bỏ nhân vật trùng tên “{vietnamese}”."); continue; }
            var profile = new CharacterProfile
            {
                ChineseName = chinese,
                VietnameseName = vietnamese,
                Gender = ParseGender(source.Gender),
                Aliases = CleanList(source.Aliases, 100, chinese, vietnamese),
                PrimaryNarrativePronoun = Clip(source.NarrativePronoun?.Trim() ?? "", 80),
                SecondaryNarrativePronoun = Clip(source.SecondaryPronoun?.Trim() ?? "", 80),
                DisallowedNarrativePronouns = CleanList(source.AvoidPronouns, 100),
                AddressingNotes = Clip(source.Notes?.Trim() ?? "", 2000)
            };
            if (project.Setting == StorySetting.Modern) ModernizeNarrativePronouns(profile, warnings);
            else profile.ApplyGenderDefaults();
            // Pronoun normalization needs both a pronoun and words to avoid; without them the profile still names the character.
            profile.NormalizationEnabled = profile.PrimaryNarrativePronoun.Length > 0 && profile.DisallowedNarrativePronouns.Count > 0;
            project.Profiles.Add(profile);
            foreach (string key in new[] { chinese, vietnamese }.Concat(profile.Aliases))
                if (key.Length > 0) byName.TryAdd(key, profile);
        }

        foreach (CodexRelationship source in file.Relationships ?? [])
        {
            if (source is null) continue;
            if (!byName.TryGetValue(source.Speaker?.Trim() ?? "", out CharacterProfile? speaker) ||
                !byName.TryGetValue(source.Listener?.Trim() ?? "", out CharacterProfile? listener))
            { warnings.Add($"Bỏ quan hệ “{source.Speaker} → {source.Listener}”: không tìm thấy nhân vật."); continue; }
            if (speaker == listener || speaker.Relationships.Count >= 100 ||
                speaker.Relationships.Any(r => r.EffectiveListenerCharacterId == listener.Id)) continue;
            string self = Clip(source.SelfReference?.Trim() ?? "", 80);
            string addressee = Clip(source.AddresseeReference?.Trim() ?? "", 80);
            string relationship = Clip(source.Relationship?.Trim() ?? "", 200);
            bool directional = self.Length > 0 && addressee.Length > 0;
            if (!directional && relationship.Length == 0) continue;
            speaker.Relationships.Add(new CharacterRelationship
            {
                TargetCharacterId = listener.Id,
                SpeakerCharacterId = directional ? speaker.Id : Guid.Empty,
                ListenerCharacterId = directional ? listener.Id : Guid.Empty,
                Relationship = relationship,
                SelfReference = directional ? self : "",
                AddresseeReference = directional ? addressee : "",
                AddressingNotes = Clip(source.Notes?.Trim() ?? "", 2000),
                Enabled = directional
            });
        }
        project.Validate();

        var terms = new List<ConfirmedTerm>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (CodexGlossaryEntry entry in glossary)
        {
            string chinese = entry?.Chinese?.Trim() ?? "", vietnamese = entry?.Vietnamese?.Trim() ?? "";
            if (chinese.Length == 0 || vietnamese.Length == 0 || !seen.Add(chinese)) continue;
            if (terms.Count >= MaxTerms) { warnings.Add($"Từ điển quá {MaxTerms:N0} mục; phần còn lại bị bỏ qua."); break; }
            terms.Add(new(chinese, vietnamese));
        }
        return new(project, terms, warnings);
    }

    /// <summary>Narrative pronouns of period fiction; a modern book reads them as archaic.</summary>
    public static IReadOnlyList<string> ArchaicNarrativePronouns { get; } = ["hắn", "hắn ta", "nàng", "nàng ấy", "chàng", "y", "thị"];

    /// <summary>
    /// A modern book never narrates with hắn / nàng: archaic choices (or none) become the gender's modern default, and the
    /// archaic forms join the words to avoid so the translation is normalized the same way.
    /// </summary>
    private static void ModernizeNarrativePronouns(CharacterProfile profile, List<string> warnings)
    {
        (string primary, string secondary) = profile.Gender switch
        {
            CharacterGender.Male => ("anh", "anh ta"),
            CharacterGender.Female => ("cô", "cô ấy"),
            _ => ("", "")
        };
        bool IsArchaic(string pronoun) => ArchaicNarrativePronouns.Contains(pronoun.Trim(), StringComparer.OrdinalIgnoreCase);
        if (IsArchaic(profile.PrimaryNarrativePronoun) || IsArchaic(profile.SecondaryNarrativePronoun))
            warnings.Add($"Đổi đại từ cổ trang “{profile.PrimaryNarrativePronoun}/{profile.SecondaryNarrativePronoun}” của " +
                $"{profile.VietnameseName} sang hiện đại.");
        // A modern secondary pronoun already tells age and attitude ("ông ta", "bà ấy", "lão ta"): its head word is the primary.
        string secondaryHead = profile.SecondaryNarrativePronoun.Trim().Split(' ')[0];
        if (profile.PrimaryNarrativePronoun.Length == 0 || IsArchaic(profile.PrimaryNarrativePronoun))
            profile.PrimaryNarrativePronoun = secondaryHead.Length > 0 && !IsArchaic(profile.SecondaryNarrativePronoun) &&
                !IsArchaic(secondaryHead) ? secondaryHead : primary;
        if (profile.SecondaryNarrativePronoun.Length == 0 || IsArchaic(profile.SecondaryNarrativePronoun))
            profile.SecondaryNarrativePronoun = primary.Length == 0 ? "" : secondary;
        string[] chosen = [profile.PrimaryNarrativePronoun, profile.SecondaryNarrativePronoun];
        profile.DisallowedNarrativePronouns = CleanList(profile.DisallowedNarrativePronouns
            .Concat(ArchaicNarrativePronouns), 100, chosen);
    }

    private static T Parse<T>(string json, string name) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException($"{name} trống.");
        }
        catch (JsonException ex) { throw new InvalidDataException($"{name} không phải JSON hợp lệ: {ex.Message}", ex); }
    }

    private static StorySetting ParseSetting(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "modern" or "hiện đại" => StorySetting.Modern,
        "historical" or "ancient" or "cổ đại" or "cổ trang" => StorySetting.Historical,
        _ => StorySetting.Unspecified
    };

    private static CharacterGender ParseGender(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "male" or "nam" or "m" => CharacterGender.Male,
        "female" or "nữ" or "f" => CharacterGender.Female,
        "other" or "khác" => CharacterGender.Other,
        _ => CharacterGender.Unknown
    };

    private static List<string> CleanList(IEnumerable<string>? values, int max, params string[] exclude)
    {
        var result = new List<string>();
        foreach (string value in values ?? [])
        {
            string item = Clip(value?.Trim() ?? "", 200);
            if (item.Length == 0 || exclude.Contains(item, StringComparer.OrdinalIgnoreCase) ||
                result.Contains(item, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(item);
            if (result.Count >= max) break;
        }
        return result;
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
