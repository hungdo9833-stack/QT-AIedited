using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>Phase C: one task per pair, both directions in one answer, and a CPU cross-check that they agree. Mock replies only.</summary>
public sealed class AddressingPairTests
{
    private static AddressingRequestPlan Request() =>
        AddressingAiSurvey.PlanRequests(AddressingAiSurvey.PlanTasks(AddressingAiSurveyTests.Sources, AddressingAiSurveyTests.Names), "mock").Single();
    private static AddressingTask PairTask(AddressingRequestPlan r) => r.Tasks.Single(t => t.Kind == AddressingTaskKind.Pair);

    private static JsonObject Direction(IReadOnlyList<AddressingTaskSample> samples, params (int From, int To, string Self, string Address)[] phases) => new()
    {
        ["status"] = phases.Length > 1 ? "Changes" : "Consistent", ["relationship"] = "chị em",
        ["phases"] = new JsonArray(phases.Select(p => (JsonNode)new JsonObject
        {
            ["from_chapter"] = p.From, ["to_chapter"] = p.To, ["self_vi"] = p.Self, ["address_vi"] = p.Address, ["self_zh"] = new JsonArray(), ["address_zh"] = new JsonArray(),
            ["sample_ids"] = new JsonArray(samples.Where(s => s.ChapterNumber >= p.From && (p.To == 0 || s.ChapterNumber <= p.To)).Select(s => (JsonNode)s.SampleId).ToArray())
        }).ToArray())
    };
    private static IReadOnlyList<AddressingProposal> Verify(AddressingRequestPlan r, JsonObject forward, JsonObject reverse) =>
        AddressingAiSurvey.Verify(AddressingAiSurveyTests.Answer(r, t => t.Kind != AddressingTaskKind.Pair ? null :
            new JsonObject { ["task_id"] = t.TaskId, ["forward"] = forward, ["reverse"] = reverse, ["reason"] = "Hai chiều." }).ToJsonString(), r)
        .Where(p => p.Kind == AddressingTaskKind.Pair).ToArray();

    [Fact]
    public void One_task_asks_both_directions_with_their_own_samples()
    {
        var r = Request(); var pair = PairTask(r);
        Assert.Equal(("王秀兰", "林青"), (pair.Subject, pair.Listener));
        string prompt = r.Prompt;
        Assert.Contains("PAIR A=王秀兰 B=林青", prompt); Assert.Contains("FORWARD: A=王秀兰 speaking to B=林青 (cite S ids)", prompt);
        Assert.Contains("REVERSE: B=林青 speaking to A=王秀兰 (cite R ids)", prompt); Assert.Contains("R1 [ch.1]", prompt);
        var proposals = Verify(r, Direction(pair.Samples, (1, 0, "chị", "em")), Direction(pair.ReverseSamples, (1, 0, "em", "chị")));
        Assert.Equal(2, proposals.Count); Assert.All(proposals, p => Assert.True(p.EvidenceValid, string.Join(",", p.Issues)));
        var back = proposals.Single(p => p.Subject == "林青");
        Assert.Equal("王秀兰", back.Listener); Assert.Equal("em", back.Phases[0].SelfVi); Assert.All(back.Phases[0].SampleIds, id => Assert.StartsWith("R", id));
        // Each direction cites its own samples: the reverse cannot borrow the forward's evidence.
        var borrowed = Verify(r, Direction(pair.Samples, (1, 0, "chị", "em")), Direction(pair.Samples, (1, 0, "em", "chị")).Also(d => d["relationship"] = "em chị"));
        Assert.Contains("MissingOrForeignSample", borrowed.Single(p => p.Subject == "林青").Issues);
    }

