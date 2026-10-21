using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class LlamaCppPackageProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qt-llamacpp-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Downloads_verifies_extracts_and_second_call_is_offline()
    {
        byte[] server = Zip("llama-server.exe", "server");
        byte[] cuda = Zip("cudart64_12.dll", "cuda");
        int calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            if (request.RequestUri!.Host == "api.github.com")
                return Json(Release(server, cuda));
            return Bytes(request.RequestUri.AbsolutePath.EndsWith(LlamaCppRelease.ServerZip) ? server : cuda);
        }));
        var provider = new LlamaCppPackageProvider(http, _root);

        LlamaCppPackage package = await provider.EnsureAsync();
        int firstCalls = calls;
        LlamaCppPackage again = await provider.EnsureAsync();

        Assert.True(File.Exists(package.ServerExecutable));
        Assert.All(package.ZipPaths, path => Assert.True(File.Exists(path)));
        Assert.Equal(firstCalls, calls);
        Assert.Equal(package.Build, again.Build);
        Assert.Equal(package.ServerExecutable, again.ServerExecutable);
        Assert.Equal(package.ZipPaths, again.ZipPaths);
    }

    [Fact]
    public async Task Digest_mismatch_is_rejected()
    {
        byte[] server = Zip("llama-server.exe", "server");
        byte[] cuda = Zip("cudart64_12.dll", "cuda");
        using var http = new HttpClient(new Handler(request => request.RequestUri!.Host == "api.github.com"
            ? Json(Release(server, cuda, badServerDigest: true))
            : Bytes(request.RequestUri.AbsolutePath.EndsWith(LlamaCppRelease.ServerZip) ? server : cuda)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LlamaCppPackageProvider(http, _root).EnsureAsync());
    }

    [Fact]
    public async Task Existing_partial_zip_is_resumed_with_range()
    {
        byte[] server = Zip("llama-server.exe", "server-content");
        byte[] cuda = Zip("cudart64_12.dll", "cuda");
        string folder = Path.Combine(_root, LlamaCppRelease.Build);
        Directory.CreateDirectory(folder);
        int prefix = server.Length / 2;
        await File.WriteAllBytesAsync(Path.Combine(folder, LlamaCppRelease.ServerZip), server[..prefix]);
        bool ranged = false;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.Host == "api.github.com") return Json(Release(server, cuda));
            if (request.RequestUri.AbsolutePath.EndsWith(LlamaCppRelease.ServerZip))
            {
                ranged = request.Headers.Range?.Ranges.Single().From == prefix;
                var response = Bytes(server[prefix..], HttpStatusCode.PartialContent);
                response.Content.Headers.ContentRange = new(prefix, server.Length - 1, server.Length);
                return response;
            }
            return Bytes(cuda);
        }));

        await new LlamaCppPackageProvider(http, _root).EnsureAsync();

        Assert.True(ranged);
        Assert.Equal(server, await File.ReadAllBytesAsync(Path.Combine(folder, LlamaCppRelease.ServerZip)));
    }

    private static string Release(byte[] server, byte[] cuda, bool badServerDigest = false) => $$"""
        {"assets":[
          {"name":"{{LlamaCppRelease.ServerZip}}","digest":"sha256:{{(badServerDigest ? new string('0', 64) : Hash(server))}}"},
          {"name":"{{LlamaCppRelease.CudaRuntimeZip}}","digest":"sha256:{{Hash(cuda)}}"}
        ]}
        """;
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    private static byte[] Zip(string name, string content)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        using (StreamWriter writer = new(archive.CreateEntry(name).Open())) writer.Write(content);
        return output.ToArray();
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Bytes(byte[] value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
        { Content = new ByteArrayContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(respond(request));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
