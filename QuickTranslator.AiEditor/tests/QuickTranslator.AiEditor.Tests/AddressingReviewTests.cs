using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>Addressing Ledger step 3: human review writes the Character Project safely (mock proposals only).</summary>
public sealed class AddressingReviewTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-review-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    /// <summary>A project whose ledger holds verified proposals produced by the real planner/verifier from a mock reply.</summary>
    internal static async Task<(ProjectSession Session, AddressingReviewService Service, AddressingLedgerState Ledger)> Seed(string root,
        Func<AddressingTask, JsonObject?>? edit = null)
    {
        var session = await new ProjectRepository().CreateAsync(Path.Combine(root, Guid.NewGuid().ToString("N")), "Synthetic review");
        var request = AddressingAiSurvey.PlanRequests(AddressingAiSurvey.PlanTasks(AddressingAiSurveyTests.Sources, AddressingAiSurveyTests.Names),
            AddressingAiSurveyTests.Options.Model).Single();
        var proposals = AddressingAiSurvey.Verify(AddressingAiSurveyTests.Answer(request, edit).ToJsonString(), request);
        var ledger = await new AddressingLedgerStore(session).UpdateAsync(s => s.Proposals.AddRange(proposals));
        return (session, new AddressingReviewService(session), ledger);
    }
    private static AddressingProposal Character(AddressingLedgerState s, string name) => s.Proposals.Single(p => p.Kind == AddressingTaskKind.Character && p.Subject == name);
    private static AddressingProposal Pair(AddressingLedgerState s, string speaker) => s.Proposals.Single(p => p.Kind == AddressingTaskKind.Pair && p.Subject == speaker);
    private static CharacterAcceptance Heroine(AddressingProposal p) => new(CharacterGender.Female, "Vương Tú Lan", "nàng", "", p.NameForms, 0);
    private static CharacterAcceptance Hero(AddressingProposal p) => new(CharacterGender.Male, "Lâm Thanh", "hắn", "hắn ta", p.NameForms, 0);
    private static async Task<long> Revision(ProjectSession s) => (await new ProjectResourceStore(s).LoadAsync()).Revision;

    [Fact]
    public async Task Accepting_a_character_creates_a_locked_narrative_profile_with_aliases_and_glossary()
    {
        var (session, service, ledger) = await Seed(_root);
        var proposal = Character(ledger, "王秀兰");
        var saved = await service.AcceptCharacterAsync(proposal.Id, Heroine(proposal), await Revision(session));
        var profile = saved.Characters.Profiles.Single(p => p.ChineseName == "王秀兰");
        Assert.Equal("Vương Tú Lan", profile.VietnameseName); Assert.Equal(CharacterGender.Female, profile.Gender);
        Assert.Equal("nàng", profile.PrimaryNarrativePronoun); Assert.True(profile.NarrativePronounLocked);
        Assert.DoesNotContain("nàng", profile.DisallowedNarrativePronouns);
        Assert.Contains("小兰", profile.Aliases); Assert.Contains("秀兰姐", profile.Aliases);
        Assert.Contains(saved.Terms, t => t.Chinese == "小兰" && t.Vietnamese == "Tiểu Lan");
        Assert.Contains(saved.Terms, t => t.Chinese == "王秀兰" && t.Vietnamese == "Vương Tú Lan");
        Assert.False(saved.Characters.ConsistencyLockEnabled); // Never switched on implicitly.
        var review = (await service.LoadAsync()).LatestReview(proposal.Id)!;
        Assert.Equal(AddressingDecision.Accepted, review.Decision); Assert.Contains("nàng", review.Applied); Assert.Equal(saved.Revision, review.ResourceRevision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptCharacterAsync(proposal.Id, Heroine(proposal), saved.Revision));
    }

    [Fact]
    public async Task Pair_needs_both_profiles_then_writes_one_locked_directed_rule_used_by_the_translation_prompt()
    {
        var (session, service, ledger) = await Seed(_root);
        var pair = Pair(ledger, "王秀兰");
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptPairAsync(pair.Id, new("chị", "em", "chị → em", 0), 0));
        Assert.Contains("duyệt nhân vật", missing.Message);
        var r1 = await service.AcceptCharacterAsync(Character(ledger, "王秀兰").Id, Heroine(Character(ledger, "王秀兰")), await Revision(session));
        var r2 = await service.AcceptCharacterAsync(Character(ledger, "林青").Id, Hero(Character(ledger, "林青")), r1.Revision);
        var r3 = await service.AcceptPairAsync(pair.Id, new("chị", "em", "chị → em", 0), r2.Revision);
        var sister = r3.Characters.Profiles.Single(p => p.ChineseName == "王秀兰"); var brother = r3.Characters.Profiles.Single(p => p.ChineseName == "林青");
        var rule = Assert.Single(sister.Relationships);
        Assert.Equal(brother.Id, rule.EffectiveListenerCharacterId); Assert.Equal("chị", rule.SelfReference); Assert.Equal("em", rule.AddresseeReference);
        Assert.True(rule.Enabled && rule.Locked);
        // Only after the explicit switch does the existing selector put the rules into the translation prompt.
        var locked = await service.SetConsistencyLockAsync(true, r3.Revision);
        var selection = new CharacterConsistencyRuleSelector().Select(locked.Characters, "王秀兰看着林青说道：「林青，吃饭了。」");
        Assert.Contains("narrative pronoun = nàng (LOCKED)", selection.PromptBlock); Assert.Contains("DIALOGUE Vương Tú Lan -> Lâm Thanh: self = chị; address = em (LOCKED)", selection.PromptBlock);
    }

    [Fact]
    public async Task Locked_rule_alias_owner_and_glossary_conflicts_are_never_overwritten()
    {
        var (session, service, ledger) = await Seed(_root);
        var store = new ProjectResourceStore(session); var resources = await store.LoadAsync();
        resources.Terms.Add(new("小兰", "Tiểu Lan Hoa"));
        resources = await store.SaveAsync(resources);
        var heroine = Character(ledger, "王秀兰");
        var glossary = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptCharacterAsync(heroine.Id, Heroine(heroine), resources.Revision));
        Assert.Contains("Glossary", glossary.Message);
        var withoutXiaoLan = Heroine(heroine) with { NameForms = heroine.NameForms.Where(f => f.Zh != "小兰").ToArray() };
        resources = await service.AcceptCharacterAsync(heroine.Id, withoutXiaoLan, resources.Revision);
        Assert.DoesNotContain("小兰", resources.Characters.Profiles.Single().Aliases);
        // A user-locked rule with different terms stays as it is.
        resources = await service.AcceptCharacterAsync(Character(ledger, "林青").Id, Hero(Character(ledger, "林青")), resources.Revision);
        var sister = resources.Characters.Profiles.Single(p => p.ChineseName == "王秀兰"); var brother = resources.Characters.Profiles.Single(p => p.ChineseName == "林青");
        sister.Relationships.Add(new CharacterRelationship { TargetCharacterId = brother.Id, ListenerCharacterId = brother.Id, SpeakerCharacterId = sister.Id,
            Relationship = "Do người dùng khóa", SelfReference = "ta", AddresseeReference = "ngươi", Enabled = true, Locked = true });
        resources = await store.SaveAsync(resources);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptPairAsync(Pair(ledger, "王秀兰").Id, new("chị", "em", "", 0), resources.Revision));
        Assert.Contains("không ghi đè", conflict.Message);
        Assert.Equal("ta", (await store.LoadAsync()).Characters.Profiles.Single(p => p.ChineseName == "王秀兰").Relationships.Single().SelfReference);
    }

    [Fact]
    public async Task Stale_resources_unverified_proposals_and_edits_outside_the_proposal_are_refused()
    {
        var (session, service, ledger) = await Seed(_root, t => null);
        var heroine = Character(ledger, "王秀兰");
        long current = await Revision(session);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptCharacterAsync(heroine.Id, Heroine(heroine), current + 5));
        var invented = Heroine(heroine) with { NameForms = [.. heroine.NameForms, new AddressingNameForm("兰花", "Lan Hoa")] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptCharacterAsync(heroine.Id, invented, current));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AcceptCharacterAsync(heroine.Id, Heroine(heroine) with { NarrativePronoun = " " }, current));
        Assert.Equal(current, await Revision(session)); // Nothing written by refused attempts.

        var (bad, badService, badLedger) = await Seed(Path.Combine(_root, "bad"), t => t.Kind == AddressingTaskKind.Character && t.Subject == "王秀兰"
            ? AddressingAiSurveyTests.Answer(AddressingAiSurvey.PlanRequests([t], "x").Single())["characters"]![0]!.DeepClone().AsObject().Also(n => n["gender"] = "male") : null);
        var invalid = Character(badLedger, "王秀兰"); Assert.False(invalid.EvidenceValid);
        long badRevision = await Revision(bad);
        await Assert.ThrowsAsync<InvalidOperationException>(() => badService.AcceptCharacterAsync(invalid.Id, Heroine(invalid), badRevision));
        var rejected = await badService.RejectAsync(invalid.Id, "Sai giới tính");
        Assert.Equal(AddressingDecision.Rejected, rejected.LatestReview(invalid.Id)!.Decision);
        var deferred = await badService.DeferAsync(Pair(badLedger, "林青").Id);
        Assert.Equal(AddressingDecision.Deferred, deferred.LatestReview(Pair(badLedger, "林青").Id)!.Decision);
        Assert.Equal(2, deferred.Reviews.Count); // Append-only history.
    }

    [Fact]
    public async Task All_phases_become_chapter_ranged_narrative_pronouns_and_the_reviewed_phase_is_the_default()
    {
        var (session, service, ledger) = await Seed(_root, t => t.Kind != AddressingTaskKind.Character || t.Subject != "林青" ? null : new JsonObject
        {
            ["task_id"] = t.TaskId, ["status"] = "Changes", ["gender"] = "male", ["name_forms"] = new JsonArray(new JsonObject { ["zh"] = "林青", ["vi"] = "Lâm Thanh" }),
            ["phases"] = new JsonArray(
                new JsonObject { ["from_chapter"] = 1, ["to_chapter"] = 1, ["narrative_vi"] = "cậu", ["secondary_vi"] = "", ["sample_ids"] = new JsonArray(t.Samples.First(s => s.ChapterNumber == 1).SampleId) },
                new JsonObject { ["from_chapter"] = 2, ["to_chapter"] = 0, ["narrative_vi"] = "hắn", ["secondary_vi"] = "", ["sample_ids"] = new JsonArray(t.Samples.First(s => s.ChapterNumber == 2).SampleId) }),
            ["reason"] = "Lớn lên đổi cách gọi."
        });
        var hero = Character(ledger, "林青"); Assert.True(hero.EvidenceValid, string.Join(",", hero.Issues)); Assert.Equal(2, hero.Phases.Count);
        var saved = await service.AcceptCharacterAsync(hero.Id, Hero(hero) with { PhaseIndex = 1 }, await Revision(session));
        var profile = saved.Characters.Profiles.Single();
        Assert.Equal("hắn", profile.PrimaryNarrativePronoun);
        Assert.Equal([new NarrativePronounPhase(1, 1, "cậu", ""), new NarrativePronounPhase(2, null, "hắn", "hắn ta")], profile.NarrativePhases);
        Assert.Equal("cậu", profile.NarrativePronounFor(1)); Assert.Equal("hắn", profile.NarrativePronounFor(9)); Assert.Equal("hắn", profile.NarrativePronounFor(null));
    }
}
