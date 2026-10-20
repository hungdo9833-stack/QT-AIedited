using System.Text.RegularExpressions;
using QuickTranslator.AiEditor.Core.BookIntelligence;

namespace QuickTranslator.AiEditor.Tests;

public sealed class AdaptiveSemanticTests
{
    internal static string Windows(int count) => string.Concat(Enumerable.Range(0, count).Select(i => Window("林青说道：我叫林青。周宁是林青的亲生父亲。")));
    internal static string Window(string prefix) => prefix + new string('云', 900 - prefix.Length);
    internal static IReadOnlyList<SemanticSurveyRequest> Plan(SemanticFixture s, int count = 4, int reserve = int.MaxValue) =>
        SemanticSurveyRequests.Plan(s.Memory, s.Sources, new() { ProjectId = s.Memory.ProjectId }, "mock:selected", s.Options,
            batching: new(count, reserve));
    internal static SemanticFinding Name(SemanticSurveyRequest unit, string name = "林青", string? local = null) => new()
    {
        FindingType = SemanticFindingType.CharacterIdentification, IdentityResolutionStatus = SemanticResolution.NewIdentity,
        SubjectIdentity = new(null, local ?? "p_" + unit.Evidence.Id.ToString("N"), name, MemoryEntityKind.Character, SemanticResolution.NewIdentity),
        FactKind = MemoryFactKind.PrimaryName, Value = name, SourceEvidenceIds = [unit.Evidence.Id],
        SupportSpans = [new(unit.Evidence.Id, unit.Evidence.StartUtf16 + unit.EvidenceText.IndexOf(name, StringComparison.Ordinal), name.Length, name)],
        EffectiveChapter = unit.ChapterNumber, RevealedChapter = unit.ChapterNumber, Confidence = .8, Reason = "Labelled mock source, no name-based merge"
    };
    internal static SemanticResponse Response(SemanticSurveyRequest r, params SemanticFinding[] findings) =>
        new(r.WireVersion, r.ProjectId, r.Evidence.ChapterId, r.Units.Select(x => x.Evidence.Id).ToArray(), findings);
    internal static SemanticResponse FromPrompt(string prompt, IReadOnlyList<SemanticSurveyRequest> atomic, bool names = true)
    {
        var ids = Regex.Matches(prompt, @"(?m)^EVIDENCE project=.*? id=([a-fA-F0-9-]{36}) start_utf16=").Select(m => Guid.Parse(m.Groups[1].Value)).ToArray();
        var units = ids.Select(id => atomic.Single(x => x.Evidence.Id == id)).ToArray();
        return new(prompt.Contains("MULTI-EVIDENCE", StringComparison.Ordinal) ? 2 : 1, units[0].ProjectId, units[0].Evidence.ChapterId, ids, names ? units.Select(x => Name(x)).ToArray() : []);
    }

    [Fact]
    public void Planner_keeps_all_ids_hashes_ranges_order_and_chapters_and_shares_only_identical_confirmed_snapshots()
    {
        var s = new SemanticFixture(Windows(8)); s.Sources.Add(new(Guid.NewGuid(), 2, Windows(3)));
        var atomic = Plan(s, 1); var grouped = Plan(s);
        Assert.Equal(11, atomic.Count); Assert.Equal(3, grouped.Count); Assert.Equal(new[] { 4, 4, 3 }, grouped.Select(x => x.Units.Count));
        Assert.Equal(atomic.Select(x => (x.Id, x.Evidence)), grouped.SelectMany(x => x.Units).Select(x => (x.Id, x.Evidence)));
        Assert.All(grouped, b => { Assert.Single(b.Units.Select(x => x.SharedContextJson).Distinct()); Assert.Single(b.Units.Select(x => x.Evidence.ChapterId).Distinct()); });
        Assert.True(grouped.Sum(x => x.EstimatedInputTokens) < atomic.Sum(x => x.EstimatedInputTokens));
        Assert.NotEqual(grouped[0].Id, Plan(s, 6)[0].Id);
        var otherContext = atomic[1] with { SharedContextJson = "different confirmed snapshot" };
        Assert.Equal(3, SemanticAdaptiveBatching.Pack([atomic[0], otherContext, atomic[2]], s.Options, new()).Count);
    }
}
