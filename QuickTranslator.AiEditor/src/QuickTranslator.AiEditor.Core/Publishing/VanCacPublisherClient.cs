using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Publishing;

public enum VanCacChapterStatus { Draft, Published, Hidden }
public enum VanCacPutAction { Created, Updated, Unchanged }

public sealed record VanCacUser(string DisplayName, IReadOnlyList<string> Roles, string? AuthorId);
public sealed record VanCacAuthor(string Id, string Name);
public sealed record VanCacNovel(string Id, string Slug, string Title, string AuthorId, string Author,
    string PublicationStatus, int ChapterCount)
{
    public override string ToString() => $"{Title} · {Author} · {ChapterCount} chương";
}
public sealed record VanCacRemoteChapter(string Id, int Number, string Title, string PublicationStatus);
public sealed record VanCacPutResult(VanCacPutAction Action, string ChapterId, string PublicationStatus);
public sealed record VanCacStatusResult(string Status, int Updated, int SkippedEmpty);
public sealed record VanCacNewNovel(string Title, string Slug, string AuthorId, string Description,
    IReadOnlyList<string> Genres, bool Completed);

/// <summary>Website error. 401/403 mean the token or ownership is no longer valid: stop the whole run.</summary>
public sealed class VanCacApiException(int? statusCode, string message, IReadOnlyDictionary<string, string>? fields = null)
    : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public IReadOnlyDictionary<string, string> Fields { get; } = fields ?? new Dictionary<string, string>();
    public bool IsAuthFailure => StatusCode is 401 or 403;
}

/// <summary>Small surface the publish runner needs; lets tests run without HTTP.</summary>
public interface IVanCacChapterSink
{
    Task<VanCacPutResult> PutChapterAsync(string novelId, int number, string title, string content,
        VanCacChapterStatus? status, CancellationToken cancellationToken);
}

/// <summary>
/// Client for the Văn Các Publisher API v1 (docs/PUBLISHER_API.md in the website repo).
/// The token is only ever sent in the Authorization header: never logged, put in URLs or serialized.
/// Transient failures (network, 429, 5xx) are retried with backoff; PUT is idempotent so retries are safe.
/// </summary>
public sealed class VanCacPublisherClient : IVanCacChapterSink, IDisposable
{
    public const int MaxTitleLength = 200, MaxContentLength = 200_000;
    private const int MaximumAttempts = 4;
    private static readonly Regex TokenPattern = new("^vcp_[a-f0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Uri _apiRoot;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public VanCacPublisherClient(string baseUrl, string token, HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        Uri root = NormalizeBaseUrl(baseUrl);
        if (!IsValidToken(token)) throw new ArgumentException("Mã kết nối phải có dạng vcp_ + 64 ký tự hex.", nameof(token));
        _apiRoot = new Uri(root, "api/publisher/v1/");
        _ownsHttp = true;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("QTStudio-VanCacPublisher/1.0");
        _delay = delay ?? Task.Delay;
    }

    public static bool IsValidToken(string? token) => token is not null && TokenPattern.IsMatch(token);

