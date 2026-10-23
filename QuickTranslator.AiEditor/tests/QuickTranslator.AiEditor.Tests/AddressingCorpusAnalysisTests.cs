using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using Xunit.Abstractions;

namespace QuickTranslator.AiEditor.Tests;

// Share the existing romance and hard-wrapped xianxia fixtures and their verified quote truth.
public sealed partial class AddressingSceneTests
{
    private readonly ITestOutputHelper _corpusOutput;
    public AddressingSceneTests(ITestOutputHelper output) => _corpusOutput = output;

    private static readonly AddressingSettings CorpusSettings = new(TasksPerRequest: 2);
    private static readonly ModelOptions CorpusOptions = new() { Model = "mock-corpus", ContextSize = 32768, MaximumOutputTokens = 4096 };

    private static (CensusSource[] Sources, string[] Names, IReadOnlyDictionary<string, (string Speaker, string Listener)> Truth) CorpusFixture(string fixture) => fixture switch
    {
        "romance" => (Book(Romance, 3), Romantic, RomanceTruth),
        "xianxia" => (Book(Xianxia, 3), Cultivators, XianxiaTruth),
        "variants" => (AddressingAiSurveyTests.Sources, AddressingAiSurveyTests.Names, new Dictionary<string, (string, string)>()),
        // Same mute-child identity fixture as AddressingIdentityTests, with no dialogue between the nicknames.
        "identity" => (Book("    宁夕看着床上的小宝，小包子睡得正香。\n    陆霆骁抱起小宝，小包子揉了揉眼睛。\n" +
            "    宁夕看着陆霆骁说道：“你来了。”\n    陆霆骁看着宁夕说道：“嗯。”\n", 3),
            ["宁夕", "陆霆骁", "小宝", "小包子"], new Dictionary<string, (string, string)>()),
        "historical" => (Book("    上官婉儿看着欧阳锋。王氏坐在门口。\n    「陛下来了。」\n    「走吧。」\n" +
            "    上官婉儿看着欧阳锋说道：「在下告辞。」\n", 3), ["上官婉儿", "欧阳锋", "王氏"],
            new Dictionary<string, (string, string)> { ["陛下来了。"] = ("上官婉儿", "欧阳锋"), ["走吧。"] = ("欧阳锋", "上官婉儿"), ["在下告辞。"] = ("上官婉儿", "欧阳锋") }),
        _ => throw new ArgumentOutOfRangeException(nameof(fixture))
    };

    // The pre-F08 path: each planner independently rebuilds variants and extracts the corpus.
    private static AddressingSurveyWork UnsharedPlan(Guid projectId, IReadOnlyList<CensusSource> sources, ProjectResources resources,
        AddressingLedgerState state, AddressingSettings settings)
    {
        var (names, titles) = AddressingAiSurvey.SurveyNames(projectId, sources, resources, 3, settings.FilterJunkCandidates);
        var scenes = AddressingAiSurvey.PlanScenes(sources, names, settings, titleForms: titles);
        var turns = state.SceneTurns();
        return new(scenes, [.. AddressingAiSurvey.PlanTasks(sources, names, settings, sceneTurns: turns),
            .. AddressingAiSurvey.PlanIdentities(sources, names, settings, sceneTurns: turns)], state, settings);
    }

    private static void SameWork(AddressingSurveyWork expected, AddressingSurveyWork actual)
    {
        // Serialize the complete task graph: ids, ordering, samples, offsets, variants, titles, attribution and gender.
        Assert.Equal(JsonSerializer.Serialize(expected.Scenes), JsonSerializer.Serialize(actual.Scenes));
        Assert.Equal(JsonSerializer.Serialize(expected.Tasks), JsonSerializer.Serialize(actual.Tasks));
        Assert.Equal(expected.PendingScenes.Select(t => t.TaskId), actual.PendingScenes.Select(t => t.TaskId));
        Assert.Equal(expected.PendingTasks.Select(t => t.TaskId), actual.PendingTasks.Select(t => t.TaskId));
        Assert.Equal(expected.EstimateRequests(CorpusOptions), actual.EstimateRequests(CorpusOptions));
        foreach (var (left, right) in new[] { (expected.Scenes, actual.Scenes), (expected.Tasks, actual.Tasks) })
            Assert.Equal(JsonSerializer.Serialize(AddressingAiSurvey.PlanRequests(left, CorpusOptions.Model, expected.Settings, CorpusOptions)),
                JsonSerializer.Serialize(AddressingAiSurvey.PlanRequests(right, CorpusOptions.Model, actual.Settings, CorpusOptions)));
    }

