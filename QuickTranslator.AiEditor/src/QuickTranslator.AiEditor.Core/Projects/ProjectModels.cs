using QuickTranslator.AiEditor.Core.Models;

namespace QuickTranslator.AiEditor.Core.Projects;

public enum ProjectStatus { Planning, Translating, Editing, Completed, Archived }
public enum ProjectChapterState { Untranslated, Translating, AwaitingReview, NeedsInspection, Approved, Error }

public sealed class ProjectManifest
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Guid ProjectId { get; set; } = Guid.NewGuid();
    public string VietnameseTitle { get; set; } = "";
    public string ChineseTitle { get; set; } = "";
    public string Author { get; set; } = "";
    public List<string> Genres { get; set; } = [];
    public string Description { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string? CoverImagePath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset LastOpenedAt { get; set; }
    public string ApplicationVersion { get; set; } = "";
    public ProjectStatus Status { get; set; } = ProjectStatus.Planning;
    public Guid? CurrentChapterId { get; set; }
    public int ChapterCount { get; set; }
    public int DeletedChapterCount { get; set; }
    public List<ProjectChapterEntry> Chapters { get; set; } = [];
    public ProjectFileLayout Files { get; set; } = new();

    public void Validate()
    {
        if (SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException($"Phiên bản dự án {SchemaVersion} chưa được hỗ trợ.");
        if (ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(VietnameseTitle))
            throw new InvalidDataException("Dự án thiếu ID hoặc tên tiếng Việt.");
        if (Chapters is null || Files is null || Genres is null || Chapters.Any(x => x is null) || !Enum.IsDefined(Status))
            throw new InvalidDataException("Manifest thiếu danh sách chương/đường dẫn hoặc trạng thái không hợp lệ.");
        if (Chapters.Any(x => x.ChapterId == Guid.Empty || x.Number <= 0 || string.IsNullOrWhiteSpace(x.RelativePath)))
            throw new InvalidDataException("Danh sách chương có mục không hợp lệ.");
        if (Chapters.GroupBy(x => x.ChapterId).Any(x => x.Count() > 1) ||
            Chapters.Select(x => x.RelativePath.Replace('\\', '/')).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Chapters.Count ||
            Chapters.Where(x => x.DeletedAt is null).GroupBy(x => x.Number).Any(x => x.Count() > 1))
            throw new InvalidDataException("Dự án có ID hoặc số chương bị trùng.");
        if (ChapterCount != Chapters.Count(x => x.DeletedAt is null) || DeletedChapterCount != Chapters.Count(x => x.DeletedAt is not null))
            throw new InvalidDataException("Số lượng chương trong manifest không khớp danh sách chương.");
        foreach (string path in Files.AllRelativePaths().Concat(Chapters.Select(x => x.RelativePath)))
            ProjectPath.EnsureSafeRelative(path);
        if (CurrentChapterId is Guid current && !Chapters.Any(x => x.ChapterId == current && x.DeletedAt is null))
            throw new InvalidDataException("Chương đang chọn không còn trong dự án.");
    }
}

public sealed class ProjectChapterEntry
{
    public Guid ChapterId { get; set; }
    public int Number { get; set; }
    public string VietnameseTitle { get; set; } = "";
    public string ChineseTitle { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public ProjectChapterState State { get; set; }
    public int SegmentCount { get; set; }
    public int CompletedSegmentCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public sealed class ProjectFileLayout
{
    public string TranslationSettings { get; set; } = "Settings/translation.json";
    public string ReaderSettings { get; set; } = "Settings/reader.json";
    public string ExportSettings { get; set; } = "Settings/export.json";
    public string PromptPresets { get; set; } = "Settings/prompts/presets.qtaip.json";
    public string CharacterProfiles { get; set; } = "Characters/profiles.characters.json";
    public string Glossary { get; set; } = "Glossary/terms.json";
    public string ContextNotes { get; set; } = "Glossary/context.txt";

    internal IEnumerable<string> AllRelativePaths() =>
        [TranslationSettings, ReaderSettings, ExportSettings, PromptPresets, CharacterProfiles, Glossary, ContextNotes];
}

public sealed class ProjectTranslationSettings
{
    public ModelOptions Model { get; set; } = new();
    public SegmenterOptions Segmentation { get; set; } = new();
    public Guid? SelectedPromptPresetId { get; set; }
    public string TraditionalDataDirectory { get; set; } = "";
}

public sealed class ProjectExportSettings
{
    public string FileNameTemplate { get; set; } = "{number:0000} - {title}.txt";
    public bool IncludeChapterTitle { get; set; } = true;
    public bool UseUtf8Bom { get; set; }
}

public sealed class ProjectGlossary
{
    public List<ConfirmedTerm> Terms { get; set; } = [];
}

public sealed record ProjectOpenResult(ProjectSession Session, bool RecoveredFromBackup);

public sealed class ProjectSession
{
    internal SemaphoreSlim OperationGate { get; } = new(1, 1);
    internal SemaphoreSlim ResourceGate { get; } = new(1, 1);
    internal ProjectSession(string manifestPath, ProjectManifest manifest)
    {
        ManifestPath = Path.GetFullPath(manifestPath);
        RootDirectory = Path.GetDirectoryName(ManifestPath)!;
        Manifest = manifest;
        ChapterStore = new(Path.Combine(RootDirectory, "Chapters"));
    }

    public string ManifestPath { get; }
    public string RootDirectory { get; }
    public ProjectManifest Manifest { get; internal set; }
    public Persistence.ChapterFileStore ChapterStore { get; }
    public ProjectRecoveryReport RecoveryReport { get; internal set; } = new([], []);
    public Persistence.RecoveryInfo? ManifestRecovery { get; internal set; }
    public string Resolve(string relativePath) => ProjectPath.Resolve(RootDirectory, relativePath);
}

internal static class ProjectPath
{
    public static void EnsureSafeRelative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
            throw new InvalidDataException("Đường dẫn dự án phải là đường dẫn tương đối.");
        string normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(part => part is ".." or ""))
            throw new InvalidDataException("Đường dẫn dự án không được thoát khỏi thư mục dự án.");
    }

    public static string Resolve(string root, string relative)
    {
        EnsureSafeRelative(relative);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Đường dẫn vượt khỏi thư mục dự án.");
        return full;
    }
}
