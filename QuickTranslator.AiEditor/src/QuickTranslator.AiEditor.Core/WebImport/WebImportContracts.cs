namespace QuickTranslator.AiEditor.Core.WebImport;

// Contract between the web-import core (structure detection, extraction) and the WPF import window.
// Phase 1 fetches plain HTTP only; pages that need a real browser or a user confirmation raise
// WebImportBlockedException so the UI can explain it instead of importing an empty chapter.

/// <summary>One chapter (or forum post holding one or more chapters) found on the book page, in reading order.</summary>
public sealed record WebChapterLink(int Index, string Title, Uri Url);

/// <summary>Result of analysing a book / thread URL before anything is downloaded.</summary>
public sealed record WebBookAnalysis(
    Uri SourceUrl,
    string? BookTitle,
    IReadOnlyList<WebChapterLink> Chapters,
    string DetectionSummary,
    IReadOnlyList<string> Warnings,
    bool NumberingConflict = false);

/// <summary>Text of one chapter link after joining its sub-pages. A forum post may contain several chapters.</summary>
public sealed record WebChapterContent(WebChapterLink Link, string Title, string Text, int PageCount);

public sealed record WebImportProgress(int Completed, int Total, string? CurrentTitle);

public sealed record WebImportOptions
{
    /// <summary>Pause between two requests to the same site; the importer never runs requests in parallel.</summary>
    public TimeSpan DelayBetweenRequests { get; init; } = TimeSpan.FromSeconds(1.5);
    public int MaxRetries { get; init; } = 3;
    /// <summary>Raw pages are kept here (e.g. the project's Cache folder) so an interrupted import resumes without re-downloading.</summary>
    public string? CacheDirectory { get; init; }
}

public interface IWebNovelImporter
{
    Task<WebBookAnalysis> AnalyzeAsync(Uri url, CancellationToken cancellationToken = default);

    Task<WebChapterContent> FetchChapterAsync(WebChapterLink link, CancellationToken cancellationToken = default);

    IAsyncEnumerable<WebChapterContent> FetchChaptersAsync(IReadOnlyList<WebChapterLink> links,
        IProgress<WebImportProgress>? progress = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// One UTF-8 text in the format the existing TXT import understands: chapters keep their own 第N章 headings;
    /// a chapter without one gets its link title as a heading line. Keep site order renumbers headings by link index.
    /// </summary>
    string ComposeImportText(IReadOnlyList<WebChapterContent> chapters, bool keepSiteOrder = false);
}

public enum WebImportBlockReason { RequiresBrowser, RequiresUserConfirmation, BotChallenge, NotFound, NoChapterList }

/// <summary>The page cannot be read with plain HTTP. Phase 1 reports it; it never tries to get around the block.</summary>
public sealed class WebImportBlockedException(Uri url, WebImportBlockReason reason, string message)
    : Exception(message)
{
    public Uri Url { get; } = url;
    public WebImportBlockReason Reason { get; } = reason;
}