    private sealed class CorpusClient(Func<AddressingSurveyWork> work, IReadOnlyDictionary<string, (string Speaker, string Listener)> truth,
        string outcome = "valid") : IJsonSchemaLlmClient
    {
        public List<string> Prompts { get; } = [];
        public Task<LlmCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LlmCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
        {
            Prompts.Add(prompt);
            if (outcome == "outage" && Prompts.Count == 2) throw new SemanticTransportFailureException(HttpStatusCode.ServiceUnavailable);
            if (outcome == "refusal") throw new GeminiSurveyBlockedException("SAFETY");
            var current = work();
            var tasks = AddressingAiSurvey.TasksInPrompt(prompt, [.. current.Scenes, .. current.Tasks]);
            var request = new AddressingRequestPlan("reply", tasks, prompt, 0);
            var reply = AddressingAiSurveyTests.Answer(request);
            reply["scenes"] = SceneAnswer(request, truth)["scenes"]!.DeepClone();
            foreach (var node in reply["characters"]!.AsArray())
            {
                var task = tasks.Single(t => t.TaskId == node!["task_id"]!.GetValue<string>());
                node!["gender"] = task.SourceGender;
            }
            foreach (var node in reply["pairs"]!.AsArray())
                foreach (var direction in new[] { "forward", "reverse" })
                    foreach (var phase in node![direction]!["phases"]!.AsArray())
                    {
                        phase!["self_zh"] = new JsonArray(); phase["address_zh"] = new JsonArray();
                        phase["self_vi"] = outcome == "invalid" ? "我" : "tôi";
                        phase["address_vi"] = "bạn";
                    }
            foreach (var node in reply["identities"]!.AsArray()) node!["verdict"] = "different";
            return Task.FromResult(new LlmCompletion(reply.ToJsonString(), true, "stop", new GenerationMetrics { InputTokens = 1000, OutputTokens = 300 }));
        }
    }

