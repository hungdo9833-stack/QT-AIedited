using System.Net;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class GpuBackendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-m65-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("https://gpu.example.com")]
    [InlineData("https://gpu.example.com:11434/api/chat")]
    [InlineData("http://10.10.0.12:11434")]
    [InlineData("http://192.168.20.3:11434/api/chat")]
    public void Remote_endpoint_accepts_https_or_private_http_and_normalizes_chat_path(string value)
    {
        Uri endpoint = LlamaServerClient.ValidateRemoteEndpoint(new(value));

        Assert.Equal("/api/chat", endpoint.AbsolutePath);
    }

    [Theory]
    [InlineData("http://gpu.example.com:11434")]
    [InlineData("http://8.8.8.8:11434/api/chat")]
    [InlineData("https://user:password@gpu.example.com/api/chat")]
    [InlineData("https://gpu.example.com/api/chat?redirect=true")]
    [InlineData("ftp://gpu.example.com/api/chat")]
    public void Public_http_or_unexpected_remote_endpoint_is_rejected(string value) =>
        Assert.Throws<ArgumentException>(() => LlamaServerClient.ValidateRemoteEndpoint(new(value)));

    [Fact]
    public void Remote_transport_disables_redirects_and_keeps_platform_certificate_validation()
    {
        using SocketsHttpHandler handler = LlamaServerClient.CreateRemoteHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
    }

    [Fact]
    public void Parallel_requests_are_validated_and_cloned()
    {
        var profile = new GpuBackendProfile
        {
            Name = "llama-server", Kind = GpuBackendKind.Remote,
            ParallelRequests = 4, Endpoint = "http://127.0.0.1:11436", Model = "alias"
        };

        profile.Validate();
        Assert.Equal(4, profile.Clone().ParallelRequests);
        profile.ParallelRequests = 0;
        Assert.Throws<InvalidDataException>(profile.Validate);
        profile.ParallelRequests = 9;
        Assert.Throws<InvalidDataException>(profile.Validate);
    }

    /// <summary>A profile file as the Ollama-era app wrote it: the "api" discriminator and the Ollama port.</summary>
    private async Task<string> WriteOllamaEraLocalProfileAsync(string name, string model, Action<AiRuntimeProfile>? runtime = null)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        GpuBackendProfile profile = GpuBackendProfile.CreateDefaultLocal();
        profile.Runtime = AiRuntimeProfile.CreateDefault(GpuBackendProfile.DefaultLocalModel);
        runtime?.Invoke(profile.Runtime);
        await new GpuProfileStore(path, new FakeProtector()).SaveAsync(new GpuProfileCatalog { Profiles = [profile], ActiveProfileId = profile.Id });
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        JsonObject stored = root["profiles"]!.AsArray()[0]!.AsObject();
        stored["name"] = "Ollama Local";
        stored["api"] = "Ollama";
        stored["parallelRequests"] = 1;
        stored["endpoint"] = "http://127.0.0.1:11434/api/chat";
        stored["model"] = model;
        stored["runtime"]!["modelOptions"]!["model"] = model;
        await File.WriteAllTextAsync(path, root.ToJsonString(), new UTF8Encoding(false));
        return path;
    }

    [Fact]
    public async Task Old_ollama_local_profile_is_migrated_once_to_default_gguf()
    {
        string path = await WriteOllamaEraLocalProfileAsync("old-local.json", "translategemma:12b");
        var store = new GpuProfileStore(path, new FakeProtector());

        GpuProfileCatalog migrated = await store.LoadAsync();
        GpuBackendProfile local = Assert.Single(migrated.Profiles);

        Assert.Equal("llama-server Local", local.Name);
        Assert.Equal(LlamaServerClient.DefaultEndpoint.ToString(), local.Endpoint);
        Assert.Equal(GpuBackendProfile.DefaultLocalModel, local.Model);
        Assert.Equal(4, local.ParallelRequests);
        Assert.Equal(8192, local.Runtime!.ModelOptions.ContextSize);
        Assert.Equal(3072, local.Runtime.ModelOptions.MaximumOutputTokens);
        string once = await File.ReadAllTextAsync(path);
        await store.LoadAsync();
        Assert.Equal(once, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Old_ollama_local_profile_with_hf_gguf_tag_keeps_its_model_and_runtime()
    {
        string path = await WriteOllamaEraLocalProfileAsync("old-local-hf.json", "hf.co/IndexTeam/Index-Translate-9B-GGUF:Q4_K_M",
            runtime => runtime.ModelOptions = runtime.ModelOptions with { Temperature = 0.1, MaximumOutputTokens = 2048 });

        GpuBackendProfile local = Assert.Single((await new GpuProfileStore(path, new FakeProtector()).LoadAsync()).Profiles);

        Assert.Equal(4, local.ParallelRequests);
        Assert.Equal("IndexTeam/Index-Translate-9B-GGUF:Q4_K_M", local.Model);
        Assert.Equal(2048, local.Runtime!.ModelOptions.MaximumOutputTokens);
    }

    [Fact]
    public async Task Local_slots_chosen_by_the_user_survive_reload()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "local-slots.json");
        var store = new GpuProfileStore(path, new FakeProtector());
        GpuBackendProfile local = GpuBackendProfile.CreateDefaultLocal();
        local.ParallelRequests = 2;
        await store.SaveAsync(new GpuProfileCatalog { Profiles = [local], ActiveProfileId = local.Id });

        Assert.Equal(2, Assert.Single((await store.LoadAsync()).Profiles).ParallelRequests);
    }

    [Fact]
    public void Factory_creates_llama_server_client_for_remote_profile()
    {
        var profile = new GpuBackendProfile
        {
            Name = "llama-server", Kind = GpuBackendKind.Remote,
            ParallelRequests = 4, Endpoint = "http://127.0.0.1:11436", Model = "alias"
        };

        using ILlmBackendClient client = LlamaServerClient.Create(profile);

        Assert.IsType<LlamaServerClient>(client);
    }

    [Fact]
    public async Task Remote_timeout_or_disconnect_is_reported_without_fallback_request()
    {
        int timeoutCalls = 0;
        using (var timeoutHttp = new HttpClient(new LlamaServerClientTests.FakeHandler(async (_, token) =>
        {
            timeoutCalls++;
            await Task.Delay(Timeout.Infinite, token);
            return LlamaServerClientTests.JsonResponse("{}");
        })) { Timeout = Timeout.InfiniteTimeSpan })
        using (var timeoutClient = new LlamaServerClient(timeoutHttp, new("https://gpu.example.com"), 1))
        {
            LlmRequestException timeout = await Assert.ThrowsAsync<LlmRequestException>(() =>
                timeoutClient.TranslateAsync("private-timeout-prompt", new() { RequestTimeout = TimeSpan.FromMilliseconds(50) }));
            Assert.Equal(LlmErrorKind.Timeout, timeout.Kind);
            Assert.Equal(1, timeoutCalls);
        }

        int disconnectCalls = 0;
        using var disconnectHttp = new HttpClient(new LlamaServerClientTests.FakeHandler((_, _) =>
        {
            disconnectCalls++;
            throw new HttpRequestException("synthetic disconnect");
        }));
        using var disconnectClient = new LlamaServerClient(disconnectHttp, new("https://gpu.example.com"), 1);

        LlmRequestException disconnected = await Assert.ThrowsAsync<LlmRequestException>(() =>
            disconnectClient.TranslateAsync("private-disconnect-prompt", new()));
        Assert.Equal(LlmErrorKind.Connection, disconnected.Kind);
        Assert.Contains("không tự chuyển provider", disconnected.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, disconnectCalls);
    }

    [Fact]
    public async Task Ssh_server_port_keeps_the_persisted_field_name_of_existing_profile_files()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "ssh-port.json");
        var store = new GpuProfileStore(path, new FakeProtector());
        var remote = new GpuBackendProfile
        {
            Id = Guid.NewGuid(), Name = "Rental", Kind = GpuBackendKind.Remote, ParallelRequests = 4,
            Endpoint = "http://127.0.0.1:11436", Model = "IndexTeam/Index-Translate-9B-GGUF:Q4_K_M",
            Ssh = new GpuSshSettings
            {
                Host = "net4.example.com", Port = 48673, Username = "admin", Authentication = SshAuthenticationKind.Password,
                Password = "secret", HostKeyFingerprint = "SHA256:4KYTCoAicuQi0v6wqwqShPdDv0iZskCrZr/8bmX/AUw", ServerPort = 11436
            }
        };
        GpuBackendProfile local = GpuBackendProfile.CreateDefaultLocal();
        await store.SaveAsync(new GpuProfileCatalog { ActiveProfileId = local.Id, Profiles = [local, remote] });

        // Files written before the rename carry "remoteOllamaPort"; the same key must still be read and written.
        Assert.Contains("\"remoteOllamaPort\": 11436", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        Assert.Equal(11436, (await store.LoadAsync()).Profiles.Single(profile => profile.Id == remote.Id).Ssh!.ServerPort);
    }

    [Fact]
    public async Task Profile_store_round_trips_selection_and_never_writes_plain_token()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "ollama-profiles.json");
        var protector = new FakeProtector();
        var store = new GpuProfileStore(path, protector);
        GpuBackendProfile local = GpuBackendProfile.CreateDefaultLocal();
        var remote = new GpuBackendProfile
        {
            Id = Guid.NewGuid(), Name = "EZYCloudX 3090 Ti", Kind = GpuBackendKind.Remote, ParallelRequests = 4,
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
        var catalog = new GpuProfileCatalog { ActiveProfileId = remote.Id, Profiles = [local, remote] };

        await store.SaveAsync(catalog);
        GpuProfileCatalog reopened = await store.LoadAsync();

        Assert.Equal(remote.Id, reopened.ActiveProfileId);
        Assert.Equal("plain-api-token", reopened.Profiles.Single(profile => profile.Id == remote.Id).Token);
        Assert.Equal(4, reopened.Profiles.Single(profile => profile.Id == remote.Id).ParallelRequests);
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
        var store = new GpuProfileStore(Path.Combine(_root, "profiles.json"), new FakeProtector());
        var clients = new Dictionary<Guid, RecordingBackendClient>();
        ILlmBackendClient Factory(GpuBackendProfile profile) => clients[profile.Id] = new(profile.Id);
        using var manager = new GpuBackendManager(store, Factory);
        await manager.LoadAsync();
        GpuBackendProfile local = manager.ActiveProfile;
        var remote = new GpuBackendProfile
        {
            Id = Guid.NewGuid(), Name = "Remote", Kind = GpuBackendKind.Remote,
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
        Guid localId = GpuBackendProfile.DefaultLocalId;
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
            new GpuProfileStore(path, new FakeProtector()).LoadAsync());
    }

    [Fact]
    public async Task Old_profile_file_without_api_fields_uses_compatible_defaults()
    {
        string path = await WriteOllamaEraLocalProfileAsync("old-profiles.json", "hf.co/IndexTeam/Index-Translate-9B-GGUF:Q4_K_M");
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        JsonObject stored = root["profiles"]!.AsArray()[0]!.AsObject();
        stored.Remove("api");
        stored.Remove("parallelRequests");
        await File.WriteAllTextAsync(path, root.ToJsonString(), new UTF8Encoding(false));

        GpuProfileCatalog reopened = await new GpuProfileStore(path, new FakeProtector()).LoadAsync();

        // Before the api field existed the local profile was Ollama on port 11434: it is moved to llama-server.
        Assert.Equal(4, reopened.Profiles[0].ParallelRequests);
        Assert.Equal(LlamaServerClient.DefaultEndpoint.ToString(), reopened.Profiles[0].Endpoint);
    }

    [Fact]
    public async Task Old_profile_file_is_read_once_into_the_new_file_and_kept_as_backup()
    {
        string legacy = await WriteOllamaEraLocalProfileAsync("ollama-profiles.json", "translategemma:12b");
        string before = await File.ReadAllTextAsync(legacy);
        string current = Path.Combine(_root, "gpu-profiles.json");
        var store = new GpuProfileStore(current, new FakeProtector(), legacy);

        GpuBackendProfile local = Assert.Single((await store.LoadAsync()).Profiles);

        Assert.Equal(GpuBackendProfile.DefaultLocalModel, local.Model);
        Assert.True(File.Exists(current));
        Assert.Equal(before, await File.ReadAllTextAsync(legacy));
        Assert.DoesNotContain("\"api\"", await File.ReadAllTextAsync(current), StringComparison.Ordinal);
    }

    [Fact]
    public void Windows_dpapi_round_trip_does_not_contain_plain_secret()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new WindowsDpapiSecretProtector();

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

    private sealed class FakeProtector : ISecretProtector
    {
        public string Protect(string value) => "protected:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        public string Unprotect(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value["protected:".Length..]));
    }

    private sealed class RecordingBackendClient(Guid id) : ILlmBackendClient
    {
        public Guid Id { get; } = id;
        public List<string> Prompts { get; } = [];
        public Task CheckConnectionAsync(string model, TimeSpan timeout, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["translategemma:12b"]);
        public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken cancellationToken = default)
        {
            Prompts.Add(prompt);
            return Task.FromResult(FakeClient.Completion("translated"));
        }
        public void Dispose() { }
    }
}
