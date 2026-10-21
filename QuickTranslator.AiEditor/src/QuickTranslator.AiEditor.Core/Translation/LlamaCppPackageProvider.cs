using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed class LlamaCppPackageProvider : ILlamaCppPackageProvider
{
    private readonly HttpClient _http;
    private readonly string _root;

    public LlamaCppPackageProvider(HttpClient? httpClient = null, string? rootFolder = null)
    {
        _http = httpClient ?? new HttpClient();
        _root = Path.GetFullPath(rootFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QTStudio", "llamacpp"));
    }

    public async Task<LlamaCppPackage> EnsureAsync(IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string folder = Path.Combine(_root, LlamaCppRelease.Build);
        string executable = Path.Combine(folder, "llama-server.exe");
        string[] assets = [LlamaCppRelease.ServerZip, LlamaCppRelease.CudaRuntimeZip];
        string[] zips = assets.Select(asset => Path.Combine(folder, asset)).ToArray();
        if (File.Exists(executable) && zips.All(File.Exists))
            return new(LlamaCppRelease.Build, zips, executable);

        Directory.CreateDirectory(folder);
        Dictionary<string, string> digests = await ReadDigestsAsync(cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < assets.Length; index++)
        {
            await DownloadAsync(assets[index], zips[index], progress, cancellationToken).ConfigureAwait(false);
            if (digests.TryGetValue(assets[index], out string? expected))
            {
                try { await VerifyAsync(zips[index], expected, cancellationToken).ConfigureAwait(false); }
                catch
                {
                    File.Delete(zips[index]);
                    throw;
                }
            }
        }

        progress?.Report("Đang giải nén llama.cpp…");
        foreach (string zip in zips)
            ZipFile.ExtractToDirectory(zip, folder, overwriteFiles: true);
        if (!File.Exists(executable))
            throw new InvalidDataException("Gói llama.cpp không chứa llama-server.exe.");
        return new(LlamaCppRelease.Build, zips, executable);
    }

    private async Task<Dictionary<string, string>> ReadDigestsAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/{LlamaCppRelease.Build}");
        request.Headers.UserAgent.ParseAdd("QTStudio/1.0");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!json.RootElement.TryGetProperty("assets", out JsonElement assets)) return result;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out JsonElement name) ||
                !asset.TryGetProperty("digest", out JsonElement digest) || digest.ValueKind != JsonValueKind.String)
                continue;
            string? value = digest.GetString();
            if (value?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true)
                result[name.GetString() ?? ""] = value[7..];
        }
        return result;
    }

    private async Task DownloadAsync(string asset, string path, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        long existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        using var request = new HttpRequestMessage(HttpMethod.Get, LlamaCppRelease.DownloadUri(asset));
        if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        bool resumed = existing > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!resumed) existing = 0;
        long? contentLength = response.Content.Headers.ContentLength;
        long? total = response.Content.Headers.ContentRange?.Length ??
            (contentLength.HasValue ? existing + contentLength.Value : null);
        await using Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write,
            FileShare.Read, 1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[1024 * 128];
        long completed = existing;
        while (true)
        {
            int count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            completed += count;
            progress?.Report(total is long size
                ? $"Đang tải {asset}: {Mb(completed):0.0}/{Mb(size):0.0} MB"
                : $"Đang tải {asset}: {Mb(completed):0.0} MB");
        }
    }

    private static async Task VerifyAsync(string path, string expected, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 128, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        if (!Convert.ToHexString(actual).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Gói {Path.GetFileName(path)} không khớp SHA-256 từ GitHub; hãy tải lại.");
    }

    private static double Mb(long bytes) => bytes / 1024d / 1024d;
}
