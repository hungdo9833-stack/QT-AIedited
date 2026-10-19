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
                pairs.Add(new JsonObject { ["task_id"] = t.TaskId, ["status"] = "Consistent", ["relationship"] = "chị em",
                    ["phases"] = new JsonArray(new JsonObject { ["from_chapter"] = 1, ["to_chapter"] = 0, ["self_vi"] = "chị", ["address_vi"] = "em",
                        ["self_zh"] = new JsonArray(), ["address_zh"] = new JsonArray(), ["sample_ids"] = ids }), ["reason"] = "Mock" });
            else
            {
                bool female = t.SourceGender == "female";
                characters.Add(new JsonObject { ["task_id"] = t.TaskId, ["status"] = "Consistent", ["gender"] = t.SourceGender,
                    ["name_forms"] = new JsonArray(t.NameCandidates.Select(v => (JsonNode)new JsonObject { ["zh"] = v.Form, ["vi"] = "Tú Lan" }).ToArray()),
                    ["phases"] = new JsonArray(new JsonObject { ["from_chapter"] = 1, ["to_chapter"] = 0, ["narrative_vi"] = female ? "nàng" : "hắn",
                        ["secondary_vi"] = "", ["sample_ids"] = ids }), ["reason"] = "Mock" });
            }
        }
        return new JsonObject { ["pairs"] = pairs, ["characters"] = characters }.ToJsonString();
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
}
