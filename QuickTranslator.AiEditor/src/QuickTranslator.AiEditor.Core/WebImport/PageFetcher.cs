using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace QuickTranslator.AiEditor.Core.WebImport;

internal sealed class PageFetcher : IDisposable
{
    private readonly WebImportOptions _options;
    private readonly HttpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _lastRequestEnd;

    internal PageFetcher(WebImportOptions options, HttpMessageHandler? handler)
    {
        if (options.DelayBetweenRequests < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaxRetries < 0) throw new ArgumentOutOfRangeException(nameof(options));
        _options = options;
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0.0.0 Safari/537.36");
        _client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh-TW;q=0.9,en;q=0.5");
    }

    // useCache: false for book / directory pages so a later analysis sees newly published chapters.
    internal async Task<WebPage> FetchAsync(Uri url, CancellationToken token, bool useCache = true)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("http" or "https"))
            throw new ArgumentException("Chỉ hỗ trợ đường dẫn HTTP hoặc HTTPS.", nameof(url));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var cachePath = !useCache || _options.CacheDirectory is null ? null : Path.Combine(_options.CacheDirectory,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.AbsoluteUri))) + ".json");
            if (cachePath is not null && File.Exists(cachePath))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<CachedPage>(await File.ReadAllBytesAsync(cachePath, token).ConfigureAwait(false));
                    if (cached is not null)
                    {
                        var page = WebPage.Parse(new Uri(cached.Url), cached.Bytes, cached.Charset);
                        BlockDetector.Check(page);
                        return page;
                    }
                }
                catch (JsonException) { /* Interrupted or damaged cache: download again. */ }
            }

            for (var attempt = 0; ; attempt++)
            {
                if (_lastRequestEnd is long end)
                {
                    var pause = _options.DelayBetweenRequests - Stopwatch.GetElapsedTime(end);
                    if (pause > TimeSpan.Zero) await Task.Delay(pause, token).ConfigureAwait(false);
                }
                TimeSpan retryDelay;
                try
                {
                    using var response = await _client.GetAsync(url, token).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                        throw BlockDetector.Block(url, WebImportBlockReason.NotFound);
                    if ((int)response.StatusCode is 429 or >= 500 && attempt < _options.MaxRetries)
                    {
                        // A challenge is a stop condition, even when delivered as a 503.
                        var errorBytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                        BlockDetector.Check(WebPage.Parse(url, errorBytes, response.Content.Headers.ContentType?.CharSet));
                        retryDelay = Backoff(attempt);
                        var after = response.Headers.RetryAfter;
                        var serverDelay = after?.Delta ?? (after?.Date - DateTimeOffset.UtcNow);
                        if (serverDelay > retryDelay) retryDelay = serverDelay.Value;
                    }
                    else
                    {
                        var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                        var finalUrl = response.RequestMessage?.RequestUri ?? url;
                        var charset = response.Content.Headers.ContentType?.CharSet;
                        var page = WebPage.Parse(finalUrl, bytes, charset);
                        BlockDetector.Check(page);
                        response.EnsureSuccessStatusCode();
                        if (cachePath is not null)
                        {
                            Directory.CreateDirectory(_options.CacheDirectory!);
                            var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                            try
                            {
                                await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(new CachedPage(finalUrl.AbsoluteUri, charset, bytes)), token).ConfigureAwait(false);
                                File.Move(temporary, cachePath, overwrite: true);
                            }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                        return page;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && attempt < _options.MaxRetries)
                { retryDelay = Backoff(attempt); }
                catch (TimeoutException) when (attempt < _options.MaxRetries)
                { retryDelay = Backoff(attempt); }
                catch (HttpRequestException ex) when (ex.StatusCode is null && attempt < _options.MaxRetries)
                { retryDelay = Backoff(attempt); }
                finally { _lastRequestEnd = Stopwatch.GetTimestamp(); }
                await Task.Delay(retryDelay, token).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
    }

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(250 * Math.Pow(2, Math.Min(attempt, 7)));
    private sealed record CachedPage(string Url, string? Charset, byte[] Bytes);
    public void Dispose() { _client.Dispose(); _gate.Dispose(); }
}
