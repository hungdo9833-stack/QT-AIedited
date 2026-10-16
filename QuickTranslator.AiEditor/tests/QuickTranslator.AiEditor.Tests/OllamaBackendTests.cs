using System.Net;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class OllamaBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-m65-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("https://gpu.example.com")]
    [InlineData("https://gpu.example.com:11434/api/chat")]
    [InlineData("http://10.10.0.12:11434")]
    [InlineData("http://192.168.20.3:11434/api/chat")]
    public void Remote_endpoint_accepts_https_or_private_http_and_normalizes_chat_path(string value)
    {
        Uri endpoint = OllamaClient.ValidateRemoteEndpoint(new(value));

        Assert.Equal("/api/chat", endpoint.AbsolutePath);
    }

    [Theory]
    [InlineData("http://gpu.example.com:11434")]
    [InlineData("http://8.8.8.8:11434/api/chat")]
    [InlineData("https://user:password@gpu.example.com/api/chat")]
    [InlineData("https://gpu.example.com/api/chat?redirect=true")]
    [InlineData("ftp://gpu.example.com/api/chat")]
    public void Public_http_or_unexpected_remote_endpoint_is_rejected(string value) =>
        Assert.Throws<ArgumentException>(() => OllamaClient.ValidateRemoteEndpoint(new(value)));

    [Fact]
    public void Remote_transport_disables_redirects_and_keeps_platform_certificate_validation()
    {
        using SocketsHttpHandler handler = OllamaClient.CreateRemoteHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public async Task Remote_client_authenticates_tags_and_chat_and_requires_an_installed_non_cloud_model()
    {
        var requests = new List<(string Path, string? Authorization, string? Body)>();
        using var http = new HttpClient(new OllamaClientTests.FakeHandler(async (request, token) =>
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            requests.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(), body));
            return request.RequestUri.AbsolutePath == "/api/tags"
                ? OllamaClientTests.JsonResponse("{\"models\":[{\"name\":\"translategemma:12b\",\"size\":8000000000}]}")
                : OllamaClientTests.JsonResponse("{\"message\":{\"content\":\"Bản dịch remote.\"},\"done\":true}");
        }, autoLocalTags: false)) { Timeout = Timeout.InfiniteTimeSpan };
        using var client = new OllamaClient(http, new("https://gpu.example.com"), OllamaBackendKind.Remote, "secret-token");

        IReadOnlyList<string> models = await client.ListModelsAsync(TimeSpan.FromSeconds(1));
        OllamaCompletion result = await client.TranslateAsync("synthetic-private-prompt", new());

        Assert.Equal(["translategemma:12b"], models);
        Assert.Equal("Bản dịch remote.", result.Text);
        Assert.Equal(["/api/tags", "/api/tags", "/api/chat"], requests.Select(request => request.Path));
        Assert.All(requests, request => Assert.Equal("Bearer secret-token", request.Authorization));
        Assert.Null(requests[0].Body);
        Assert.Null(requests[1].Body);
        Assert.Contains("synthetic-private-prompt", requests[2].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_remote_model_never_sends_prompt()
    {
        int chatCalls = 0;
        using var http = new HttpClient(new OllamaClientTests.FakeHandler((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/tags")
                return Task.FromResult(OllamaClientTests.JsonResponse("{\"models\":[]}"));
            chatCalls++;
            return Task.FromResult(OllamaClientTests.JsonResponse("{}"));
        }, autoLocalTags: false));
        using var client = new OllamaClient(http, new("https://gpu.example.com"), OllamaBackendKind.Remote, null);

        OllamaRequestException error = await Assert.ThrowsAsync<OllamaRequestException>(() =>
            client.TranslateAsync("must-not-leave", new()));

        Assert.Equal(OllamaErrorKind.Http, error.Kind);
        Assert.Equal(0, chatCalls);
    }

    [Fact]
    public async Task Remote_timeout_or_disconnect_is_reported_without_fallback_request()
    {
        int timeoutCalls = 0;
        using (var timeoutHttp = new HttpClient(new OllamaClientTests.FakeHandler(async (_, token) =>
        {
            timeoutCalls++;
            await Task.Delay(Timeout.Infinite, token);
            return OllamaClientTests.JsonResponse("{}");
        }, autoLocalTags: false)) { Timeout = Timeout.InfiniteTimeSpan })
        using (var timeoutClient = new OllamaClient(timeoutHttp, new("https://gpu.example.com"), OllamaBackendKind.Remote))
        {
            OllamaRequestException timeout = await Assert.ThrowsAsync<OllamaRequestException>(() =>
                timeoutClient.TranslateAsync("private-timeout-prompt", new() { RequestTimeout = TimeSpan.FromMilliseconds(50) }));
            Assert.Equal(OllamaErrorKind.Timeout, timeout.Kind);
            Assert.Equal(1, timeoutCalls);
        }

        int disconnectCalls = 0;
        using var disconnectHttp = new HttpClient(new OllamaClientTests.FakeHandler((_, _) =>
        {
            disconnectCalls++;
            throw new HttpRequestException("synthetic disconnect");
        }, autoLocalTags: false));
        using var disconnectClient = new OllamaClient(disconnectHttp, new("https://gpu.example.com"), OllamaBackendKind.Remote);

        OllamaRequestException disconnected = await Assert.ThrowsAsync<OllamaRequestException>(() =>
            disconnectClient.TranslateAsync("private-disconnect-prompt", new()));
        Assert.Equal(OllamaErrorKind.Connection, disconnected.Kind);
        Assert.Contains("không tự chuyển provider", disconnected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, disconnectCalls);
    }

    [Fact]
    public async Task Profile_store_round_trips_selection_and_never_writes_plain_token()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "ollama-profiles.json");
        var protector = new FakeProtector();
        var store = new OllamaProfileStore(path, protector);
        OllamaBackendProfile local = OllamaBackendProfile.CreateDefaultLocal();
        var remote = new OllamaBackendProfile
        {
            Id = Guid.NewGuid(), Name = "EZYCloudX 3090 Ti", Kind = OllamaBackendKind.Remote,
            Endpoint = "https://gpu.example.com", Model = "translategemma:12b",
            Token = "plain-api-token", RemoteDisclosureAccepted = true,
            Runtime = new AiRuntimeProfile
            {
                ModelOptions = new ModelOptions { Model = "translategemma:12b", Temperature = 0.2,
                    ContextSize = 8192, MaximumOutputTokens = 2048, RequestTimeout = TimeSpan.FromSeconds(420) },
                SegmentationOptions = new SegmenterOptions { MinimumHanCharacters = 350,
                    TargetHanCharacters = 550, MaximumHanCharacters = 750, MaximumTextElements = 3000 }
            }
        };
        var catalog = new OllamaProfileCatalog { ActiveProfileId = remote.Id, Profiles = [local, remote] };

        await store.SaveAsync(catalog);
        OllamaProfileCatalog reopened = await store.LoadAsync();

        Assert.Equal(remote.Id, reopened.ActiveProfileId);
        Assert.Equal("plain-api-token", reopened.Profiles.Single(profile => profile.Id == remote.Id).Token);
        AiRuntimeProfile runtime = reopened.Profiles.Single(profile => profile.Id == remote.Id).Runtime!;
        Assert.Equal(8192, runtime.ModelOptions.ContextSize);
        Assert.Equal(550, runtime.SegmentationOptions.TargetHanCharacters);
        string persisted = await File.ReadAllTextAsync(path, Encoding.UTF8);
        Assert.DoesNotContain("plain-api-token", persisted, StringComparison.Ordinal);
        Assert.Contains("protected:", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Backend_manager_switches_profiles_without_sending_remote_content_before_consent()
    {
        Directory.CreateDirectory(_root);
        var store = new OllamaProfileStore(Path.Combine(_root, "profiles.json"), new FakeProtector());
        var clients = new Dictionary<Guid, RecordingBackendClient>();
        IOllamaBackendClient Factory(OllamaBackendProfile profile) => clients[profile.Id] = new(profile.Id);
        using var manager = new OllamaBackendManager(store, Factory);
        await manager.LoadAsync();
        OllamaBackendProfile local = manager.ActiveProfile;
        var remote = new OllamaBackendProfile
        {
            Id = Guid.NewGuid(), Name = "Remote", Kind = OllamaBackendKind.Remote,
            Endpoint = "https://gpu.example.com", Model = "translategemma:12b", Token = "token"
        };
        await manager.SaveProfileAsync(remote, activate: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ActivateAsync(remote.Id));
        Assert.Equal(local.Id, manager.ActiveProfile.Id);
        Assert.Empty(clients[local.Id].Prompts);

        remote.RemoteDisclosureAccepted = true;
        await manager.SaveProfileAsync(remote, activate: true);
        await manager.TranslateAsync("remote prompt", new());
        await manager.ActivateAsync(local.Id);
        await manager.TranslateAsync("local prompt", new());

        Assert.Equal(["remote prompt"], clients[remote.Id].Prompts);
        Assert.Equal(["local prompt"], clients[local.Id].Prompts);
        Assert.Equal(local.Id, manager.ActiveProfile.Id);
    }

    [Fact]
    public async Task Profile_without_runtime_settings_is_rejected_by_current_schema()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "incomplete-profiles.json");
        Guid localId = OllamaBackendProfile.DefaultLocalId;
        await File.WriteAllTextAsync(path, $$"""
        {
          "schemaVersion": 1,
          "activeProfileId": "{{localId}}",
          "profiles": [
            {
              "id": "{{localId}}",
              "name": "Ollama Local",
              "kind": "Local",
              "endpoint": "http://127.0.0.1:11434/api/chat",
              "model": "model:7b",
              "remoteDisclosureAccepted": false
            }
          ]
        }
        """, new UTF8Encoding(false));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new OllamaProfileStore(path, new FakeProtector()).LoadAsync());
    }

    [Fact]
    public void Windows_dpapi_round_trip_does_not_contain_plain_secret()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new WindowsDpapiOllamaSecretProtector();

        string encrypted;
        try
        {
            encrypted = protector.Protect("m6.5-secret-token");
        }
        catch (CryptographicException error) when (error.InnerException is Win32Exception { NativeErrorCode: 2 })
        {
            // The isolated test runner has no loaded Windows user profile. A normal desktop
            // process has one; keep the native DPAPI behavior and cover persisted shape with
            // Profile_store_round_trips_selection_and_never_writes_plain_token above.
            return;
        }

        Assert.DoesNotContain("m6.5-secret-token", encrypted, StringComparison.Ordinal);
        Assert.Equal("m6.5-secret-token", protector.Unprotect(encrypted));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class FakeProtector : IOllamaSecretProtector
    {
        public string Protect(string value) => "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value["protected:".Length..]));
    }

    private sealed class RecordingBackendClient(Guid id) : IOllamaBackendClient
    {
        public Guid Id { get; } = id;
        public List<string> Prompts { get; } = [];
        public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["translategemma:12b"]);
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(FakeClient.Completion("translated"));
        }
        public void Dispose() { }
    }
}
