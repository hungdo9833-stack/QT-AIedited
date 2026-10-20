using System.Text.Json;
using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>Phase D: "same person?" (小包子 = 小宝), merged by "Hoàn thành" only when the CPU and the AI agree. Mock answers only.</summary>
public sealed class AddressingIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-identity-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static readonly string[] Names = ["宁夕", "陆霆骁", "小宝", "小包子"];
    // The mute child 小宝 is also called 小包子 ("little bun") in narration; the adults talk to each other.
    private const string Chapter =
        "    宁夕看着床上的小宝，小包子睡得正香。\n" +
        "    陆霆骁抱起小宝，小包子揉了揉眼睛。\n" +
        "    宁夕看着陆霆骁说道：“你来了。”\n" +
        "    陆霆骁看着宁夕说道：“嗯。”\n";
    private static CensusSource[] Book(string chapter, int chapters = 2) => Enumerable.Range(1, chapters).Select(n => new CensusSource(Guid.NewGuid(), n, chapter)).ToArray();
    private static AddressingTask? Candidate(IReadOnlyList<AddressingTask> tasks, string a, string b) =>
        tasks.SingleOrDefault(t => new[] { t.Subject, t.Listener }.Order().SequenceEqual(new[] { a, b }.Order()));

    [Fact]
    public void The_cpu_asks_about_a_nickname_that_shares_paragraphs_with_a_name_and_never_talks_to_it()
    {
        var tasks = AddressingAiSurvey.PlanIdentities(Book(Chapter), Names);
        var bun = Candidate(tasks, "小宝", "小包子");
        Assert.NotNull(bun); Assert.Equal(AddressingTaskKind.Identity, bun!.Kind);
        Assert.Equal(4, bun.Samples.Count); Assert.Equal([1, 2], bun.Samples.Select(s => s.ChapterNumber).Distinct()); // Spread over the book.
        Assert.All(bun.Samples, s => { Assert.StartsWith("P", s.SampleId); Assert.Contains("小宝", s.Text); Assert.Contains("小包子", s.Text); });
        Assert.Null(Candidate(tasks, "宁夕", "陆霆骁")); // Two full names that talk to each other.
        string prompt = AddressingAiSurvey.PlanRequests([bun], "m").Single().Prompt;
        Assert.Contains("IDENTITY X=小包子 Y=小宝", prompt); Assert.Contains("P1 [ch.1] 宁夕看着床上的小宝，小包子睡得正香。", prompt);
    }

    [Theory]
    [InlineData("    小宝和小包子一起玩。\n", "listed side by side")]
    [InlineData("    小宝看着小包子说道：“你好。”\n", "they talk")]
    [InlineData("    小宝笑了，他很开心。小宝跑了，他很快。小宝叫了，他很响。小宝睡了，他很香。小宝醒了，他很乖。小宝哭了，他很委屈。\n" +
                "    小包子笑了，她很开心。小包子跑了，她很快。小包子叫了，她很响。小包子睡了，她很香。小包子醒了，她很乖。小包子哭了，她很委屈。\n", "genders differ")]
    public void The_cpu_never_asks_when_the_text_shows_two_people(string extra, string why)
    {
        Assert.Null(Candidate(AddressingAiSurvey.PlanIdentities(Book(Chapter + extra), Names), "小宝", "小包子"));
        _ = why;
    }

    [Fact]
    public void Two_full_names_are_never_asked_and_one_shared_paragraph_is_not_enough()
    {
        const string adults = "    王秀兰站在窗前，林青坐在沙发上。\n";
        Assert.Empty(AddressingAiSurvey.PlanIdentities(Book(adults, 3), ["王秀兰", "林青"]));
        Assert.Null(Candidate(AddressingAiSurvey.PlanIdentities(Book(Chapter, 1).Select(s => s with { OriginalText = Chapter.Split('\n')[0] + "\n" }).ToArray(), Names), "小宝", "小包子"));
    }

    private static JsonObject Reply(AddressingRequestPlan request, string verdict, string primary, string vi, IEnumerable<string>? ids = null) => new()
    {
        ["pairs"] = new JsonArray(), ["characters"] = new JsonArray(), ["scenes"] = new JsonArray(),
        ["identities"] = new JsonArray(request.Tasks.Select(t => (JsonNode)new JsonObject
        {
            ["task_id"] = t.TaskId, ["verdict"] = verdict, ["primary"] = primary, ["secondary_vi"] = vi, ["reason"] = "Biệt danh.",
            ["sample_ids"] = new JsonArray((ids ?? t.Samples.Select(s => s.SampleId)).Select(x => (JsonNode)x).ToArray())
        }).ToArray())
    };

    [Fact]
    public void The_verifier_accepts_same_only_with_a_listed_primary_own_samples_and_a_vietnamese_rendering()
    {
        var task = Candidate(AddressingAiSurvey.PlanIdentities(Book(Chapter), Names), "小宝", "小包子")!;
        var request = AddressingAiSurvey.PlanRequests([task], "m").Single();
        AddressingProposal Verify(string verdict, string primary, string vi, IEnumerable<string>? ids = null) =>
            AddressingAiSurvey.Verify(Reply(request, verdict, primary, vi, ids).ToJsonString(), request).Single();
        var same = Verify("same", "小宝", "Tiểu Bao Tử");
        Assert.True(same.EvidenceValid); Assert.Equal("Same", same.Status); Assert.Equal("小宝", same.Primary);
        Assert.Equal(new AddressingNameForm("小包子", "Tiểu Bao Tử"), Assert.Single(same.NameForms));
        Assert.Contains("UnlistedPrimary", Verify("same", "宁夕", "Tiểu Bao Tử").Issues);
        Assert.Contains("MissingOrForeignSample", Verify("same", "小宝", "Tiểu Bao Tử", ["S1"]).Issues);
        Assert.Contains("MissingOrForeignSample", Verify("same", "小宝", "Tiểu Bao Tử", []).Issues);
        Assert.Contains("ChineseInVietnameseField", Verify("same", "小宝", "Tiểu 包子").Issues);
        Assert.Contains("MissingOrOverlongVietnameseName", Verify("same", "小宝", "").Issues);
        var different = Verify("different", "", "");
        Assert.False(different.EvidenceValid); Assert.Empty(different.Issues); Assert.Equal("Different", different.Status);
    }

    private sealed class IdentityMock(IReadOnlyList<AddressingTask> tasks, string verdict) : IJsonSchemaOllamaClient
    {
        public int Calls { get; private set; }
        public Task<OllamaCompletion> TranslateAsync(string prompt, ModelOptions options, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<OllamaCompletion> TranslateStructuredAsync(string prompt, ModelOptions options, JsonElement schema, CancellationToken ct = default)
        {
            Calls++; Assert.Equal(AddressingAiSurvey.Schema.GetRawText(), schema.GetRawText());
            var sent = AddressingAiSurvey.TasksInPrompt(prompt, tasks); Assert.NotEmpty(sent);
            var reply = Reply(AddressingAiSurvey.PlanRequests(sent, options.Model).Single(), verdict, "", "");
            for (int i = 0; i < sent.Count; i++) reply["identities"]![i]!["task_id"] = "T" + (i + 1);
            return Task.FromResult(new OllamaCompletion(reply.ToJsonString(), true, "stop", new GenerationMetrics { InputTokens = 100, OutputTokens = 30 }));
        }
    }

    [Theory]
    [InlineData("different", 0)]
    [InlineData("unclear", 1)]
    public async Task Identity_resume_keeps_different_final_but_asks_unclear_once_more(string verdict, int secondRequests)
    {
        var task = Candidate(AddressingAiSurvey.PlanIdentities(Book(Chapter), Names), "小宝", "小包子")!;
        var session = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), "Synthetic identity resume");
        var client = new IdentityMock([task], verdict); var options = AddressingAiSurveyTests.Options;
        var first = await new AddressingAiEngine(client).RunAsync(new(session), [task], options, 1);
        Assert.Equal(1, first.Planned); Assert.Equal(1, first.RequestsSent); Assert.Equal(1, client.Calls);
        var proposal = Assert.Single(first.State.Proposals); Assert.False(proposal.EvidenceValid); Assert.Empty(proposal.Issues);
        Assert.Equal(verdict == "different" ? "Different" : "Unclear", proposal.Status);
        // A fresh store/engine reloads the checkpoint; no in-memory "done" flag can hide a resend.
        var second = await new AddressingAiEngine(client).RunAsync(new(session), [task], options, 1);
        Assert.Equal(secondRequests, second.Planned); Assert.Equal(secondRequests, second.RequestsSent); Assert.Equal(1 + secondRequests, client.Calls);
        Assert.Equal(1 + secondRequests, second.State.Attempts.Count);
        Assert.Equal(JsonSerializer.Serialize(first.State.Attempts[0]), JsonSerializer.Serialize(second.State.Attempts[0]));
        Assert.Equal(proposal.Id, Assert.Single(second.State.Proposals).Id);
        var third = await new AddressingAiEngine(client).RunAsync(new(session), [task], options, 1);
        Assert.Equal(0, third.Planned); Assert.Equal(0, third.RequestsSent); Assert.Equal(1 + secondRequests, client.Calls);
        Assert.Empty((await new ProjectResourceStore(session).LoadAsync()).Characters.Profiles);
    }

    [Theory]
    [InlineData(AddressingTaskKind.Identity)]
    [InlineData(AddressingTaskKind.Pair)]
    [InlineData(AddressingTaskKind.Character)]
    [InlineData(AddressingTaskKind.Scene)]
    public void Different_is_final_only_for_an_issue_free_identity_with_the_same_task_id(AddressingTaskKind kind)
    {
        var proposal = Same("小包子", "小宝", "", "") with { Status = "Different", EvidenceValid = false, NameForms = [] };
        var task = new AddressingTask(proposal.TaskId, kind, proposal.Subject, proposal.Listener, 1, 1, []);
        var state = new AddressingLedgerState { Proposals = [proposal] };
        Assert.Equal(kind == AddressingTaskKind.Identity, state.Done(task)); Assert.False(state.Done(task.TaskId));
        Assert.False(state.Done(task with { TaskId = "other-task" }));
        state.Proposals[0] = proposal with { Issues = ["MissingOrForeignSample"] }; Assert.False(state.Done(task));
        state.Proposals[0] = proposal with { Kind = AddressingTaskKind.Pair }; Assert.False(state.Done(task));
    }

    // ---------- "Hoàn thành" ----------

    private static AddressingPhase Narrative(string vi) => new(1, 0, "", "", [], [], vi, "", ["N1"]);
    private static AddressingProposal Character(string zh, string vi, string gender, string narrative, params AddressingNameForm[] more) =>
        new(Guid.NewGuid(), "r", "character:" + zh, AddressingTaskKind.Character, zh, null, "Consistent", "", gender, [Narrative(narrative)], [new(zh, vi), .. more], "", true, []);
    private static AddressingProposal Pair(string speaker, string listener, string self, string address) =>
        new(Guid.NewGuid(), "r", "pair:" + speaker + listener, AddressingTaskKind.Pair, speaker, listener, "Consistent", "dì → cháu", "unknown",
            [new(1, 0, self, address, [], [], "", "", ["S1"])], [], "", true, []);
    private static AddressingProposal Same(string x, string y, string primary, string vi) =>
        new(Guid.NewGuid(), "r", "identity:" + x + y, AddressingTaskKind.Identity, x, y, "Same", "", "unknown", [], [new(primary == x ? y : x, vi)], "", true, []) { Primary = primary };

    private async Task<(ProjectSession Session, AddressingReviewService Service)> Ledger(params AddressingProposal[] proposals)
    {
        var session = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), "Synthetic identity");
        await new AddressingLedgerStore(session).UpdateAsync(s => s.Proposals.AddRange(proposals));
        return (session, new AddressingReviewService(session));
    }
    private static Task<ProjectResources> Load(ProjectSession s) => new ProjectResourceStore(s).LoadAsync();

    [Fact]
    public async Task Complete_merges_the_nickname_into_the_main_profile_and_its_pairs_follow_then_undo_restores()
    {
        var (session, service) = await Ledger(
            Character("宁夕", "Ninh Tịch", "female", "cô"), Character("小宝", "Tiểu Bảo", "male", "thằng bé"),
            Character("小包子", "Tiểu Bao Tử", "male", "thằng bé", new AddressingNameForm("包子", "Bao Tử")),
            Pair("宁夕", "小宝", "dì", "con"), Pair("宁夕", "小包子", "cô", "cháu"), Pair("小包子", "宁夕", "con", "dì"),
            Same("小包子", "小宝", "小宝", "Tiểu Bao Tử"));
        var done = await service.CompleteAsync((await Load(session)).Revision);
        var profiles = done.Resources.Characters.Profiles;
        Assert.DoesNotContain(profiles, p => p.ChineseName == "小包子"); // No second profile for the same child.
        var child = profiles.Single(p => p.ChineseName == "小宝");
        Assert.Contains("小包子", child.Aliases); Assert.Contains("包子", child.Aliases);
        Assert.Contains(done.Resources.Terms, t => t.Chinese == "小包子" && t.Vietnamese == "Tiểu Bao Tử");
        var aunt = profiles.Single(p => p.ChineseName == "宁夕");
        var rule = Assert.Single(aunt.Relationships); Assert.Equal(child.Id, rule.EffectiveListenerCharacterId);
        Assert.Equal("dì", rule.SelfReference); // The main name's own rule wins over the one reached through the nickname.
        Assert.Contains(done.Held, h => h.Subject == "宁夕" && h.Listener == "小包子" && h.Detail.Contains("không ghi đè"));
        Assert.Contains(child.Relationships, r => r.EffectiveListenerCharacterId == aunt.Id && r.SelfReference == "con"); // 小包子 → 宁夕 lands on 小宝.
        Assert.Contains(done.Applied, a => a.Kind == AddressingTaskKind.Identity && a.Detail.Contains("Gộp 小包子 vào hồ sơ 小宝"));
        Assert.Contains(done.Applied, a => a.Kind == AddressingTaskKind.Character && a.Subject == "小包子" && a.Detail == "đã gộp vào hồ sơ 小宝");

        Assert.Empty((await service.CompleteAsync(done.Resources.Revision)).Applied); // Nothing offered twice.
        var undone = await service.UndoCompletionAsync();
        Assert.Empty(undone.Characters.Profiles);
        Assert.Contains((await service.PreviewCompletionAsync()).Applied, a => a.Kind == AddressingTaskKind.Identity); // Pending again.
    }

    [Fact]
    public async Task Different_or_unclear_merges_nothing_and_is_not_listed()
    {
        var different = Same("小包子", "小宝", "", "") with { Status = "Different", EvidenceValid = false, NameForms = [], Primary = "" };
        var (session, service) = await Ledger(Character("小宝", "Tiểu Bảo", "male", "thằng bé"), Character("小包子", "Tiểu Bao Tử", "male", "thằng bé"), different);
        var done = await service.CompleteAsync((await Load(session)).Revision);
        Assert.Equal(2, done.Resources.Characters.Profiles.Count); Assert.Empty(done.Held);
    }

    public static TheoryData<string, string> Guards => new()
    {
        { "they-talk", "nói chuyện" }, { "own-profile", "đã có trong hồ sơ" }, { "chain", "chuỗi" }, { "rejected", "bác bỏ" }, { "no-main-profile", "chưa có hồ sơ 小宝" }
    };

    [Theory]
    [MemberData(nameof(Guards))]
    public async Task The_cpu_still_vetoes_a_merge_at_write_time_and_the_nickname_keeps_its_own_profile(string guard, string reason)
    {
        var same = Same("小包子", "小宝", "小宝", "Tiểu Bao Tử");
        var proposals = new List<AddressingProposal> { Character("小宝", "Tiểu Bảo", "male", "thằng bé"), Character("小包子", "Tiểu Bao Tử", "male", "thằng bé"), same };
        if (guard == "they-talk") proposals.Add(Pair("小宝", "小包子", "tớ", "cậu"));
        if (guard == "chain") proposals.Add(Same("小宝", "宝宝", "宝宝", "Bảo Bảo"));
        if (guard == "no-main-profile") proposals.RemoveAt(0);
        var (session, service) = await Ledger([.. proposals]);
        var resources = await Load(session);
        if (guard == "own-profile")
        {
            resources.Characters.Profiles.Add(new() { ChineseName = "小包子", VietnameseName = "Bánh Bao", NormalizationEnabled = false });
            resources = await new ProjectResourceStore(session).SaveAsync(resources);
        }
        if (guard == "rejected") await service.RejectAsync(same.Id);
        var done = await service.CompleteAsync(resources.Revision);
        Assert.DoesNotContain(done.Resources.Characters.Profiles, p => p.Aliases.Contains("小包子"));
        Assert.Contains(done.Resources.Characters.Profiles, p => p.ChineseName == "小包子"); // Not lost: its own profile.
        Assert.Contains(done.Held, h => h.ProposalId == same.Id && h.Detail.Contains(reason)); // Held for the right reason.
    }

    [Fact]
    public async Task Survey_to_completion_with_a_same_answer_end_to_end()
    {
        var sources = Book(Chapter);
        var task = Candidate(AddressingAiSurvey.PlanIdentities(sources, Names), "小宝", "小包子")!;
        var request = AddressingAiSurvey.PlanRequests([task], "m").Single();
        var verified = AddressingAiSurvey.Verify(Reply(request, "same", "小宝", "Tiểu Bao Tử").ToJsonString(), request);
        var (session, service) = await Ledger([Character("小宝", "Tiểu Bảo", "male", "thằng bé"), .. verified]);
        var done = await service.CompleteAsync((await Load(session)).Revision);
        Assert.Contains("小包子", done.Resources.Characters.Profiles.Single(p => p.ChineseName == "小宝").Aliases);
    }
}
