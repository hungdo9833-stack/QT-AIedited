using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Core.Publishing;

public sealed record VanCacPublishOptions(string NovelId, VanCacChapterStatus? Status, bool AllowIncomplete);

public sealed record VanCacPublishProgress(int Done, int Total, int Number, string Title, string Outcome);

public sealed record VanCacChapterFailure(int Number, string Title, string Message);

public sealed record VanCacPublishReport(
    int Created, int Updated, int Unchanged, int SkippedEmpty, int SkippedIncomplete,
    IReadOnlyList<VanCacChapterFailure> Failures, string? AbortReason, bool Cancelled)
{
    public int Sent => Created + Updated + Unchanged;
    public bool Completed => AbortReason is null && !Cancelled;
}

/// <summary>
/// Uploads chapters to Sắc Viện one request at a time, in reading order. Chapter text comes from the caller
/// (ProjectPublicationService.ReadChaptersAsync, i.e. ChapterTranslationComposer), so the website receives
/// exactly what Reader and TXT export show. The Project is never written.
/// Per-chapter errors are recorded and the run continues; 401/403 stop the run because no later chapter can
/// succeed. Cancellation stops before the next chapter; the server PUT is idempotent, so re-running is safe.
/// </summary>
public static class VanCacPublishRunner
{
    private static readonly Regex NumberedTitle = new(@"^\s*(?:Chương|Chuong|Chapter|第)\s*0*(\d+)\s*(?:章)?\s*[:：.\-–—·]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The website renders "Chương N: title"; drop a leading "Chương N" that repeats the same number.</summary>
    public static string WebTitle(int number, string? title)
    {
        string text = (title ?? "").Trim();
        Match match = NumberedTitle.Match(text);
        if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed) && parsed == number)
            text = text[match.Length..].Trim();
        if (text.Length == 0) text = $"Chương {number}";
        return text.Length > VanCacPublisherClient.MaxTitleLength ? text[..VanCacPublisherClient.MaxTitleLength].TrimEnd() : text;
    }

    public static async Task<VanCacPublishReport> RunAsync(IAsyncEnumerable<ProjectPublicationChapter> chapters, int total,
        IVanCacChapterSink sink, VanCacPublishOptions options, IProgress<VanCacPublishProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        int created = 0, updated = 0, unchanged = 0, skippedEmpty = 0, skippedIncomplete = 0, done = 0;
        var failures = new List<VanCacChapterFailure>();
        string? abort = null;
        bool cancelled = false;
        try
        {
            await foreach (ProjectPublicationChapter chapter in chapters.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string title = WebTitle(chapter.Number, chapter.Title), outcome;
                if (!options.AllowIncomplete && (!chapter.IsComplete || chapter.NeedsInspection)) { skippedIncomplete++; outcome = "bỏ qua (chưa hoàn tất/cần kiểm tra)"; }
                else if (string.IsNullOrWhiteSpace(chapter.Text)) { skippedEmpty++; outcome = "bỏ qua (chưa có bản dịch)"; }
                else
                {
                    try
                    {
                        VanCacPutResult result = await sink.PutChapterAsync(options.NovelId, chapter.Number, title, chapter.Text,
                            options.Status, cancellationToken).ConfigureAwait(false);
                        switch (result.Action)
                        {
                            case VanCacPutAction.Created: created++; outcome = "đã tạo"; break;
                            case VanCacPutAction.Updated: updated++; outcome = "đã cập nhật"; break;
                            default: unchanged++; outcome = "không đổi"; break;
                        }
                    }
                    catch (VanCacApiException ex) when (ex.IsAuthFailure) { abort = ex.Message; failures.Add(new(chapter.Number, title, ex.Message)); break; }
                    catch (Exception ex) when (ex is VanCacApiException or ArgumentException)
                    {
                        failures.Add(new(chapter.Number, title, ex.Message)); outcome = "lỗi: " + ex.Message;
                    }
                }
                progress?.Report(new(++done, total, chapter.Number, title, outcome));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { cancelled = true; }
        catch (InvalidOperationException) when (abort is null)
        {
            // ReadChaptersAsync throws when the Project changes mid-run (queue, Editor). Chapters sent so far stay valid.
            abort = "Project đã thay đổi trong lúc đăng (hàng đợi dịch hoặc Editor). Các chương đã gửi vẫn giữ trên website; hãy kiểm tra lại rồi đăng tiếp, chương trùng sẽ báo \"không đổi\".";
        }
        return new(created, updated, unchanged, skippedEmpty, skippedIncomplete, failures, abort, cancelled);
    }
}
