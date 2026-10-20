using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>The one-button survey (scenes → characters and pairs, planned again) with a mock provider only.</summary>
public sealed class AddressingSurveyRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-runner-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    // Romance dialogue the CPU cannot attribute: pairs exist only after the scene tasks are answered.
    private const string Chapter =
        "    宁夕推开门，看到陆霆骁坐在沙发上。\n" +
        "    “你怎么来了？”\n" +
        "    “来看你。”\n" +
        "    “我很好。”\n" +
        "    陆霆骁皱眉：“宁夕，你发烧了。”\n" +
        "    “没有。”\n" +
        "    夜深了，窗外下起了雨。\n" +
        "    宁夕一个人坐在窗前，她想着白天发生的事情。宁夕叹了口气，她很累。宁夕笑了，她还是睡不着。\n" +
        "    陆霆骁站在门外，他没有离开。陆霆骁皱眉，他很担心。\n" +
        "    “宁夕。”\n" +
        "    “嗯？”\n" +
        "    陆霆骁说道：“早点睡。”\n";
    private static readonly Dictionary<string, string> Speaker = new()
    {
        ["你怎么来了？"] = "宁夕", ["来看你。"] = "陆霆骁", ["我很好。"] = "宁夕", ["宁夕，你发烧了。"] = "陆霆骁", ["没有。"] = "宁夕",
        ["宁夕。"] = "陆霆骁", ["嗯？"] = "宁夕", ["早点睡。"] = "陆霆骁"
    };
    private static readonly AddressingSettings Settings = new(TasksPerRequest: 2);
    private static readonly ModelOptions Options = new() { Model = "mock-runner", ContextSize = 32768, MaximumOutputTokens = 4096 };

    /// <summary>A model that reads the prompt it is sent and answers every task correctly, with the short ids.</summary>
    private sealed class Mock(Func<IReadOnlyList<AddressingTask>> candidates) : IJsonSchemaOllamaClient
    {
        public int Calls { get; private set; }
        public Func<int, Exception?> Fail { get; set; } = _ => null;
        public Func<AddressingTask, bool> AnswerBadly { get; set; } = _ => false;
        /// <summary>Identity answers: "same" with this primary and rendering, or null for "different".</summary>
        public Func<AddressingTask, (string Primary, string SecondaryVi)?> Identity { get; set; } = _ => null;
        public List<string> Sent { get; } = [];
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
        {
            Calls++;
            if (Fail(Calls) is { } error) throw error;
            var tasks = AddressingAiSurvey.TasksInPrompt(prompt, candidates());
            Sent.AddRange(tasks.Select(t => t.TaskId));
            JsonArray pairs = [], characters = [], scenes = [], identities = [];
            foreach (var (t, i) in tasks.Select((t, i) => (t, i)))
            {
                string id = "T" + (i + 1);
                var ids = new JsonArray(t.Samples.Select(s => (JsonNode)s.SampleId).ToArray());
                if (t.Kind == AddressingTaskKind.Scene)
                    scenes.Add(new JsonObject { ["task_id"] = id, ["reason"] = "Theo lượt.", ["turns"] = new JsonArray(t.Samples.Select(s =>
                    {
                        string who = s.CpuSpeaker ?? Speaker.GetValueOrDefault(s.Text, "");
                        return (JsonNode)new JsonObject { ["quote_id"] = s.SampleId, ["speaker"] = who, ["listener"] = who.Length == 0 ? "" : who == "宁夕" ? "陆霆骁" : "宁夕",
                            ["self_zh"] = new JsonArray(), ["address_zh"] = new JsonArray() };
                    }).ToArray()) });
                else if (t.Kind == AddressingTaskKind.Pair)
                {
                    JsonObject Direction(string speaker, IReadOnlyList<AddressingTaskSample> samples) => samples.Count == 0
                        ? new JsonObject { ["status"] = "Ambiguous", ["relationship"] = "", ["phases"] = new JsonArray() }
                        : new JsonObject { ["status"] = "Consistent", ["relationship"] = "người yêu", ["phases"] = new JsonArray(new JsonObject
                        {
                            ["from_chapter"] = 1, ["to_chapter"] = 0, ["self_vi"] = AnswerBadly(t) ? "我" : speaker == "宁夕" ? "em" : "anh",
                            ["address_vi"] = speaker == "宁夕" ? "anh" : "em", ["self_zh"] = new JsonArray(), ["address_zh"] = new JsonArray(),
                            ["sample_ids"] = new JsonArray(samples.Select(s => (JsonNode)s.SampleId).ToArray())
                        }) };
                    pairs.Add(new JsonObject { ["task_id"] = id, ["forward"] = Direction(t.Subject, t.Samples), ["reverse"] = Direction(t.Listener!, t.ReverseSamples), ["reason"] = "Mẫu." });
                }
                else if (t.Kind == AddressingTaskKind.Identity)
                    identities.Add(Identity(t) is { } same
                        ? new JsonObject { ["task_id"] = id, ["verdict"] = "same", ["primary"] = same.Primary, ["secondary_vi"] = same.SecondaryVi, ["reason"] = "Biệt danh.",
                            ["sample_ids"] = new JsonArray(t.Samples.Select(s => (JsonNode)s.SampleId).ToArray()) }
                        : new JsonObject { ["task_id"] = id, ["verdict"] = "different", ["primary"] = "", ["secondary_vi"] = "", ["sample_ids"] = new JsonArray(), ["reason"] = "Hai người." });
                else
                    characters.Add(new JsonObject { ["task_id"] = id, ["status"] = "Consistent", ["gender"] = t.SourceGender, ["reason"] = "Mẫu.",
                        ["name_forms"] = new JsonArray(new JsonObject { ["zh"] = t.Subject, ["vi"] = t.Subject == "宁夕" ? "Ninh Tịch" : "Lục Đình Kiêu" }),
                        ["phases"] = new JsonArray(new JsonObject { ["from_chapter"] = 1, ["to_chapter"] = 0, ["narrative_vi"] = t.SourceGender == "female" ? "cô" : "anh",
                            ["secondary_vi"] = "", ["sample_ids"] = ids }) });
            }
            string json = new JsonObject { ["pairs"] = pairs, ["characters"] = characters, ["scenes"] = scenes, ["identities"] = identities }.ToJsonString();
            return Task.FromResult(new OllamaCompletion(json, true, "stop", new GenerationMetrics { InputTokens = 1000, OutputTokens = 300 }));
        }
    }

    private async Task<(ProjectSession Session, CensusSource[] Sources, ProjectResources Resources, Mock Client)> Project(string chapter = Chapter)
    {
        var session = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), "Synthetic survey");
        var sources = Enumerable.Range(1, 3).Select(n => new CensusSource(Guid.NewGuid(), n, chapter)).ToArray();
        var resources = await new ProjectResourceStore(session).LoadAsync();
        var store = new AddressingLedgerStore(session);
        // Like a real model, the mock only sees the prompt; it finds the tasks by replanning against the current ledger.
        var client = new Mock(() =>
        {
            var work = AddressingSurveyRunner.Plan(session.Manifest.ProjectId, sources, resources, store.LoadAsync().GetAwaiter().GetResult(), Settings);
            return [.. work.Scenes, .. work.Tasks];
        });
        return (session, sources, resources, client);
    }

    [Fact]
    public async Task One_click_runs_scenes_then_characters_and_the_pairs_the_scenes_unlocked_then_Complete_writes_them()
    {
        var (session, sources, resources, client) = await Project();
        var before = AddressingSurveyRunner.Plan(session.Manifest.ProjectId, sources, resources, new() { ProjectId = session.Manifest.ProjectId }, Settings);
        Assert.NotEmpty(before.PendingScenes); Assert.Equal(0, before.PendingPairs); // CPU alone: no pair yet.
        var progress = new List<AddressingSurveyProgress>();
        var result = await new AddressingSurveyRunner(client).RunAsync(session, sources, resources, Options, 50, Settings, new SyncProgress(progress));
        Assert.Null(result.StoppedBy); Assert.True(result.Remaining.Complete, $"{result.Remaining.PendingScenes.Count} scenes, {result.Remaining.PendingTasks.Count} tasks left");
        Assert.Equal(client.Calls, result.RequestsSent);
        Assert.Equal(client.Sent.Count, client.Sent.Distinct().Count()); // Nothing was sent twice.
        var ledger = result.State;
        Assert.Contains(ledger.Proposals, p => p.Kind == AddressingTaskKind.Scene && p.EvidenceValid);
        Assert.Contains(ledger.Proposals, p => p.Kind == AddressingTaskKind.Pair && p.Subject == "宁夕" && p.Listener == "陆霆骁" && p.EvidenceValid);
        Assert.Contains(ledger.Proposals, p => p.Kind == AddressingTaskKind.Character && p.Subject == "宁夕" && p.EvidenceValid);
        Assert.Contains(progress, p => p.Stage == "Cảnh"); Assert.Contains(progress, p => p.Stage == "Nhân vật và cặp");
        Assert.Equal(0, (await new AddressingSurveyRunner(client).RunAsync(session, sources, resources, Options, 50, Settings)).RequestsSent); // Done: no call.

        // Survey → "Hoàn thành": the user goes straight to translation.
        var service = new AddressingReviewService(session);
        var done = await service.CompleteAsync(resources.Revision);
        var heroine = done.Resources.Characters.Profiles.Single(p => p.ChineseName == "宁夕");
        Assert.Equal("Ninh Tịch", heroine.VietnameseName); Assert.Equal("cô", heroine.PrimaryNarrativePronoun);
        Assert.Contains(heroine.Relationships, r => r.SelfReference == "em" && r.AddresseeReference == "anh");
        Assert.True(done.Resources.Characters.ConsistencyLockEnabled);
    }

    [Fact]
    public async Task A_pair_the_cpu_already_sees_is_asked_once_after_the_scenes_with_their_turns()
    {
        // One explicit line per chapter makes a CPU-only pair; asking it before the scenes would waste a request on
        // fewer samples and ask it again once the scene turns change its samples.
        var (session, sources, resources, client) = await Project(Chapter + "    宁夕看着陆霆骁说道：“晚安。”\n");
        Assert.Equal(1, AddressingSurveyRunner.Plan(session.Manifest.ProjectId, sources, resources, new() { ProjectId = session.Manifest.ProjectId }, Settings)
            .PendingTasks.Count(t => t.Kind == AddressingTaskKind.Pair && t.Subject == "宁夕"));
        var result = await new AddressingSurveyRunner(client).RunAsync(session, sources, resources, Options, 50, Settings);
        Assert.Null(result.StoppedBy);
        var asked = result.State.Proposals.Where(p => p.Kind == AddressingTaskKind.Pair && p.Subject == "宁夕" && p.Listener == "陆霆骁").ToArray();
        Assert.Single(asked);
        Assert.True(Assert.Single(asked).Phases[0].SampleIds.Count > 3); // Scene turns added samples beyond the 3 explicit lines.
    }

    [Fact]
    public async Task The_request_limit_stops_a_click_and_the_next_click_continues_without_resending()
    {
        var (session, sources, resources, client) = await Project();
        var first = await new AddressingSurveyRunner(client).RunAsync(session, sources, resources, Options, 1, Settings);
        Assert.Equal("RequestBudget", first.StoppedBy); Assert.Equal(1, first.RequestsSent); Assert.False(first.Remaining.Complete);
        Assert.True(first.Remaining.EstimatedRequests > 0);
        AddressingSurveyResult next;
        int clicks = 1;
        do { next = await new AddressingSurveyRunner(client).RunAsync(session, sources, resources, Options, 1, Settings); clicks++; }
        while (next.StoppedBy == "RequestBudget" && clicks < 30);
        Assert.Null(next.StoppedBy); Assert.True(next.Remaining.Complete);
        Assert.Equal(client.Sent.Count, client.Sent.Distinct().Count());
        Assert.Contains(next.State.Proposals, p => p.Kind == AddressingTaskKind.Pair && p.Subject == "宁夕" && p.EvidenceValid);
    }

    [Fact]
    public async Task A_service_error_stops_the_click_with_its_code_and_a_later_click_resumes()
    {
        var (session, sources, resources, client) = await Project();
        client.Fail = n => n == 1 ? new SemanticTransportFailureException(HttpStatusCode.ServiceUnavailable) : null;
        var failed = await new AddressingSurveyRunner(client).RunAsync(session, sources, resources, Options, 50, Settings);
        Assert.Equal("Http503", failed.StoppedBy); Assert.Equal(1, failed.RequestsSent); // No hidden retry.
        var resumed = await new AddressingSurveyRunner(client).RunAsync(session, sources, resources, Options, 50, Settings);
        Assert.Null(resumed.StoppedBy); Assert.True(resumed.Remaining.Complete);
    }

    [Fact]
    public async Task An_invalid_answer_is_asked_again_only_by_the_next_click()
    {
        var (session, sources, resources, client) = await Project();
        client.AnswerBadly = t => t.Kind == AddressingTaskKind.Pair; // "我" in a Vietnamese field: rejected by the verifier.
        var first = await new AddressingSurveyRunner(client).RunAsync(session, sources, resources, Options, 50, Settings);
        Assert.Equal("RetryNextRun", first.StoppedBy); Assert.Equal(client.Sent.Count, client.Sent.Distinct().Count());
        client.AnswerBadly = _ => false;
        var second = await new AddressingSurveyRunner(client).RunAsync(session, sources, resources, Options, 50, Settings);
        Assert.Null(second.StoppedBy); Assert.True(second.Remaining.Complete);
        Assert.Contains(second.State.Proposals, p => p.Kind == AddressingTaskKind.Pair && p.EvidenceValid);
    }

    private sealed class SyncProgress(List<AddressingSurveyProgress> sink) : IProgress<AddressingSurveyProgress>
    {
        public void Report(AddressingSurveyProgress value) { lock (sink) sink.Add(value); }
    }
}
