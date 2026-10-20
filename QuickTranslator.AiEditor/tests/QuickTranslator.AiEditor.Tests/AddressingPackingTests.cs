using System.Text.Json;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>Requests are packed by token budget, not by a fixed task count; a cut-off reply stays a contained, retryable fault.</summary>
public sealed class AddressingPackingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-packing-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static AddressingTask Character(int n, int sampleLength = 60, int samples = 8) => new($"character:{n:D16}", AddressingTaskKind.Character, "角色" + n, null, 1, 10,
        Enumerable.Range(1, samples).Select(i => new AddressingTaskSample("S" + i, i, i * 10, "", new string('话', sampleLength))).ToArray());
    private static AddressingTask[] Characters(int count, int sampleLength = 60) => Enumerable.Range(1, count).Select(n => Character(n, sampleLength)).ToArray();

    [Fact]
    public void Small_tasks_fill_a_request_up_to_the_task_count_cap_in_order()
    {
        var tasks = Characters(40);
        var requests = AddressingAiSurvey.PlanRequests(tasks, "m");
        Assert.Equal([16, 16, 8], requests.Select(r => r.Tasks.Count));
        Assert.Equal(tasks.Select(t => t.TaskId), requests.SelectMany(r => r.Tasks).Select(t => t.TaskId)); // Order kept, nothing lost or doubled.
        Assert.All(requests, r => Assert.Matches(@"(?m)^TASK T1 ", r.Prompt));
        Assert.Contains("TASK T16 ", requests[0].Prompt); Assert.DoesNotContain("TASK T17 ", requests[0].Prompt);
        Assert.Equal(requests.Count, AddressingAiSurvey.CountRequests(tasks, new()));
    }

    [Fact]
    public void Heavy_tasks_close_a_request_at_the_input_budget_but_one_oversized_task_still_travels_alone()
    {
        var settings = new AddressingSettings(InputTokensPerRequest: 4_000);
        var heavy = Characters(12, sampleLength: 160);
        var requests = AddressingAiSurvey.PlanRequests(heavy, "m", settings);
        Assert.True(requests.Count > 1);
        Assert.All(requests.Where(r => r.Tasks.Count > 1), r => Assert.True(r.EstimatedInputTokens <= 4_000, $"{r.EstimatedInputTokens} tokens for {r.Tasks.Count} tasks"));
        Assert.Equal(heavy.Length, requests.Sum(r => r.Tasks.Count));
        var one = AddressingAiSurvey.PlanRequests(Characters(3), "m", new(InputTokensPerRequest: 1_000));
        Assert.Equal(3, one.Count); Assert.All(one, r => Assert.Single(r.Tasks)); // Over budget even alone: never dropped.
    }

    [Fact]
    public void The_expected_output_budget_follows_the_model_output_limit_so_a_reply_cannot_be_cut_off()
    {
        var tasks = Characters(30);
        var gemini = new ModelOptions { Model = "g", ContextSize = 65536, MaximumOutputTokens = 16384 };
        Assert.All(AddressingAiSurvey.PlanRequests(tasks, "g", null, gemini).Take(1), r => Assert.Equal(16, r.Tasks.Count));
        var small = new ModelOptions { Model = "l", ContextSize = 32768, MaximumOutputTokens = 2_000 }; // 60% = 1200 → 3 tasks of 350.
        var requests = AddressingAiSurvey.PlanRequests(tasks, "l", null, small);
        Assert.All(requests, r => Assert.True(r.Tasks.Count * 350 <= 1_200));
        Assert.Equal(10, requests.Count);
        Assert.Equal(requests.Count, AddressingAiSurvey.CountRequests(tasks, new(), small));
        var tightContext = new ModelOptions { Model = "c", ContextSize = 4_096, MaximumOutputTokens = 1_024 }; // Input must fit beside the output.
        Assert.All(AddressingAiSurvey.PlanRequests(Characters(20), "c", null, tightContext).Where(r => r.Tasks.Count > 1),
            r => Assert.True(r.EstimatedInputTokens + 1_024 <= 4_096));
    }

    [Fact]
    public void A_scene_with_many_quotes_expects_more_output_than_a_character()
    {
        var scene = new AddressingTask("scene:1", AddressingTaskKind.Scene, "场景", null, 1, 1,
            Enumerable.Range(1, 30).Select(i => new AddressingTaskSample("Q" + i, 1, i, "", "好")).ToArray()) { Participants = ["甲", "乙"] };
        var settings = new AddressingSettings(OutputTokensPerRequest: 2_000);
        Assert.Equal(3, AddressingAiSurvey.CountRequests([scene, scene, scene], settings)); // 30 quotes × 40 = 1200 each: two exceed 2000.
        Assert.Equal(1, AddressingAiSurvey.CountRequests([Character(1), Character(2), Character(3)], settings)); // 3 × 350.
    }

    [Fact]
    public void Settings_allow_up_to_24_tasks_per_request()
    {
        new AddressingSettings(TasksPerRequest: 24).Validate();
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddressingSettings(TasksPerRequest: 25).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddressingSettings(InputTokensPerRequest: 10).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new AddressingSettings(OutputTokensPerTask: 1).Validate());
        Assert.Equal(16, new AddressingSettings().TasksPerRequest);
    }

    private sealed class Cut(string text, string? reason = "stop") : IJsonSchemaOllamaClient
    {
        public int Calls { get; private set; }
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
        {
            Calls++; return Task.FromResult(new OllamaCompletion(text, true, reason, new GenerationMetrics { InputTokens = 5000, OutputTokens = 6000 }));
        }
    }

    [Fact]
    public void A_reply_cut_in_the_middle_of_the_json_is_rejected_whole_and_never_yields_a_proposal()
    {
        var tasks = AddressingAiSurvey.PlanTasks(AddressingAiSurveyTests.Sources, AddressingAiSurveyTests.Names);
        var request = AddressingAiSurvey.PlanRequests(tasks, "m").Single();
        string full = AddressingAiSurveyTests.Answer(request).ToJsonString();
        foreach (int keep in new[] { full.Length / 2, full.Length - 1, 10 })
            Assert.Throws<InvalidDataException>(() => AddressingAiSurvey.VerifyReply(full[..keep], request));
    }

    [Theory]
    [InlineData("truncated-json", "stop", "SemanticParseFailure")]
    [InlineData("whole-json", "length", "IncompleteProviderResponse")]
    public async Task A_cut_off_reply_is_a_failed_attempt_the_next_click_asks_again_without_stopping_the_run(string kind, string reason, string code)
    {
        var tasks = AddressingAiSurvey.PlanTasks(AddressingAiSurveyTests.Sources, AddressingAiSurveyTests.Names);
        var session = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), "Synthetic packing");
        var store = new AddressingLedgerStore(session);
        string whole = AddressingAiSurveyTests.Answer(AddressingAiSurvey.PlanRequests(tasks, AddressingAiSurveyTests.Options.Model).Single()).ToJsonString();
        var client = new Cut(kind == "truncated-json" ? whole[..(whole.Length / 2)] : whole, reason);
        var run = await new AddressingAiEngine(client).RunAsync(store, tasks, AddressingAiSurveyTests.Options, 5);
        Assert.Null(run.StoppedBy); // A contained fault: later requests of the run are still tried.
        Assert.Empty(run.State.Proposals);
        var attempt = Assert.Single((await store.LoadAsync()).Attempts);
        Assert.False(attempt.Succeeded); Assert.Equal(code, attempt.ErrorCode);
        // It does not count as an answered attempt: the tasks are still pending and the next click sends them again.
        var state = await store.LoadAsync();
        Assert.All(tasks, t => Assert.False(state.Done(t)));
        await new AddressingAiEngine(client).RunAsync(store, tasks, AddressingAiSurveyTests.Options, 5);
        Assert.Equal(2, client.Calls);
        var after = await store.LoadAsync();
        Assert.All(tasks, t => Assert.False(after.Done(t)));
    }
}
