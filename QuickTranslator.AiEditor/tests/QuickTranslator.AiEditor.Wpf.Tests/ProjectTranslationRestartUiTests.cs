using System.IO;
using System.Security.Cryptography;
using System.Text;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Persistence;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Tests;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class ProjectTranslationRestartUiTests
{
    [Fact]
    public Task Paused_local_and_remote_queue_reopens_with_pinned_workers_progress_models_and_verified_remote() =>
        StaHost.Run(async () =>
        {
            RestartScenario scenario = await CreateScenarioAsync();
            var ssh = new RecordingSshService();
            var remoteClient = new BackendClient("remote resumed");
            await using var connections = new GpuConnectionManager(ssh, (_, _) => remoteClient,
                healthInterval: TimeSpan.FromHours(1));
            OllamaBackendManager backends = await LoadBackendsAsync(scenario.Root);
            using var fixture = new WorkspaceFixture(scenario.Root, backends, connections);
            await fixture.OpenProjectAsync();
            fixture.Call("InitializeProjectTranslationCoordinator", fixture.Project);
            ProjectTranslationCoordinator loaded = await fixture.TaskCall<ProjectTranslationCoordinator>(
                "EnsureProjectTranslationCoordinatorAsync");
            ProjectTranslationJob before = Assert.IsType<ProjectTranslationJob>(loaded.CurrentJob);

            ProjectTranslationCoordinator resumed = await fixture.TaskCall<ProjectTranslationCoordinator>(
                "RefreshProjectTranslationCoordinatorAsync", loaded);
            ProjectTranslationJob restored = Assert.IsType<ProjectTranslationJob>(resumed.CurrentJob);

            Assert.Equal(scenario.JobId, restored.JobId);
            Assert.Equal(scenario.WorkerIds, restored.Workers.Select(worker => worker.WorkerId));
            Assert.Equal(scenario.Models, restored.Workers.Select(worker => worker.ModelOptions.Model));
            Assert.Equal(scenario.CompletedSegments, restored.Items.Sum(item => item.CompletedSegments));
            Assert.Equal(scenario.RemoteFingerprint, Assert.Single(ssh.Settings).HostKeyFingerprint);
            Assert.Equal(before.Revision, restored.Revision);

            await resumed.ResumeAsync();

            Assert.Equal(ProjectTranslationJobStatus.Completed, resumed.CurrentJob!.Status);
            Assert.All(resumed.CurrentJob.Items, item => Assert.Equal(item.TotalSegments, item.CompletedSegments));
            Assert.Equal(scenario.WorkerIds, resumed.CurrentJob.Workers.Select(worker => worker.WorkerId));
            Assert.Equal(scenario.Models, resumed.CurrentJob.Workers.Select(worker => worker.ModelOptions.Model));
            Assert.True(fixture.Client.Calls + remoteClient.Calls >= 2);
        });

    [Fact]
    public Task Offline_remote_blocks_resume_with_clear_error_without_swapping_worker_or_losing_progress() =>
        StaHost.Run(async () =>
        {
            RestartScenario scenario = await CreateScenarioAsync();
            var ssh = new OfflineSshService();
            await using var connections = new GpuConnectionManager(ssh, (_, _) => new BackendClient("unused"),
                healthInterval: TimeSpan.FromHours(1));
            OllamaBackendManager backends = await LoadBackendsAsync(scenario.Root);
            using var fixture = new WorkspaceFixture(scenario.Root, backends, connections);
            await fixture.OpenProjectAsync();
            fixture.Call("InitializeProjectTranslationCoordinator", fixture.Project);
            ProjectTranslationCoordinator loaded = await fixture.TaskCall<ProjectTranslationCoordinator>(
                "EnsureProjectTranslationCoordinatorAsync");

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.TaskCall<ProjectTranslationCoordinator>("RefreshProjectTranslationCoordinatorAsync", loaded));

            Assert.Contains("Không thể khôi phục GPU Remote", error.Message, StringComparison.Ordinal);
            Assert.Contains("Job vẫn được giữ nguyên", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, ssh.Calls);
            await AssertJobUnchangedAsync(fixture.Project, scenario);
        });

    [Fact]
    public Task Changed_ssh_fingerprint_blocks_before_connection_without_swapping_worker_or_losing_progress() =>
        StaHost.Run(async () =>
        {
            RestartScenario scenario = await CreateScenarioAsync();
            OllamaBackendManager backends = await LoadBackendsAsync(scenario.Root);
            OllamaBackendProfile changed = backends.GetProfile(scenario.RemoteProfileId);
            changed.Ssh!.HostKeyFingerprint = Fingerprint('B');
            await backends.SaveProfileAsync(changed, activate: false);
            var ssh = new RecordingSshService();
            await using var connections = new GpuConnectionManager(ssh, (_, _) => new BackendClient("unused"),
                healthInterval: TimeSpan.FromHours(1));
            using var fixture = new WorkspaceFixture(scenario.Root, backends, connections);
            await fixture.OpenProjectAsync();
            fixture.Call("InitializeProjectTranslationCoordinator", fixture.Project);
            ProjectTranslationCoordinator loaded = await fixture.TaskCall<ProjectTranslationCoordinator>(
                "EnsureProjectTranslationCoordinatorAsync");

            InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                fixture.TaskCall<ProjectTranslationCoordinator>("RefreshProjectTranslationCoordinatorAsync", loaded));

            Assert.Contains("Danh tính SSH", error.Message, StringComparison.Ordinal);
            Assert.Contains("đã đổi", error.Message, StringComparison.Ordinal);
            Assert.Empty(ssh.Settings);
            await AssertJobUnchangedAsync(fixture.Project, scenario);
        });

    private static async Task<RestartScenario> CreateScenarioAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "qtaie-safety-wpf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var repository = new ProjectRepository();
        ProjectSession session = await repository.CreateAsync(Path.Combine(root, "Novel"), "Restart synthetic");
        var chapterIds = new List<Guid>();
        for (int number = 1; number <= 4; number++)
        {
            ChapterDocument chapter = TranslationService.CreateChapter($"Chương {number}", $"天地玄黄宇宙洪荒第{number}章。");
            chapterIds.Add((await repository.AddChapterAsync(session, chapter, number)).ChapterId);
        }

        const string localModel = "translategemma:12b";
        const string remoteModel = "index-translate:9b";
        string fingerprint = Fingerprint('A');
        OllamaBackendProfile remote = RemoteProfile(remoteModel, fingerprint);
        await using (var profileScope = new AsyncBackendScope(await LoadBackendsAsync(root)))
            await profileScope.Manager.SaveProfileAsync(remote, activate: false);

        ProjectResources resources = await new ProjectResourceStore(session).LoadAsync();
        PromptPreset preset = PromptPreset.Default;
        ModelOptions localOptions = new() { Model = localModel, ContextSize = 4096, MaximumOutputTokens = 1024 };
        ModelOptions remoteOptions = new() { Model = remoteModel, ContextSize = 8192, MaximumOutputTokens = 2048 };
        SegmenterOptions localSegments = new() { MinimumHanCharacters = 100, TargetHanCharacters = 200,
            MaximumHanCharacters = 300, MaximumTextElements = 2000 };
        SegmenterOptions remoteSegments = new() { MinimumHanCharacters = 300, TargetHanCharacters = 500,
            MaximumHanCharacters = 700, MaximumTextElements = 4000 };
        Guid localWorker = Guid.NewGuid();
        Guid remoteWorker = Guid.NewGuid();
        var workers = new[]
        {
            new ProjectTranslationWorkerDefinition(localWorker,
                OllamaBackendSnapshot.LocalDefault(localModel), new FakeClient(), localOptions, localSegments,
                BackendIdentity: "local:" + OllamaBackendProfile.DefaultLocalId.ToString("N")),
            new ProjectTranslationWorkerDefinition(remoteWorker,
                new(remote.Id, OllamaBackendKind.Remote, remote.Name, remote.Endpoint, remoteModel),
                new FakeClient(), remoteOptions, remoteSegments, BackendIdentity: BackendIdentity(remote))
        };
        Task<ProjectTranslationEnvironment> Load(CancellationToken _) => Task.FromResult(
            new ProjectTranslationEnvironment(localOptions with { }, preset with { }, resources.Clone(),
                OllamaBackendSnapshot.LocalDefault(localModel)));
        var coordinator = new ProjectTranslationCoordinator(session, repository, workers, Load);
        ProjectTranslationJob ready = await new ProjectTranslationJobStore(session)
            .SaveNewAsync(await coordinator.PlanAsync(chapterIds));
        ready.Status = ProjectTranslationJobStatus.Paused;
        ready.Workers[0].Status = ProjectTranslationWorkerStatus.Paused;
        ready.Workers[0].CompletedChapters = 1;
        ready.Workers[0].CompletedHanCharacters = 180;
        ready.Workers[1].Status = ProjectTranslationWorkerStatus.Paused;
        ready.Workers[1].CompletedHanCharacters = 95;
        ProjectTranslationItem completed = ready.Items[0];
        completed.Status = ProjectTranslationItemStatus.Completed;
        completed.Disposition = ProjectTranslationItemDisposition.TranslatedThisJob;
        completed.AssignedWorkerId = localWorker;
        completed.CompletedSegments = completed.TotalSegments;
        completed.WorkerMetricsRecorded = true;
        ready = await new ProjectTranslationJobStore(session).SaveAsync(ready);
        return new(root, ready.JobId, remote.Id, fingerprint,
            ready.Workers.Select(worker => worker.WorkerId).ToArray(),
            ready.Workers.Select(worker => worker.ModelOptions.Model).ToArray(),
            ready.Items.Sum(item => item.CompletedSegments), ready.Revision);
    }

    private static async Task AssertJobUnchangedAsync(ProjectSession session, RestartScenario expected)
    {
        ProjectTranslationJob actual = Assert.IsType<ProjectTranslationJob>(
            await new ProjectTranslationJobStore(session).LoadAsync());
        Assert.Equal(expected.JobId, actual.JobId);
        Assert.Equal(expected.Revision, actual.Revision);
        Assert.Equal(ProjectTranslationJobStatus.Paused, actual.Status);
        Assert.Equal(expected.WorkerIds, actual.Workers.Select(worker => worker.WorkerId));
        Assert.Equal(expected.Models, actual.Workers.Select(worker => worker.ModelOptions.Model));
        Assert.Equal(expected.CompletedSegments, actual.Items.Sum(item => item.CompletedSegments));
    }

    private static OllamaBackendProfile RemoteProfile(string model, string fingerprint) => new()
    {
        Id = Guid.NewGuid(),
        Name = "GPU Remote verified",
        Kind = OllamaBackendKind.Remote,
        Endpoint = "https://gpu.example.test/api/chat",
        Model = model,
        Runtime = new AiRuntimeProfile
        {
            ModelOptions = new() { Model = model, ContextSize = 8192, MaximumOutputTokens = 2048 },
            SegmentationOptions = new() { MinimumHanCharacters = 300, TargetHanCharacters = 500,
                MaximumHanCharacters = 700, MaximumTextElements = 4000 }
        },
        RemoteDisclosureAccepted = true,
        Ssh = new()
        {
            Host = "gpu.example.test", Port = 22, Username = "qtstudio",
            Authentication = SshAuthenticationKind.Password, Password = "synthetic-only",
            HostKeyFingerprint = fingerprint, RemoteOllamaPort = 11434
        }
    };

    private static async Task<OllamaBackendManager> LoadBackendsAsync(string root)
    {
        var manager = new OllamaBackendManager(new OllamaProfileStore(
            Path.Combine(root, "app", "ollama-profiles.json"), new PlainProtector()));
        await manager.LoadAsync();
        return manager;
    }

    private static string BackendIdentity(OllamaBackendProfile profile)
    {
        GpuSshSettings ssh = profile.Ssh!;
        string value = $"{profile.Id:N}|{ssh.Host.ToLowerInvariant()}|{ssh.Port}|{ssh.Username}|{ssh.RemoteOllamaPort}|{ssh.HostKeyFingerprint}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string Fingerprint(char value) => "SHA256:" + new string(value, 43);

    private sealed record RestartScenario(string Root, Guid JobId, Guid RemoteProfileId,
        string RemoteFingerprint, Guid[] WorkerIds, string[] Models, int CompletedSegments, long Revision);

    private sealed class PlainProtector : IOllamaSecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string value) => value;
    }

    private sealed class AsyncBackendScope(OllamaBackendManager manager) : IAsyncDisposable
    {
        public OllamaBackendManager Manager { get; } = manager;
        public ValueTask DisposeAsync() { Manager.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class RecordingSshService : ISshConnectionService
    {
        public List<GpuSshSettings> Settings { get; } = [];
        public Task<ISshSession> ConnectAsync(GpuSshSettings settings, CancellationToken cancellationToken = default)
        {
            Settings.Add(settings.Clone());
            return Task.FromResult<ISshSession>(new SshSession());
        }
    }

    private sealed class OfflineSshService : ISshConnectionService
    {
        public int Calls { get; private set; }
        public Task<ISshSession> ConnectAsync(GpuSshSettings settings, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<ISshSession>(new IOException("synthetic remote offline"));
        }
    }

    private sealed class SshSession : ISshSession
    {
        public bool IsConnected { get; private set; } = true;
        public Task<SshCommandResult> ExecuteAsync(SshOperation operation, string? argument = null,
            CancellationToken cancellationToken = default) => Task.FromResult(new SshCommandResult(0, "", ""));
        public Task UploadFileAsync(string localPath, string remotePath, IProgress<long>? progress = null,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<ISshTunnel> OpenOllamaTunnelAsync(int remotePort,
            CancellationToken cancellationToken = default) => Task.FromResult<ISshTunnel>(new SshTunnel());
        public ValueTask DisposeAsync() { IsConnected = false; return ValueTask.CompletedTask; }
    }

    private sealed class SshTunnel : ISshTunnel
    {
        public int LocalPort => 43117;
        public bool IsStarted { get; private set; } = true;
        public ValueTask DisposeAsync() { IsStarted = false; return ValueTask.CompletedTask; }
    }

    private sealed class BackendClient(string response) : IOllamaBackendClient
    {
        public int Calls { get; private set; }
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(FakeClient.Completion(response));
        }
        public Task CheckConnectionAsync(string model, TimeSpan timeout,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<string>> ListModelsAsync(TimeSpan timeout,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public void Dispose() { }
    }
}