    [Fact]
    public void A_quiet_direction_rides_along_with_the_attested_one_and_a_silent_one_yields_no_proposal()
    {
        // 王秀兰 speaks to 林青 three times; he answers once (below the 3-line minimum on its own).
        string text = string.Concat(Enumerable.Repeat("    王秀兰看着林青说道：「林青，吃饭了。」\n", 3)) + "    林青看着王秀兰说道：「秀兰姐，我来了。」\n";
        var task = AddressingAiSurvey.PlanTasks([new(Guid.NewGuid(), 1, text)], AddressingAiSurveyTests.Names).Single(t => t.Kind == AddressingTaskKind.Pair);
        Assert.Equal("王秀兰", task.Subject); Assert.Equal(3, task.Samples.Count); Assert.Single(task.ReverseSamples);
        string silent = string.Concat(Enumerable.Repeat("    王秀兰看着林青说道：「林青，吃饭了。」\n", 3));
        var one = AddressingAiSurvey.PlanTasks([new(Guid.NewGuid(), 1, silent)], AddressingAiSurveyTests.Names).Single(t => t.Kind == AddressingTaskKind.Pair);
        Assert.Empty(one.ReverseSamples);
        var r = AddressingAiSurvey.PlanRequests([one], "mock").Single();
        Assert.Contains("(no samples: answer reverse with status Ambiguous and phases [])", r.Prompt);
        var proposals = Verify(r, Direction(one.Samples, (1, 0, "chị", "em")), new JsonObject { ["status"] = "Ambiguous", ["relationship"] = "", ["phases"] = new JsonArray() });
        Assert.Equal("王秀兰", Assert.Single(proposals).Subject); // No guessed reverse rule.
        string none = "    王秀兰看着林青说道：「林青，吃饭了。」\n    林青看着王秀兰说道：「秀兰姐，我来了。」\n";
        Assert.DoesNotContain(AddressingAiSurvey.PlanTasks([new(Guid.NewGuid(), 1, none)], AddressingAiSurveyTests.Names), t => t.Kind == AddressingTaskKind.Pair);
    }

    public static TheoryData<string, string, string, string, bool> Reciprocity => new()
    {
        // A self, A address, B self, B address, contradiction?
        { "chị", "em", "em", "chị", false },       // Mirror.
        { "bố", "con", "con", "bố", false },
        { "tôi", "em", "em", "anh", false },       // A uses neutral tôi: proves nothing (older brother saying tôi).
        { "tôi", "cô", "tôi", "anh", false },      // Both neutral selves; cô/anh are not compared with tôi.
        { "ta", "ngươi", "tôi", "ngài", false },   // Neutral both ways.
        { "chị", "em", "chị", "em", true },        // Both claim to be the elder.
        { "anh", "em", "anh", "em", true },
        { "sư phụ", "đồ nhi", "đồ nhi", "sư phụ", false },
        { "anh", "em", "em/con", "anh", false },   // Several forms: one shared is enough.
        { "tôi", "bố mẹ", "mẹ", "con", false },
        { "con", "bố mẹ", "mẹ", "con", false },
        { "sư phụ", "đồ nhi", "sư huynh", "sư phụ", true },
        { "con", "anh em", "anh", "con", false },
        { "con", "  BỐ   MẸ ", "mẹ", "con", false },
        { "con", "bố và mẹ", "bố mẹ", "con", true }, // Words must be contiguous.
        { "con", "thầy", "ầy", "con", true },     // No substring matches inside a word.
        { "mẹ", "con", "con", "dì", true }         // A calls herself mother, B calls her aunt.
    };

    [Theory]
    [MemberData(nameof(Reciprocity))]
    public void The_cross_check_flags_role_contradictions_but_not_neutral_forms(string aSelf, string aAddress, string bSelf, string bAddress, bool contradiction)
    {
        var a = new AddressingPhase(1, 0, aSelf, aAddress, [], [], "", "", ["S1"]);
        var b = new AddressingPhase(1, 0, bSelf, bAddress, [], [], "", "", ["R1"]);
        Assert.Equal(contradiction, AddressingAiSurvey.Contradicts([a], [b]));
    }

