using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuickTranslator.AiEditor.Core.AutoTranslate;

/// <summary>What the content-safety scan found. <see cref="MinorRape"/> stops the book; the other two only warn.</summary>
public enum ContentFlagType { MinorSexual, SevereRape, MinorRape }

public sealed record ContentFlag(ContentFlagType Type, string Chapter, IReadOnlyList<string> Characters, string AgeEvidence,
    string Description)
{
    public string TypeLabel => Type switch
    {
        ContentFlagType.MinorSexual => "Tình dục liên quan trẻ vị thành niên",
        ContentFlagType.SevereRape => "Cưỡng hiếp mức độ nặng",
        _ => "Cưỡng hiếp trẻ vị thành niên (dưới 18 tuổi)"
    };
}

public sealed record ContentFlagReport(IReadOnlyList<ContentFlag> Flags)
{
    public static ContentFlagReport Empty { get; } = new([]);
    public bool HasFlags => Flags.Count > 0;
    /// <summary>
    /// Rape involving a character under 18: the book is not surveyed further nor translated. Also when the scan reported
    /// the two halves separately for the same chapter and character.
    /// </summary>
    public bool MustStop => Flags.Any(flag => flag.Type == ContentFlagType.MinorRape) ||
        Flags.Where(flag => flag.Type == ContentFlagType.MinorSexual).Any(minor =>
            Flags.Any(rape => rape.Type == ContentFlagType.SevereRape && rape.Chapter == minor.Chapter &&
                rape.Characters.Intersect(minor.Characters).Any()));

    public string Describe() => string.Join(Environment.NewLine + Environment.NewLine, Flags.Select(flag =>
        $"• {flag.TypeLabel} — {flag.Chapter}" +
        (flag.Characters.Count > 0 ? $"{Environment.NewLine}  Nhân vật: {string.Join(", ", flag.Characters)}" : "") +
        (flag.AgeEvidence.Length > 0 ? $"{Environment.NewLine}  Căn cứ tuổi: {flag.AgeEvidence}" : "") +
        (flag.Description.Length > 0 ? $"{Environment.NewLine}  {flag.Description}" : "")));
}

/// <summary>Raised when the scan finds rape of a minor: Codex is stopped at once and the book goes no further.</summary>
public sealed class ContentStopException(ContentFlagReport report)
    : InvalidOperationException("Dừng truyện: phát hiện cưỡng hiếp liên quan nhân vật dưới 18 tuổi.")
{
    public ContentFlagReport Report { get; } = report;
}

/// <summary>Reads content-flags.json, which Codex writes first and may still be writing (then it reads as nothing yet).</summary>
public static class ContentFlagFile
{
    public const string FileName = "content-flags.json";

    private sealed class FileModel
    {
        public List<ItemModel> Flags { get; set; } = [];
    }

    private sealed class ItemModel
    {
        public string Type { get; set; } = "";
        public string Chapter { get; set; } = "";
        public List<string> Characters { get; set; } = [];
        public string AgeEvidence { get; set; } = "";
        public string Description { get; set; } = "";
    }

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    /// <summary>Null while the file is missing or not yet complete JSON.</summary>
    public static ContentFlagReport? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        FileModel? file;
        try { file = JsonSerializer.Deserialize<FileModel>(json, Options); }
        catch (JsonException) { return null; }
        if (file is null) return null;
        var flags = new List<ContentFlag>();
        foreach (ItemModel item in file.Flags ?? [])
        {
            if (item is null || ParseType(item.Type) is not ContentFlagType type) continue;
            flags.Add(new(type, Clip(item.Chapter, 200), (item.Characters ?? []).Select(c => Clip(c, 80)).Where(c => c.Length > 0)
                .Distinct().Take(10).ToArray(), Clip(item.AgeEvidence, 300), Clip(item.Description, 600)));
        }
        return new(flags.Take(100).ToArray());
    }

    public static async Task<ContentFlagReport?> TryReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try { return File.Exists(path) ? TryParse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) : null; }
        catch (IOException) { return null; } // Codex is writing it right now.
    }

    private static ContentFlagType? ParseType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "minor" or "minorsexual" => ContentFlagType.MinorSexual,
        "rape" or "severerape" => ContentFlagType.SevereRape,
        "minorrape" => ContentFlagType.MinorRape,
        _ => null
    };

    private static string Clip(string? value, int max)
    {
        string text = value?.Trim() ?? "";
        return text.Length <= max ? text : text[..max];
    }
}
