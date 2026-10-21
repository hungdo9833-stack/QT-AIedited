using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>The CPU pre-filter drops words and "name + verb" fragments before they cost a request, and never a person or a profile.</summary>
public sealed class AddressingCandidateFilterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-filter-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private const string Chapter =
        "    宁夕推开门，她看到陆霆骁坐在沙发上。\n" +
        "    宁夕笑了，她说道：“你来了。”\n" +
        "    陆霆骁缓缓站起来，他皱眉：“宁夕，你发烧了。”\n" +
        "    宁夕缓缓坐下，她叹了口气。\n" +
        "    他们的关系变了，关系越来越近，关系也很微妙。\n" +
        "    周伯笑了笑，他把茶递给宁夕。\n";
    private static readonly CensusSource[] Sources = Enumerable.Range(1, 6).Select(n => new CensusSource(Guid.NewGuid(), n, Chapter)).ToArray();
    private static readonly string[] Names = ["宁夕", "陆霆骁", "宁夕缓缓", "陆霆骁缓缓", "关系", "周伯"];
    private static readonly Dictionary<string, int> Frequency = new() { ["宁夕"] = 400, ["陆霆骁"] = 300, ["宁夕缓缓"] = 6, ["陆霆骁缓缓"] = 6, ["关系"] = 18, ["周伯"] = 6 };

    private static IReadOnlyList<DroppedCandidate> Find(IReadOnlyList<string> names, params string[] keep) =>
        CandidateFilter.Find(Sources, names, keep.ToHashSet(StringComparer.Ordinal), Frequency, CancellationToken.None);

    [Fact]
    public void A_name_plus_verb_fragment_is_dropped_when_the_same_tail_follows_several_names()
    {
        var dropped = Find(Names).Select(d => d.Name).ToArray();
        Assert.Contains("宁夕缓缓", dropped); Assert.Contains("陆霆骁缓缓", dropped);
        Assert.DoesNotContain("宁夕", dropped); Assert.DoesNotContain("陆霆骁", dropped);
    }

    [Fact]
    public void A_word_nobody_calls_a_person_is_dropped_but_a_person_with_a_pronoun_or_a_voice_stays()
    {
        var dropped = Find(Names).Select(d => d.Name).ToArray();
        Assert.Contains("关系", dropped); // Mentioned 18 times, never followed by 他/她, never speaks or is addressed.
        Assert.DoesNotContain("周伯", dropped); // A minor person: 周伯笑了笑，他…
    }

    [Fact]
    public void A_longer_real_name_beside_a_commoner_one_stays_unless_the_gap_is_extreme()
    {
        var frequency = new Dictionary<string, int> { ["宁夕"] = 400, ["宁夕山"] = 60, ["宁夕山川"] = 6 };
        var names = new[] { "宁夕", "宁夕山", "宁夕山川" };
        var text = new[] { new CensusSource(Guid.NewGuid(), 1, "    宁夕山笑了，他说道：“宁夕，你来了。”\n    宁夕山川看着宁夕，他点头。\n    宁夕笑了，她很累。\n") };
        var dropped = CandidateFilter.Find(text, names, new HashSet<string>(), frequency, CancellationToken.None).Select(d => d.Name).ToArray();
        Assert.DoesNotContain("宁夕山", dropped); // 6.7x rarer, one base, one tail.
        Assert.Contains("宁夕山川", dropped); // 66x rarer than 宁夕.
    }

    [Fact]
    public void Profile_names_and_aliases_are_never_dropped()
    {
        var dropped = Find(Names, "关系", "宁夕缓缓").Select(d => d.Name).ToArray();
        Assert.DoesNotContain("关系", dropped); Assert.DoesNotContain("宁夕缓缓", dropped);
        Assert.Contains("陆霆骁缓缓", dropped);
    }

    [Fact]
    public void Every_drop_names_its_reason_and_carries_no_source_text()
    {
        var dropped = Find(Names);
        Assert.All(dropped, d => Assert.DoesNotContain("推开", d.Reason));
        Assert.Contains(dropped, d => d.Name == "宁夕缓缓" && d.Reason.StartsWith("extends 宁夕", StringComparison.Ordinal));
        Assert.Contains(dropped, d => d.Name == "关系" && d.Reason.StartsWith("no 他/她", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_plan_asks_nothing_about_a_dropped_candidate_and_switching_the_filter_off_restores_the_old_plan()
    {
        var session = await new ProjectRepository().CreateAsync(Path.Combine(_root, Guid.NewGuid().ToString("N")), "Synthetic filter");
        var resources = await new ProjectResourceStore(session).LoadAsync();
        var state = new AddressingLedgerState { ProjectId = session.Manifest.ProjectId };
        var dropped = AddressingAiSurvey.DroppedCandidates(session.Manifest.ProjectId, Sources, resources).Select(d => d.Name).ToHashSet(StringComparer.Ordinal);
        var plan = AddressingSurveyRunner.Plan(session.Manifest.ProjectId, Sources, resources, state);
        Assert.All(plan.Scenes.Concat(plan.Tasks), t =>
        {
            Assert.DoesNotContain(t.Subject, dropped); Assert.DoesNotContain(t.Listener ?? "", dropped);
            Assert.DoesNotContain(t.Participants, dropped.Contains);
        });
        var unfiltered = AddressingSurveyRunner.Plan(session.Manifest.ProjectId, Sources, resources, state, new(FilterJunkCandidates: false));
        Assert.True(unfiltered.Tasks.Count >= plan.Tasks.Count);
        Assert.Equal(AddressingAiSurvey.CandidateNames(session.Manifest.ProjectId, Sources, resources, 3, filterJunk: false).Count - dropped.Count,
            AddressingAiSurvey.CandidateNames(session.Manifest.ProjectId, Sources, resources).Count);
    }
}