    [Fact]
    public void A_contradiction_holds_both_directions_back_and_phases_are_compared_only_where_they_overlap()
    {
        var r = Request(); var pair = PairTask(r);
        var clash = Verify(r, Direction(pair.Samples, (1, 0, "chị", "em")), Direction(pair.ReverseSamples, (1, 0, "anh", "em")));
        Assert.All(clash, p => { Assert.False(p.EvidenceValid); Assert.Contains("ReciprocalMismatch", p.Issues); });
        // Chapter 1: chị/em ↔ em/chị. From chapter 2 she becomes "ta/chàng" and he "ta/nàng": no overlap with the other's chapter-1 terms.
        var ranged = Verify(r, Direction(pair.Samples, (1, 1, "chị", "em"), (2, 0, "ta", "chàng")), Direction(pair.ReverseSamples, (1, 1, "em", "chị"), (2, 0, "ta", "nàng")));
        Assert.All(ranged, p => Assert.True(p.EvidenceValid, string.Join(",", p.Issues)));
        var later = Verify(r, Direction(pair.Samples, (1, 1, "chị", "em"), (2, 0, "chị", "em")), Direction(pair.ReverseSamples, (1, 1, "em", "chị"), (2, 0, "anh", "em")));
        Assert.All(later, p => Assert.Contains("ReciprocalMismatch", p.Issues)); // Chapter 2 overlaps and clashes.
    }

    [Fact]
    public void Hoan_thanh_writes_both_directions_of_one_answer()
    {
        // The completion step needs no change: one pair answer becomes two directed rules.
        var r = Request(); var pair = PairTask(r);
        var proposals = Verify(r, Direction(pair.Samples, (1, 0, "chị", "em")), Direction(pair.ReverseSamples, (1, 0, "em", "chị")));
        Assert.Equal(["林青", "王秀兰"], proposals.Select(p => p.Subject).Order());
        Assert.Equal(proposals[0].TaskId, proposals[1].TaskId); Assert.NotEqual(proposals[0].Id, proposals[1].Id);
    }

    [Theory]
    [InlineData(3, 1, false, true)]
    [InlineData(3, 1, true, true)]
    [InlineData(6, 2, false, true)]
    [InlineData(2, 1, false, false)]
    [InlineData(3, 2, false, false)]
    [InlineData(5, 2, false, false)]
    public void A_contradiction_holds_only_the_sparse_direction_when_the_other_has_three_times_the_evidence(int forward, int reverse, bool swap, bool dominant)
    {
        string text = string.Concat(Enumerable.Repeat("    王秀兰看着林青说道：「林青，吃饭了。」\n", forward)) +
            string.Concat(Enumerable.Repeat("    林青看着王秀兰说道：「秀兰姐，我来了。」\n", reverse));
        var task = AddressingAiSurvey.PlanTasks([new(Guid.NewGuid(), 1, text)], AddressingAiSurveyTests.Names, new(MinimumPairLines: 1)).Single(t => t.Kind == AddressingTaskKind.Pair);
        Assert.Equal(forward, task.Samples.Count); Assert.Equal(reverse, task.ReverseSamples.Count);
        if (swap) task = task with { Subject = task.Listener!, Listener = task.Subject, Samples = task.ReverseSamples, ReverseSamples = task.Samples };
        var r = AddressingAiSurvey.PlanRequests([task], "mock").Single();
        var clash = Verify(r, Direction(task.Samples, (1, 0, "chị", "em")), Direction(task.ReverseSamples, (1, 0, "anh", "em")));
        var a = clash.Single(p => p.Subject == task.Subject); var b = clash.Single(p => p.Subject == task.Listener);
        Assert.Equal(dominant && !swap, a.EvidenceValid); Assert.Equal(dominant && swap, b.EvidenceValid);
        Assert.Equal(!a.EvidenceValid, a.Issues.Contains("ReciprocalMismatch")); Assert.Equal(!b.EvidenceValid, b.Issues.Contains("ReciprocalMismatch"));
        // More samples waive only reciprocal mismatch, never the checks on the direction's own evidence.
        var bad = Direction(task.Samples, (1, 0, "chị", "em")).Also(d => d["phases"]![0]!["self_zh"] = new JsonArray("无证词"));
        var invalid = Verify(r, bad, Direction(task.ReverseSamples, (1, 0, "anh", "em"))).Single(p => p.Subject == task.Subject);
        Assert.False(invalid.EvidenceValid); Assert.Contains("UnquotedChineseTerm", invalid.Issues);
    }
}
