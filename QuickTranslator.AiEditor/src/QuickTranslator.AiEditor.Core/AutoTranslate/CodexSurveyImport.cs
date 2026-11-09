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

/// <summary>book.json: the translated title, a blurb for readers and genre tags for publishing.</summary>
public sealed class CodexBookFile
{
    public string VietnameseTitle { get; set; } = "";
    public string Synopsis { get; set; } = "";
    public List<string> Genres { get; set; } = [];
}

public sealed record CodexBookInfo(string VietnameseTitle, string Synopsis, IReadOnlyList<string> Genres);

public sealed record CodexSurveyImportResult(CharacterProject Characters, IReadOnlyList<ConfirmedTerm> Terms,
    IReadOnlyList<string> Warnings)
{
    /// <summary>Null when Codex wrote no usable book.json; characters and glossary still apply.</summary>
    public CodexBookInfo? Book { get; init; }
    /// <summary>Warnings of the content-safety scan (never a stop: that ends the survey with ContentStopException).</summary>
    public ContentFlagReport ContentFlags { get; init; } = ContentFlagReport.Empty;
}

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
    public const int MaxGenres = 5;

    public static CodexSurveyImportResult Convert(string charactersJson, string glossaryJson, Guid projectId, string projectName,
        string? bookJson = null)
    {
        var bookWarnings = new List<string>();
        CodexBookInfo? book = ConvertBook(bookJson, bookWarnings);
        if (book is not null) projectName = book.VietnameseTitle;
        CodexSurveyImportResult result = ConvertCore(charactersJson, glossaryJson, projectId, projectName);
        return result with { Warnings = [.. bookWarnings, .. result.Warnings], Book = book };
    }

    private static CodexBookInfo? ConvertBook(string? json, List<string> warnings)
    {
        if (json is null) { warnings.Add("Codex chưa tạo book.json: giữ tên truyện gốc, chưa có giới thiệu và thể loại."); return null; }
        CodexBookFile file;
        try { file = Parse<CodexBookFile>(json, "book.json"); }
        catch (InvalidDataException ex) { warnings.Add(ex.Message); return null; }
        string title = Clip(file.VietnameseTitle?.Trim() ?? "", 200);
        if (title.Length == 0) { warnings.Add("book.json thiếu tên tiếng Việt: giữ tên truyện gốc."); return null; }
        var genres = new List<string>();
        foreach (string genre in (file.Genres ?? []).Select(g => g?.Trim() ?? ""))
            if (genre.Length is > 0 and <= 50 && !genres.Contains(genre, StringComparer.CurrentCultureIgnoreCase) && genres.Count < MaxGenres)
                genres.Add(genre);
        return new(title, Clip(file.Synopsis?.Trim() ?? "", 5000), genres);
    }

    private static CodexSurveyImportResult ConvertCore(string charactersJson, string glossaryJson, Guid projectId, string projectName)
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
            if (self.Any(IsHan) || addressee.Any(IsHan))
            {
                // 我/你 tell the translator nothing about the Vietnamese pair; keep the relationship, drop the pronouns.
                warnings.Add($"Bỏ xưng hô “{self}/{addressee}” của “{source.Speaker} → {source.Listener}”: cần từ tiếng Việt.");
                self = addressee = "";
            }
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
            // Terms also drive the post-translation cleanup: a rule must replace Chinese with Vietnamese only.
            if (!chinese.Any(IsHan) || vietnamese.Any(IsHan))
            { warnings.Add($"Bỏ mục từ điển “{chinese} = {vietnamese}”: cần chữ Hán bên trái và chỉ tiếng Việt bên phải."); continue; }
            if (terms.Count >= MaxTerms) { warnings.Add($"Từ điển quá {MaxTerms:N0} mục; phần còn lại bị bỏ qua."); break; }
            terms.Add(new(chinese, vietnamese));
        }
        ReconcileNamesWithGlossary(project, terms, warnings);
        return new(project, terms, warnings);
    }

    /// <summary>
    /// The project store keeps one spelling per Chinese name: a character's name is also its glossary entry and saving
    /// refuses two different ones. Codex may spell a title-like name both ways (覃先生 = "Đàm Tiên Sinh" as a character,
    /// "Đàm tiên sinh" in the glossary): the glossary casing wins, any other difference drops the term, and the result
    /// always fits the store (at most <see cref="MaxTerms"/> terms once every character name is added).
    /// </summary>
    private static void ReconcileNamesWithGlossary(CharacterProject project, List<ConfirmedTerm> terms, List<string> warnings)
    {
        foreach (CharacterProfile profile in project.Profiles.Where(p => p.ChineseName.Length > 0))
        {
            int index = terms.FindIndex(t => t.Chinese == profile.ChineseName);
            if (index < 0 || terms[index].Vietnamese == profile.VietnameseName) continue;
            string spelling = terms[index].Vietnamese;
            // Only a casing difference is the same name (title words lower-case in the glossary). A different word is
            // usually a nickname: the character's name is authoritative and the term goes.
            if (string.Equals(spelling, profile.VietnameseName, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Đổi tên “{profile.VietnameseName}” thành “{spelling}” cho khớp từ điển.");
                profile.VietnameseName = spelling;
            }
            else
            {
                warnings.Add($"Bỏ mục từ điển “{profile.ChineseName} = {spelling}”: trùng tên nhân vật “{profile.VietnameseName}”.");
                terms.RemoveAt(index);
            }
        }
        int namesToAdd = project.Profiles.Count(p => p.ChineseName.Length > 0 && terms.All(t => t.Chinese != p.ChineseName));
        int room = Math.Max(0, MaxTerms - namesToAdd);
        if (terms.Count > room)
        {
            warnings.Add($"Bớt {terms.Count - room} mục từ điển cuối để đủ chỗ cho tên nhân vật.");
            terms.RemoveRange(room, terms.Count - room);
        }
        project.Validate();
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
        catch (JsonException ex)
        {
            // Seen live: a multi-paragraph synopsis written with real line breaks inside the string. The content is fine;
            // only the escaping is wrong, so escape raw control characters inside strings and read it again.
            string escaped = EscapeControlCharactersInStrings(json);
            if (escaped != json)
            {
                try { return JsonSerializer.Deserialize<T>(escaped, Options) ?? throw new InvalidDataException($"{name} trống."); }
                catch (JsonException) { }
            }
            throw new InvalidDataException($"{name} không phải JSON hợp lệ: {ex.Message}", ex);
        }
    }

    internal static string EscapeControlCharactersInStrings(string json)
    {
        var builder = new System.Text.StringBuilder(json.Length + 16);
        bool inString = false, escaped = false;
        foreach (char c in json)
        {
            if (!inString)
            {
                if (c == '"') inString = true;
                builder.Append(c);
                continue;
            }
            if (escaped) { escaped = false; builder.Append(c); continue; }
            switch (c)
            {
                case '\\': escaped = true; builder.Append(c); break;
                case '"': inString = false; builder.Append(c); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case < ' ': builder.Append($"\\u{(int)c:x4}"); break;
                default: builder.Append(c); break;
            }
        }
        return builder.ToString();
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

    private static bool IsHan(char c) => c is >= '㐀' and <= '鿿' or >= '豈' and <= '﫿';

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