    [Theory]
    [InlineData("romance", 50, "valid")]
    [InlineData("xianxia", 50, "valid")]
    [InlineData("variants", 50, "valid")]
    [InlineData("identity", 50, "valid")]
    [InlineData("historical", 50, "valid")]
    [InlineData("romance", 1, "valid")]
    [InlineData("romance", 50, "invalid")]
    [InlineData("romance", 50, "outage")]
    [InlineData("romance", 50, "refusal")]
    public async Task Shared_corpus_matches_unshared_plans_prompts_verified_results_and_transport_counts(string fixture, int budget, string outcome)
    {
        var (sources, names, truth) = CorpusFixture(fixture);
        var session = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), fixture);
        var resources = await new ProjectResourceStore(session).LoadAsync();
        foreach (string name in names) resources.Characters.Profiles.Add(new CharacterProfile { ChineseName = name });
        var corpus = new AddressingAiSurvey.CorpusAnalysis(session.Manifest.ProjectId, sources, resources, CorpusSettings, default);
        AddressingSurveyWork? active = null;
        int plans = 0;
        AddressingSurveyWork Shared(AddressingLedgerState state)
        {
            plans++;
            var old = UnsharedPlan(session.Manifest.ProjectId, sources, resources, state, CorpusSettings);
            active = corpus.Plan(state);
            SameWork(old, active);
            return active;
        }
        var sharedClient = new CorpusClient(() => active!, truth, outcome);
        var shared = await new AddressingSurveyRunner(sharedClient).RunPlannedAsync(session, CorpusOptions, budget, CorpusSettings, Shared);
        Assert.True(plans >= 2); // Includes the final remaining-work calculation.
        Assert.Equal(1, corpus.DialogueExtractions);
        Assert.Equal(1, corpus.SceneExtractions);
        Assert.Equal(sharedClient.Prompts.Count, shared.RequestsSent);

        var baselineSession = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), fixture);
        AddressingSurveyWork? baselineWork = null;
        var baselineClient = new CorpusClient(() => baselineWork!, truth, outcome);
        var baseline = await new AddressingSurveyRunner(baselineClient).RunPlannedAsync(baselineSession, CorpusOptions, budget, CorpusSettings,
            state => baselineWork = UnsharedPlan(session.Manifest.ProjectId, sources, resources, state, CorpusSettings));
        Assert.Equal(baselineClient.Prompts, sharedClient.Prompts);
        Assert.Equal(baseline.RequestsSent, shared.RequestsSent);
        Assert.Equal(baseline.StoppedBy, shared.StoppedBy);
        Assert.Equal(JsonSerializer.Serialize(baseline.State.Proposals), JsonSerializer.Serialize(shared.State.Proposals));
        Assert.Equal(JsonSerializer.Serialize(baseline.State.Attempts.Select(a => a with { Id = Guid.Empty, At = default })),
            JsonSerializer.Serialize(shared.State.Attempts.Select(a => a with { Id = Guid.Empty, At = default })));
        SameWork(baseline.Remaining, shared.Remaining);

        // Exercise the public run entry point too: it must create exactly this shared planning path.
        var publicSession = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), fixture);
        var store = new AddressingLedgerStore(publicSession);
        var publicResources = await new ProjectResourceStore(publicSession).LoadAsync();
        publicResources.Characters.Profiles.AddRange(resources.Characters.Profiles);
        var publicClient = new CorpusClient(() => UnsharedPlan(publicSession.Manifest.ProjectId, sources, publicResources,
            store.LoadAsync().GetAwaiter().GetResult(), CorpusSettings), truth, outcome);
        var publicRun = await new AddressingSurveyRunner(publicClient).RunAsync(publicSession, sources, publicResources, CorpusOptions, budget, CorpusSettings);
        Assert.Equal(sharedClient.Prompts, publicClient.Prompts);
        Assert.Equal(shared.RequestsSent, publicRun.RequestsSent);
        Assert.Equal(shared.StoppedBy, publicRun.StoppedBy);
        Assert.Equal(JsonSerializer.Serialize(shared.State.Proposals), JsonSerializer.Serialize(publicRun.State.Proposals));
        Assert.InRange(publicRun.RequestsSent, 0, budget);
        _corpusOutput.WriteLine($"{fixture}/{outcome}/budget={budget}: {plans} plans; base dialogue extractions {2 * plans} -> {corpus.DialogueExtractions}; " +
            $"scene extractions {plans} -> {corpus.SceneExtractions}; requests {baseline.RequestsSent} = {shared.RequestsSent} = {publicRun.RequestsSent}");
        if (outcome == "valid" && budget == 50)
        {
            Assert.True(shared.Remaining.Complete);
            Assert.NotEmpty(shared.State.Proposals);
            Assert.All(shared.State.Proposals.Where(p => p.Kind != AddressingTaskKind.Identity), p => Assert.True(p.EvidenceValid, string.Join(",", p.Issues)));
        }
    }

    [Fact]
    public void Only_attribution_changes_rebuild_projections_and_latest_ledger_always_controls_pending_work()
    {
        var sources = Book(Romance, 3);
        var corpus = new AddressingAiSurvey.CorpusAnalysis(sources, (Romantic, Array.Empty<string>()), CorpusSettings);
        var state = new AddressingLedgerState();
        var first = corpus.Plan(state);
        var request = AddressingAiSurvey.PlanRequests(first.Scenes, CorpusOptions.Model, CorpusSettings).First();
        state.Proposals.AddRange(AddressingAiSurvey.Verify(SceneAnswer(request, RomanceTruth).ToJsonString(), request));
        var attributed = corpus.Plan(state);
        Assert.Same(first.Scenes, attributed.Scenes);
        Assert.NotSame(first.Tasks, attributed.Tasks);
        Assert.Equal(2, corpus.ProjectionBuilds);
        var reloaded = JsonSerializer.Deserialize<AddressingLedgerState>(JsonSerializer.Serialize(state))!;
        Assert.Same(attributed.Tasks, corpus.Plan(reloaded).Tasks);
        Assert.Equal(2, corpus.ProjectionBuilds);
        Assert.Equal(1, corpus.NarrationExtractions);
        state.Proposals.Clear(); // Removal/rejection must also invalidate the attributed projections.
        SameWork(first, corpus.Plan(state));
        Assert.Equal(3, corpus.ProjectionBuilds);
        Assert.Equal(1, corpus.DialogueExtractions);
        Assert.Equal(1, corpus.SceneExtractions);
    }

    [Fact]
    public void Newly_verified_title_people_reanalyze_overlapping_narration_names_once_per_name_set()
    {
        // The longer title masks the short candidate in narration; appending independent title counts would change tasks.
        var sources = Book("    宁夕看着陆老夫人。\n    陆老夫人坐在窗前，她笑了。\n    “你来了。”\n    “好。”\n", 3);
        string[] names = ["宁夕", "陆老"];
        string[] titles = ["陆老夫人"];
        var corpus = new AddressingAiSurvey.CorpusAnalysis(sources, (names, titles), CorpusSettings);
        var state = new AddressingLedgerState();
        AddressingSurveyWork Old() => new(AddressingAiSurvey.PlanScenes(sources, names, CorpusSettings, titleForms: titles),
            [.. AddressingAiSurvey.PlanTasks(sources, names, CorpusSettings, sceneTurns: state.SceneTurns()),
                .. AddressingAiSurvey.PlanIdentities(sources, names, CorpusSettings, sceneTurns: state.SceneTurns())], state, CorpusSettings);
        var before = corpus.Plan(state);
        SameWork(Old(), before);
        var request = AddressingAiSurvey.PlanRequests(before.Scenes, CorpusOptions.Model, CorpusSettings).First();
        Assert.Contains(request.Tasks.SelectMany(t => t.TitleParticipants), t => t == "陆老夫人");
        var truth = new Dictionary<string, (string, string)> { ["你来了。"] = ("陆老夫人", "宁夕"), ["好。"] = ("宁夕", "陆老夫人") };
        state.Proposals.AddRange(AddressingAiSurvey.Verify(SceneAnswer(request, truth).ToJsonString(), request));
        var after = corpus.Plan(state);
        SameWork(Old(), after);
        Assert.Contains(after.Tasks, t => t.Kind == AddressingTaskKind.Character && t.Subject == "陆老夫人");
        Assert.Equal(2, corpus.NarrationExtractions);
        Assert.Same(after.Tasks, corpus.Plan(state).Tasks);
        Assert.Equal(2, corpus.NarrationExtractions);
        state.Proposals.Clear();
        SameWork(Old(), corpus.Plan(state));
        Assert.Equal(2, corpus.NarrationExtractions); // Return to the cached original name set.
        Assert.Equal(1, corpus.DialogueExtractions);
        Assert.Equal(1, corpus.SceneExtractions);
    }
}
