namespace QuickTranslator.AiEditor.Core.Reading;

public enum ReaderTheme { Light, Warm, Dark, Vintage }
public enum ReaderPageWidth { Narrow, Comfortable, Wide }
public enum ReaderTextAlignment { Left, Justify }
public enum ReaderDensity { Compact, Standard, Spacious }

public sealed record ReaderThemePalette(string PageBackground, string TextForeground, string ChromeBackground,
    string ControlBackground, string Border, string Warning)
{
    public static ReaderThemePalette For(ReaderTheme theme) => theme switch
    {
        ReaderTheme.Light => new("#FAFAF8", "#292929", "#FAFAF8", "#FFFFFF", "#D9E0E8", "#9A3412"),
        ReaderTheme.Dark => new("#1D2128", "#D7DCE3", "#1D2128", "#2F3642", "#4B5563", "#FDBA74"),
        ReaderTheme.Vintage => new("#E7D3AD", "#55391E", "#E7D3AD", "#F2E2C3", "#B89B70", "#7C2D12"),
        _ => new("#F5EBD1", "#493B27", "#F5EBD1", "#FFF9E9", "#CDBF9F", "#9A3412")
    };
}

public sealed record ReaderDensitySettings(double LineHeight, double ParagraphSpacing, double PageMargin)
{
    public static ReaderDensitySettings For(ReaderDensity density) => density switch
    {
        ReaderDensity.Compact => new(1.35, 2, 20),
        ReaderDensity.Spacious => new(1.75, 14, 48),
        _ => new(1.55, 6, 32)
    };
}

public sealed class ReaderPreferences
{
    public int SchemaVersion { get; set; } = 1;
    public ReaderTheme Theme { get; set; } = ReaderTheme.Warm;
    public ReaderDensity Density { get; set; } = ReaderDensity.Standard;
    public string FontFamily { get; set; } = "Georgia";
    public double FontSize { get; set; } = 20;
    public double LineHeight { get; set; } = 1.55;
    public double ParagraphSpacing { get; set; } = 6;
    public double PageMargin { get; set; } = 32;
    public bool TableOfContentsVisible { get; set; } = true;
    public bool CollapseExtraBlankLinesForDisplay { get; set; } = true;
    public ReaderPageWidth PageWidth { get; set; } = ReaderPageWidth.Comfortable;
    public ReaderTextAlignment TextAlignment { get; set; } = ReaderTextAlignment.Left;
    public double FirstLineIndent { get; set; } = 24;
    public Dictionary<Guid, ReaderChapterPosition> ChapterPositions { get; set; } = [];

    public void Validate()
    {
        if (SchemaVersion != 1 || !Enum.IsDefined(Theme) || !Enum.IsDefined(Density) || !Enum.IsDefined(PageWidth) || !Enum.IsDefined(TextAlignment) ||
            string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 200 ||
            !double.IsFinite(FontSize) || FontSize is < 14 or > 30 ||
            !double.IsFinite(LineHeight) || LineHeight is < 1.3 or > 2.2 ||
            !double.IsFinite(ParagraphSpacing) || ParagraphSpacing is < 0 or > 60 ||
            !double.IsFinite(PageMargin) || PageMargin is < 8 or > 100 ||
            !double.IsFinite(FirstLineIndent) || FirstLineIndent is < 0 or > 80 || ChapterPositions is null ||
            ChapterPositions.Count > 20000 || ChapterPositions.Any(p => p.Key == Guid.Empty || p.Value is null))
            throw new InvalidDataException("Thiết lập Reader không hợp lệ.");
        foreach (var position in ChapterPositions.Values) position.Validate();
    }
}

public sealed class ReaderChapterPosition
{
    public double ScrollOffset { get; set; }
    public int CharacterOffset { get; set; }
    public Guid? SegmentId { get; set; }
    public int? SegmentOffset { get; set; }
    public int? BookmarkOffset { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public void Validate()
    {
        if (!double.IsFinite(ScrollOffset) || ScrollOffset < 0 || CharacterOffset < 0 || SegmentOffset < 0 || BookmarkOffset < 0)
            throw new InvalidDataException("Vị trí đọc không hợp lệ.");
    }
}
