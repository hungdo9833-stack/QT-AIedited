using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QuickTranslator.AiEditor.Core.Translation;

public sealed record OllamaModelPullProgress(string Status, long? CompletedBytes, long? TotalBytes)
{
    public double? Percentage => CompletedBytes is >= 0 && TotalBytes is > 0
        ? Math.Clamp(CompletedBytes.Value * 100d / TotalBytes.Value, 0d, 100d)
        : null;
}

/// <summary>
/// Pulls an Ollama model through the already authenticated SSH loopback tunnel.
/// Ollama's streaming response supplies real byte counts without exposing its port publicly.
/// </summary>
public sealed partial class RemoteOllamaModelPullService
{
    private readonly Func<HttpClient> _clientFactory;

    public RemoteOllamaModelPullService(Func<HttpClient>? clientFactory = null) =>
        _clientFactory = clientFactory ?? CreateLoopbackClient;

    public async Task PullAsync(Uri tunnelEndpoint, string model,
        IProgress<OllamaModelPullProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Uri endpoint = BuildPullEndpoint(tunnelEndpoint);
        ValidateModel(model);
        using HttpClient client = _clientFactory();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(new { name = model, stream = true })
        };
        using HttpResponseMessage response = await client.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        bool completed = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? error = Text(root, "error");
            if (!string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException("Ollama Remote không tải được model: " + Safe(error));
            string status = Text(root, "status") ?? "Đang tải model";
            long? received = Number(root, "completed");
            long? total = Number(root, "total");
            progress?.Report(new(status, received, total));
            completed |= status.Equals("success", StringComparison.OrdinalIgnoreCase);
        }
        if (!completed)
            throw new InvalidOperationException("Ollama Remote kết thúc luồng tải mà không xác nhận thành công.");
    }

    private static HttpClient CreateLoopbackClient() => new(new HttpClientHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static Uri BuildPullEndpoint(Uri tunnelEndpoint)
    {
        ArgumentNullException.ThrowIfNull(tunnelEndpoint);
        if (!tunnelEndpoint.IsAbsoluteUri || tunnelEndpoint.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(tunnelEndpoint.Host, out IPAddress? address) || !IPAddress.IsLoopback(address) ||
            !string.IsNullOrEmpty(tunnelEndpoint.UserInfo))
            throw new ArgumentException("Model chỉ được tải qua SSH loopback HTTP đã xác thực.", nameof(tunnelEndpoint));
        return new UriBuilder(tunnelEndpoint) { Path = "/api/pull", Query = "", Fragment = "" }.Uri;
    }

    private static void ValidateModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model) || !ModelNamePattern().IsMatch(model))
            throw new ArgumentException("Tên model Ollama không hợp lệ.", nameof(model));
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static long? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long number) ? number : null;

    private static string Safe(string value) => value.Length <= 300 ? value : value[..300];

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*(?::[A-Za-z0-9][A-Za-z0-9._-]*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelNamePattern();
}