    /// <summary>HTTPS only, except plain HTTP on loopback for local testing. Returns the origin with a trailing slash.</summary>
    public static Uri NormalizeBaseUrl(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out Uri? uri) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new ArgumentException("Địa chỉ website không hợp lệ.", nameof(value));
        bool loopback = uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback))
            throw new ArgumentException("Website phải dùng HTTPS (chỉ cho phép HTTP với localhost).", nameof(value));
        if (uri.AbsolutePath != "/" || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("Chỉ nhập gốc website, ví dụ https://ten-mien.com (không có đường dẫn).", nameof(value));
        return new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
    }

    public async Task<VanCacUser> GetMeAsync(CancellationToken cancellationToken = default)
        => (await SendAsync<MeResponse>(HttpMethod.Get, "me", null, cancellationToken).ConfigureAwait(false)).User;

    public async Task<IReadOnlyList<VanCacAuthor>> ListAuthorsAsync(CancellationToken cancellationToken = default)
        => (await SendAsync<ItemsResponse<VanCacAuthor>>(HttpMethod.Get, "authors", null, cancellationToken).ConfigureAwait(false)).Items;

    public Task<IReadOnlyList<VanCacNovel>> ListNovelsAsync(CancellationToken cancellationToken = default)
        => ListAllAsync<VanCacNovel>("novels", cancellationToken);

    public Task<IReadOnlyList<VanCacRemoteChapter>> ListChaptersAsync(string novelId, CancellationToken cancellationToken = default)
        => ListAllAsync<VanCacRemoteChapter>($"novels/{Escape(novelId)}/chapters", cancellationToken);

    public async Task<VanCacNovel> CreateNovelAsync(VanCacNewNovel novel, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(novel);
        var body = new
        {
            title = novel.Title.Trim(), slug = novel.Slug, authorId = novel.AuthorId, description = novel.Description.Trim(),
            genres = novel.Genres, coverClass = "cover-forest", completionStatus = novel.Completed ? "completed" : "ongoing",
            publicationStatus = "draft"
        };
        return (await SendAsync<ItemResponse<VanCacNovel>>(HttpMethod.Post, "novels", body, cancellationToken).ConfigureAwait(false)).Item;
    }

    public async Task<VanCacPutResult> PutChapterAsync(string novelId, int number, string title, string content,
        VanCacChapterStatus? status, CancellationToken cancellationToken = default)
    {
        ValidateChapter(number, title, content);
        var body = new Dictionary<string, object> { ["title"] = title.Trim(), ["content"] = content.Trim() };
        if (status is not null) body["status"] = StatusName(status.Value);
        PutResponse result = await SendAsync<PutResponse>(HttpMethod.Put,
            $"novels/{Escape(novelId)}/chapters/{number.ToString(CultureInfo.InvariantCulture)}", body, cancellationToken).ConfigureAwait(false);
        return new(result.Action, result.Item.Id, result.Item.PublicationStatus);
    }

    /// <summary>Changes every chapter of the novel currently in <paramref name="from"/> (e.g. publish all drafts).</summary>
    public Task<VanCacStatusResult> SetStatusFromAsync(string novelId, VanCacChapterStatus status, VanCacChapterStatus from,
        CancellationToken cancellationToken = default)
        => SendAsync<VanCacStatusResult>(HttpMethod.Post, $"novels/{Escape(novelId)}/chapters/status",
            new { status = StatusName(status), fromStatus = StatusName(from) }, cancellationToken);

    /// <summary>Client-side copy of the server limits so an oversize chapter fails fast with a clear message.</summary>
    public static void ValidateChapter(int number, string title, string content)
    {
        if (number is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(number), "Số chương phải từ 1 đến 1.000.000.");
        int titleLength = title?.Trim().Length ?? 0, contentLength = content?.Trim().Length ?? 0;
        if (titleLength is 0 or > MaxTitleLength) throw new ArgumentException($"Tiêu đề chương {number} cần 1–{MaxTitleLength} ký tự.", nameof(title));
        if (contentLength == 0) throw new ArgumentException($"Chương {number} chưa có nội dung.", nameof(content));
        if (contentLength > MaxContentLength) throw new ArgumentException($"Chương {number} dài {contentLength:N0} ký tự, vượt giới hạn {MaxContentLength:N0} của website.", nameof(content));
    }

    public static string StatusName(VanCacChapterStatus status) => status switch
    {
        VanCacChapterStatus.Draft => "draft", VanCacChapterStatus.Published => "published", VanCacChapterStatus.Hidden => "hidden",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private async Task<IReadOnlyList<T>> ListAllAsync<T>(string path, CancellationToken cancellationToken)
    {
        var all = new List<T>();
        for (int page = 1; ; page++)
        {
            PageResponse<T> result = await SendAsync<PageResponse<T>>(HttpMethod.Get, $"{path}?page={page}&pageSize=100", null,
                cancellationToken).ConfigureAwait(false);
            all.AddRange(result.Items);
            if (page >= result.Pagination.TotalPages || result.Items.Count == 0) return all;
        }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        byte[]? payload = body is null ? null : JsonSerializer.SerializeToUtf8Bytes(body, Json);
        for (int attempt = 1; ; attempt++)
        {
            TimeSpan? wait;
            try
            {
                using var request = new HttpRequestMessage(method, new Uri(_apiRoot, path));
                if (payload is not null)
                    request.Content = new ByteArrayContent(payload) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } } };
                using HttpResponseMessage response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                    return JsonSerializer.Deserialize<T>(text, Json) ?? throw new VanCacApiException((int)response.StatusCode, "Website trả về dữ liệu rỗng.");
                int status = (int)response.StatusCode;
                if (status != 429 && status < 500 || attempt >= MaximumAttempts) throw Failure(status, text);
                wait = response.Headers.RetryAfter?.Delta;
            }
            catch (HttpRequestException ex) when (attempt < MaximumAttempts) { wait = null; System.Diagnostics.Trace.TraceWarning($"Văn Các: lỗi mạng, thử lại ({ex.GetType().Name})."); }
            catch (HttpRequestException ex) { throw new VanCacApiException(null, "Không kết nối được website: " + ex.Message); }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < MaximumAttempts) { wait = null; }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new VanCacApiException(null, "Website không phản hồi (quá thời gian chờ)."); }
            TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2 s, 4 s, 8 s
            await _delay(wait is { } retryAfter && retryAfter > backoff ? TimeSpan.FromSeconds(Math.Min(retryAfter.TotalSeconds, 30)) : backoff,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static VanCacApiException Failure(int status, string text)
    {
        try
        {
            ErrorResponse? error = JsonSerializer.Deserialize<ErrorResponse>(text, Json);
            if (error?.Error?.Message is { Length: > 0 } message)
                return new(status, message, error.Error.Fields);
        }
        catch (JsonException) { }
        return new(status, status switch
        {
            401 => "Mã kết nối không hợp lệ, đã hết hạn hoặc bị thu hồi.",
            403 => "Tài khoản không có quyền với truyện này.",
            413 => "Chương quá dài so với giới hạn website.",
            _ => $"Website trả lỗi HTTP {status}."
        });
    }

    private static string Escape(string id) => Uri.EscapeDataString(id ?? throw new ArgumentNullException(nameof(id)));

    public void Dispose() { if (_ownsHttp) _http.Dispose(); }

    private sealed record MeResponse(VanCacUser User);
    private sealed record ItemResponse<T>(T Item);
    private sealed record ItemsResponse<T>(IReadOnlyList<T> Items);
    private sealed record Pagination(int Page, int PageSize, int Total, int TotalPages);
    private sealed record PageResponse<T>(IReadOnlyList<T> Items, Pagination Pagination);
    private sealed record PutItem(string Id, string PublicationStatus);
    private sealed record PutResponse(VanCacPutAction Action, PutItem Item);
    private sealed record ErrorBody(string? Message, Dictionary<string, string>? Fields);
    private sealed record ErrorResponse(ErrorBody? Error);
}

/// <summary>Website slug rule: ^[a-z0-9]+(-[a-z0-9]+)*$, max 120 chars. Vietnamese diacritics are removed.</summary>
public static class VanCacSlug
{
    public static string FromTitle(string? title)
    {
        string text = (title ?? "").Replace('đ', 'd').Replace('Đ', 'D').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(text.Length);
        bool dash = false;
        foreach (char c in text)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            char lower = char.ToLowerInvariant(c);
            if (lower is >= 'a' and <= 'z' or >= '0' and <= '9') { builder.Append(lower); dash = false; }
            else if (!dash && builder.Length > 0) { builder.Append('-'); dash = true; }
        }
        string slug = builder.ToString().Trim('-');
        if (slug.Length > 120) slug = slug[..120].TrimEnd('-');
        return slug;
    }

    public static bool IsValid(string? slug) => slug is { Length: > 0 and <= 120 } && Regex.IsMatch(slug, "^[a-z0-9]+(?:-[a-z0-9]+)*$");
}
