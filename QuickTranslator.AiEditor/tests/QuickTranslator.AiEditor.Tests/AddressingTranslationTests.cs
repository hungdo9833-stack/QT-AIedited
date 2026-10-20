using System.Text.Json.Nodes;
using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Projects;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>Addressing Ledger step 4: chapter-ranged and scene-aware rules reach the translation prompt.</summary>
public sealed class AddressingTranslationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "qtaie-addressing-translate-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private static (CharacterProject Project, CharacterProfile Sister, CharacterProfile Brother) Book(params CharacterRelationship[] sisterRules)
    {
        var sister = new CharacterProfile { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan", Gender = CharacterGender.Female, PrimaryNarrativePronoun = "nàng", NarrativePronounLocked = true, NormalizationEnabled = false };
        var brother = new CharacterProfile { ChineseName = "林青", VietnameseName = "Lâm Thanh", Gender = CharacterGender.Male, PrimaryNarrativePronoun = "hắn", NarrativePronounLocked = true, NormalizationEnabled = false };
        sister.Relationships.AddRange(sisterRules.Select(r => r with { TargetCharacterId = brother.Id, ListenerCharacterId = brother.Id, SpeakerCharacterId = sister.Id }));
        var project = new CharacterProject { ConsistencyLockEnabled = true, Profiles = [sister, brother] };
        project.Validate();
        return (project, sister, brother);
    }
    private static CharacterRelationship Rule(string self, string address, int? from = null, int? to = null) => new()
        { Relationship = "chị em", SelfReference = self, AddresseeReference = address, Enabled = true, Locked = true, EffectiveFromChapter = from, EffectiveToChapter = to };
    private const string Both = "王秀兰看着林青说道：「吃饭了。」";

    [Fact]
    public void Ranged_dialogue_rules_apply_only_inside_their_chapters_and_never_to_an_unknown_chapter()
    {
        var (project, _, _) = Book(Rule("chị", "em", 1, 5), Rule("ta", "chàng", 6));
        var selector = new CharacterConsistencyRuleSelector();
        Assert.Contains("self = chị; address = em", selector.Select(project, Both, 3).PromptBlock);
        Assert.Contains("self = ta; address = chàng", selector.Select(project, Both, 9).PromptBlock);
        Assert.DoesNotContain("DIALOGUE", selector.Select(project, Both, null).PromptBlock); // Unknown chapter: no ranged rule.
    }

    [Fact]
    public void Ranged_rule_wins_over_a_whole_book_rule_and_unranged_rules_keep_old_behaviour()
    {
        var (project, _, _) = Book(Rule("tôi", "cậu"), Rule("chị", "em", 2, 4));
        var selector = new CharacterConsistencyRuleSelector();
        Assert.Contains("self = chị; address = em", selector.Select(project, Both, 3).PromptBlock);
        Assert.DoesNotContain("self = tôi", selector.Select(project, Both, 3).PromptBlock);
        Assert.Contains("self = tôi; address = cậu", selector.Select(project, Both, 7).PromptBlock);
        Assert.Contains("self = tôi; address = cậu", selector.Select(project, Both).PromptBlock); // Old call shape.
    }

    [Fact]
    public void Narrative_phase_for_the_chapter_is_used_and_default_otherwise()
    {
        var (project, _, brother) = Book();
        brother.NarrativePhases = [new(1, 3, "cậu", ""), new(4, null, "hắn", "hắn ta")];
        project.Validate();
        var selector = new CharacterConsistencyRuleSelector();
        Assert.Contains("Lâm Thanh; narrative pronoun = cậu (LOCKED)", selector.Select(project, "林青走了。", 2).PromptBlock);
        Assert.Contains("Lâm Thanh; narrative pronoun = hắn (LOCKED)", selector.Select(project, "林青走了。", 8).PromptBlock);
    }

    [Fact]
    public void Dialogue_rule_applies_when_speaker_and_listener_are_in_the_scene_not_only_in_the_segment()
    {
        var (project, _, _) = Book(Rule("chị", "em"));
        var selector = new CharacterConsistencyRuleSelector();
        const string unnamedLine = "「快吃吧，别饿着。」";
        Assert.Equal("", selector.Select(project, unnamedLine, 1).PromptBlock); // Before: names had to be in the segment.
        var scene = selector.Select(project, unnamedLine, 1, "王秀兰端着饭走到林青面前。");
        Assert.Contains("DIALOGUE Vương Tú Lan -> Lâm Thanh: self = chị; address = em", scene.PromptBlock);
        Assert.Contains("(in scene, not named in this passage)", scene.PromptBlock);
        // Narration without dialogue gets no dialogue rule from the scene.
        Assert.DoesNotContain("DIALOGUE", selector.Select(project, "她笑了。", 1, "王秀兰端着饭走到林青面前。").PromptBlock);
    }

    [Fact]
    public void Invalid_or_overlapping_ranges_are_rejected_by_validation()
    {
        var (project, sister, brother) = Book();
        brother.NarrativePhases = [new(1, 5, "cậu", ""), new(4, null, "hắn", "")];
        Assert.Throws<ArgumentException>(project.Validate);
        brother.NarrativePhases = [new(3, 2, "cậu", "")];
        Assert.Throws<ArgumentException>(project.Validate);
        brother.NarrativePhases = [];
        sister.Relationships.Add(Rule("chị", "em", 5, 2) with { TargetCharacterId = brother.Id, ListenerCharacterId = brother.Id, SpeakerCharacterId = sister.Id });
        Assert.Throws<ArgumentException>(project.Validate);
    }

    [Fact]
    public void Scene_text_is_the_two_previous_segments_of_the_same_chapter()
    {
        string Paragraph(string head) => head + new string('云', 420) + "。\n\n";
        var chapter = TranslationService.CreateChapter("Synthetic", Paragraph("王秀兰来了") + Paragraph("林青看着她") + Paragraph("他笑了") + "「吃饭了。」");
        var segments = chapter.Segments.OrderBy(s => s.Order).ToArray();
        Assert.True(segments.Length >= 3, "segments=" + segments.Length);
        string scene = TranslationService.SceneText(chapter, segments[^1]);
        Assert.Contains(segments[^2].SourceText, scene); Assert.Contains(segments[^3].SourceText, scene);
        Assert.Equal("", TranslationService.SceneText(chapter, segments[0]));
    }

    [Fact]
    public async Task Reviewed_multi_phase_pair_becomes_ranged_rules_that_the_translation_prompt_uses_per_chapter()
    {
        var (session, service, ledger) = await AddressingReviewTests.Seed(_root, t => t.Kind != AddressingTaskKind.Pair || t.Subject != "王秀兰" ? null : new JsonObject
        {
            ["task_id"] = t.TaskId, ["status"] = "Changes", ["relationship"] = "chị → em",
            ["phases"] = new JsonArray(
                new JsonObject { ["from_chapter"] = 1, ["to_chapter"] = 1, ["self_vi"] = "chị", ["address_vi"] = "em", ["self_zh"] = new JsonArray(), ["address_zh"] = new JsonArray(),
                    ["sample_ids"] = new JsonArray(t.Samples.First(s => s.ChapterNumber == 1).SampleId) },
                new JsonObject { ["from_chapter"] = 2, ["to_chapter"] = 0, ["self_vi"] = "ta", ["address_vi"] = "chàng", ["self_zh"] = new JsonArray(), ["address_zh"] = new JsonArray(),
                    ["sample_ids"] = new JsonArray(t.Samples.First(s => s.ChapterNumber == 2).SampleId) }),
            ["reason"] = "Đổi cách xưng hô."
        });
        var pair = ledger.Proposals.Single(p => p.Kind == AddressingTaskKind.Pair && p.Subject == "王秀兰");
        Assert.True(pair.EvidenceValid, string.Join(",", pair.Issues));
        long revision = (await new ProjectResourceStore(session).LoadAsync()).Revision;
        foreach (var name in new[] { "王秀兰", "林青" })
        {
            var c = ledger.Proposals.Single(p => p.Kind == AddressingTaskKind.Character && p.Subject == name);
            revision = (await service.AcceptCharacterAsync(c.Id, new(name == "林青" ? CharacterGender.Male : CharacterGender.Female,
                name == "林青" ? "Lâm Thanh" : "Vương Tú Lan", name == "林青" ? "hắn" : "nàng", "", c.NameForms, 0), revision)).Revision;
        }
        var saved = await service.AcceptPairAsync(pair.Id, new("chị", "em", "chị → em", 0), revision);
        var rules = saved.Characters.Profiles.Single(p => p.ChineseName == "王秀兰").Relationships;
        Assert.Equal(2, rules.Count);
        Assert.Contains(rules, r => r.EffectiveFromChapter == 1 && r.EffectiveToChapter == 1 && r.SelfReference == "chị");
        Assert.Contains(rules, r => r.EffectiveFromChapter == 2 && r.EffectiveToChapter is null && r.SelfReference == "ta");
        var locked = await service.SetConsistencyLockAsync(true, saved.Revision);
        var selector = new CharacterConsistencyRuleSelector();
        Assert.Contains("self = chị; address = em", selector.Select(locked.Characters, Both, 1).PromptBlock);
        Assert.Contains("self = ta; address = chàng", selector.Select(locked.Characters, Both, 2).PromptBlock);
    }
}
