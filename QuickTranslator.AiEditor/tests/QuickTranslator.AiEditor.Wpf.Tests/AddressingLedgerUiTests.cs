using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;
using QuickTranslator.AiEditor.Wpf;
using Xunit;

namespace QuickTranslator.AiEditor.Wpf.Tests;

public sealed class AddressingLedgerUiTests
{
    private const string Block =
        "    王秀兰看着林青说道：「林青，姐姐给你做饭。」\n" +
        "    林青看着王秀兰说道：「秀兰姐，我不饿。」\n" +
        "    王秀兰点点头，她转身走了。小兰真是好人，林青心想，他笑了。\n";

    /// <summary>Mock provider reply for exactly the tasks the window will re-plan (no AI involved).</summary>
    private static string Reply(AddressingRequestPlan request)
    {
        var pairs = new JsonArray(); var characters = new JsonArray();
        foreach (var t in request.Tasks)
        {
            var ids = new JsonArray(t.Samples.Select(s => (JsonNode)s.SampleId).ToArray());
            if (t.Kind == AddressingTaskKind.Pair)
            {
                // Both directions in one answer, consistent with each other: chị/em one way, em/chị back.
                static JsonObject Direction(IReadOnlyList<AddressingTaskSample> samples, string self, string address) => samples.Count == 0
                    ? new JsonObject { ["status"] = "Ambiguous", ["relationship"] = "", ["phases"] = new JsonArray() }
                    : new JsonObject { ["status"] = "Consistent", ["relationship"] = "chị em", ["phases"] = new JsonArray(new JsonObject { ["from_chapter"] = 1, ["to_chapter"] = 0,
                        ["self_vi"] = self, ["address_vi"] = address, ["self_zh"] = new JsonArray(), ["address_zh"] = new JsonArray(),
                        ["sample_ids"] = new JsonArray(samples.Select(s => (JsonNode)s.SampleId).ToArray()) }) };
                pairs.Add(new JsonObject { ["task_id"] = t.TaskId, ["forward"] = Direction(t.Samples, "chị", "em"), ["reverse"] = Direction(t.ReverseSamples, "em", "chị"), ["reason"] = "Mock" });
            }
            else
            {
                bool female = t.SourceGender == "female";
                characters.Add(new JsonObject { ["task_id"] = t.TaskId, ["status"] = "Consistent", ["gender"] = t.SourceGender,
                    ["name_forms"] = new JsonArray(t.NameCandidates.Select(v => (JsonNode)new JsonObject { ["zh"] = v.Form, ["vi"] = "Tú Lan" })
                        .Append(new JsonObject { ["zh"] = t.Subject, ["vi"] = t.Subject == "王秀兰" ? "Vương Tú Lan" : t.Subject == "林青" ? "Lâm Thanh" : "Nhân vật" }).ToArray()),
                    ["phases"] = new JsonArray(new JsonObject { ["from_chapter"] = 1, ["to_chapter"] = 0, ["narrative_vi"] = female ? "nàng" : "hắn",
                        ["secondary_vi"] = "", ["sample_ids"] = ids }), ["reason"] = "Mock" });
            }
        }
        return new JsonObject { ["pairs"] = pairs, ["characters"] = characters, ["scenes"] = new JsonArray(), ["identities"] = new JsonArray() }.ToJsonString();
    }

    [Fact]
    public Task Review_window_accepts_characters_then_pair_writes_profiles_and_lock_is_a_separate_switch() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync();
        for (int n = 1; n <= 2; n++)
            await fixture.Repository.AddChapterAsync(fixture.Project, TranslationService.CreateChapter("Chương " + n, string.Concat(Enumerable.Repeat(Block, 3))), n);
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(fixture.Project, fixture.Repository);
        var resources = await new ProjectResourceStore(fixture.Project).LoadAsync();
        var tasks = AddressingAiSurvey.PlanTasks(sources, AddressingAiSurvey.CandidateNames(fixture.Project.Manifest.ProjectId, sources, resources));
        var request = AddressingAiSurvey.PlanRequests(tasks, "mock").Single();
        var proposals = AddressingAiSurvey.Verify(Reply(request), request);
        await new AddressingLedgerStore(fixture.Project).UpdateAsync(s => s.Proposals.AddRange(proposals));

        ProjectResources? applied = null;
        var window = new AddressingLedgerWindow(fixture.Project, fixture.Repository, r => applied = r);
        try
        {
            await window.LoadAsync(); Assert.Null(window.LastError);
            Assert.All(window.State.Proposals, p => Assert.True(p.EvidenceValid, string.Join(",", p.Issues)));
            // A pair cannot be written before both characters have profiles.
            window.SelectRow(AddressingTaskKind.Pair, "王秀兰"); window.SetPairInputs("chị", "em");
            await window.AcceptAsync(); Assert.Contains("duyệt nhân vật", window.LastError);

            window.SelectRow(AddressingTaskKind.Character, "王秀兰");
            window.SetCharacterInputs("Vương Tú Lan", "nàng", "小兰=Tiểu Lan\n秀兰姐=Tú Lan tỷ");
            await window.AcceptAsync(); Assert.Null(window.LastError); Assert.NotNull(applied);
            window.SelectRow(AddressingTaskKind.Character, "林青"); window.SetCharacterInputs("Lâm Thanh", "hắn", "");
            await window.AcceptAsync(); Assert.Null(window.LastError);
            window.SelectRow(AddressingTaskKind.Pair, "王秀兰"); window.SetPairInputs("chị", "em");
            await window.AcceptAsync(); Assert.Null(window.LastError);

            var saved = await new ProjectResourceStore(fixture.Project).LoadAsync();
            var sister = saved.Characters.Profiles.Single(p => p.ChineseName == "王秀兰");
            Assert.Equal("nàng", sister.PrimaryNarrativePronoun); Assert.True(sister.NarrativePronounLocked);
            Assert.Contains("小兰", sister.Aliases); Assert.Contains(saved.Terms, t => t.Chinese == "小兰" && t.Vietnamese == "Tiểu Lan");
            var rule = Assert.Single(sister.Relationships); Assert.Equal("chị", rule.SelfReference); Assert.True(rule.Locked);
            Assert.False(saved.Characters.ConsistencyLockEnabled); Assert.Equal(saved.Revision, applied!.Revision);

            await window.SetLockAsync(true); Assert.Null(window.LastError);
            Assert.True((await new ProjectResourceStore(fixture.Project).LoadAsync()).Characters.ConsistencyLockEnabled);

            window.SelectRow(AddressingTaskKind.Pair, "林青"); await window.DecideAsync(AddressingDecision.Rejected);
            Assert.Equal(AddressingDecision.Rejected, window.State.LatestReview(window.Selected!.Id)!.Decision);
            Assert.Equal(0, fixture.Client.Calls); // The review window never calls an AI provider.
        }
        finally { window.Close(); }
    });

    /// <summary>A provider stand-in that reads the prompt it is sent (tasks found by their prompt block) and answers them.</summary>
    private sealed class PromptMock(Func<IReadOnlyList<AddressingTask>> candidates) : IJsonSchemaOllamaClient
    {
        public int Calls { get; private set; }
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, System.Text.Json.JsonElement schema, CancellationToken ct = default)
        {
            Calls++;
            var request = AddressingAiSurvey.PlanRequests(AddressingAiSurvey.TasksInPrompt(prompt, candidates()), "mock").Single();
            return Task.FromResult(new OllamaCompletion(Reply(request), true, "stop", new GenerationMetrics()));
        }
    }

    [Fact]
    public Task Survey_button_asks_first_runs_the_pipeline_into_the_project_ledger_then_Complete_writes_it() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync();
        for (int n = 1; n <= 2; n++)
            await fixture.Repository.AddChapterAsync(fixture.Project, TranslationService.CreateChapter("Chương " + n, string.Concat(Enumerable.Repeat(Block, 3))), n);
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(fixture.Project, fixture.Repository);
        var resources = await new ProjectResourceStore(fixture.Project).LoadAsync();
        var store = new AddressingLedgerStore(fixture.Project);
        var client = new PromptMock(() =>
        {
            var work = AddressingSurveyRunner.Plan(fixture.Project.Manifest.ProjectId, sources, resources, store.LoadAsync().GetAwaiter().GetResult());
            return [.. work.Scenes, .. work.Tasks];
        });
        var window = new AddressingLedgerWindow(fixture.Project, fixture.Repository, _ => { }, []) { BackendFactory = (_, _, _) => client };
        try
        {
            await window.LoadAsync(); Assert.Null(window.LastError);
            Assert.False(window.Work!.Complete); Assert.Contains("Còn", window.SurveyStatus);
            Assert.Empty(window.Preview!.Applied); // Nothing surveyed yet: nothing to complete.

            window.ConfirmSend = _ => false; window.SetSurveyInputs(0, "gemini-3.6-flash", 5);
            await window.SurveyAsync();
            Assert.Equal(0, client.Calls); // Declined: no source text leaves the computer.

            string? asked = null; window.ConfirmSend = m => { asked = m; return true; };
            await window.SurveyAsync(); Assert.Null(window.LastError);
            Assert.Contains("gemini-3.6-flash", asked); Assert.Contains("tối đa 5 request", asked, StringComparison.OrdinalIgnoreCase);
            Assert.True(client.Calls >= 1); Assert.Null(window.LastSurvey!.StoppedBy);
            Assert.True(window.Work!.Complete); Assert.Contains("Đã khảo sát xong", window.SurveyStatus);
            Assert.NotEmpty((await store.LoadAsync()).Proposals); // Written to the Project's own ledger.
            Assert.Empty((await new ProjectResourceStore(fixture.Project).LoadAsync()).Characters.Profiles); // Profiles only change on "Hoàn thành".

            int calls = client.Calls;
            await window.SurveyAsync(); Assert.Equal(calls, client.Calls); // Already complete: no request.
            Assert.NotEmpty(window.Preview!.Applied);
            await window.CompleteAsync(); Assert.Null(window.LastError);
            var saved = await new ProjectResourceStore(fixture.Project).LoadAsync();
            Assert.Contains(saved.Characters.Profiles, p => p.ChineseName == "王秀兰" && p.NarrativePronounLocked);
            Assert.True(saved.Characters.ConsistencyLockEnabled);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task One_click_Complete_writes_everything_turns_translation_on_and_can_be_undone() => StaHost.Run(async () =>
    {
        using var fixture = new WorkspaceFixture(); await fixture.CreateProjectAsync();
        for (int n = 1; n <= 2; n++)
            await fixture.Repository.AddChapterAsync(fixture.Project, TranslationService.CreateChapter("Chương " + n, string.Concat(Enumerable.Repeat(Block, 3))), n);
        var sources = await EntityMemoryProjectInputs.LoadSourcesAsync(fixture.Project, fixture.Repository);
        var resources = await new ProjectResourceStore(fixture.Project).LoadAsync();
        var tasks = AddressingAiSurvey.PlanTasks(sources, AddressingAiSurvey.CandidateNames(fixture.Project.Manifest.ProjectId, sources, resources));
        var request = AddressingAiSurvey.PlanRequests(tasks, "mock").Single();
        await new AddressingLedgerStore(fixture.Project).UpdateAsync(s => s.Proposals.AddRange(AddressingAiSurvey.Verify(Reply(request), request)));

        ProjectResources? applied = null;
        var window = new AddressingLedgerWindow(fixture.Project, fixture.Repository, r => applied = r);
        try
        {
            await window.LoadAsync(); Assert.Null(window.LastError);
            Assert.Equal(request.Tasks.Sum(t => t.Kind == AddressingTaskKind.Pair && t.ReverseSamples.Count > 0 ? 2 : 1), window.Preview!.Applied.Count); Assert.Contains("Hoàn thành sẽ ghi", window.CompletionText);
            await window.CompleteAsync(); Assert.Null(window.LastError);
            var saved = await new ProjectResourceStore(fixture.Project).LoadAsync();
            Assert.True(saved.Characters.ConsistencyLockEnabled); Assert.Equal(saved.Revision, applied!.Revision);
            var sister = saved.Characters.Profiles.Single(p => p.ChineseName == "王秀兰");
            Assert.Equal("Vương Tú Lan", sister.VietnameseName); Assert.True(sister.NarrativePronounLocked); Assert.NotEmpty(sister.Relationships);
            Assert.Contains("Đã ghi", window.CompletionText);
            Assert.All(window.State.Proposals, p => Assert.Equal(AddressingDecision.Accepted, window.State.LatestReview(p.Id)!.Decision));

            await window.UndoAsync(discardLaterEdits: false); Assert.Null(window.LastError);
            var restored = await new ProjectResourceStore(fixture.Project).LoadAsync();
            Assert.Empty(restored.Characters.Profiles); Assert.False(restored.Characters.ConsistencyLockEnabled); Assert.Equal(restored.Revision, applied!.Revision);
            Assert.Equal(request.Tasks.Sum(t => t.Kind == AddressingTaskKind.Pair && t.ReverseSamples.Count > 0 ? 2 : 1), window.Preview!.Applied.Count); // Pending again.
            Assert.Equal(0, fixture.Client.Calls);
        }
        finally { window.Close(); }
    });
}
