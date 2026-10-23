using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Models;
using QuickTranslator.AiEditor.Core.Translation;

namespace QuickTranslator.AiEditor.Tests;

public sealed class DialogueTurnInferenceTests
{
    private const string Anchors = "林哲说道：“第一句。”\n王秀兰说道：“第二句。”\n";

    [Fact]
    public void Two_party_dialogue_alternates_three_bare_quotes_with_listeners()
    {
        var (chapter, segment) = Chapter(Anchors + "“第三句。”\n“第四句。”\n“第五句。”");
        var turns = Infer(chapter);
        Assert.Equal(new[] { "林哲", "王秀兰", "林哲" }, turns.Skip(2).Select(t => t.Speaker?.ChineseName));
        Assert.Equal(new[] { "王秀兰", "林哲", "王秀兰" }, turns.Skip(2).Select(t => t.Listener?.ChineseName));
        Assert.All(turns.Take(2), t => Assert.False(t.IsInferred));
        Assert.All(turns.Skip(2), t => Assert.True(t.IsInferred));
        Assert.All(turns.Skip(2), t => Assert.Equal(SpeakerEvidence.Unattributed, t.Line.SpeakerEvidence));
        string map = DialogueMapBuilder.Build(chapter, segment);
        Assert.Contains("“第三句。”: Lâm Triết → Vương Tú Lan (inferred) (self=con; address=mẹ)", map);
        Assert.Contains("“第四句。”: Vương Tú Lan → Lâm Triết (inferred)", map);
        Assert.Contains("Inferred pairs may be overruled by the text", map);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Segment_opening_with_bare_quote_inherits_earlier_turns(string newline)
    {
        string text = (Anchors + "“第三句。”\n“第四句。”").Replace("\n", newline);
        var (chapter, _) = Chapter(text);
        int boundary = text.IndexOf("“第三句", StringComparison.Ordinal);
        var segment = new TranslationSegment { StartUtf16 = boundary, SourceText = text[boundary..] };
        string map = DialogueMapBuilder.Build(chapter, segment);
        Assert.Contains("“第三句。”: Lâm Triết → Vương Tú Lan (inferred)", map);
        Assert.Contains("“第四句。”: Vương Tú Lan → Lâm Triết (inferred)", map);
        Assert.DoesNotContain("第一句", map);
        Assert.DoesNotContain("第二句", map);
    }

    [Fact]
    public void Third_named_person_in_narration_stops_the_chain()
    {
        var (chapter, _) = Chapter(Anchors + "“第三句。”\n门口的陈安走了。\n“第四句。”\n“第五句。”");
        chapter.CharacterProject.Profiles.Add(new CharacterProfile { ChineseName = "陈安", VietnameseName = "Trần An" });
        var turns = Infer(chapter);
        Assert.True(turns[2].IsInferred);
        Assert.All(turns.Skip(3), t => { Assert.Null(t.Speaker); Assert.False(t.IsInferred); });
    }

    [Theory]
    [InlineData("他问道", "林哲", "王秀兰")]
    [InlineData("她问道", "王秀兰", "林哲")]
    [InlineData("然后她问道", "王秀兰", "林哲")]
    [InlineData("他望着她，又问道", "林哲", "王秀兰")]
    public void Pronoun_subject_uses_unique_profile_gender_even_when_it_breaks_alternation(string lead, string speaker, string listener)
    {
        var (chapter, _) = Chapter(Anchors + lead + "：“第三句。”");
        var turn = Infer(chapter)[2];
        Assert.Equal(SpeakerEvidence.Pronoun, turn.Line.SpeakerEvidence);
        Assert.True(turn.IsInferred);
        Assert.Equal(speaker, turn.Speaker?.ChineseName);
        Assert.Equal(listener, turn.Listener?.ChineseName);
    }

    [Theory]
    [InlineData(CharacterGender.Male, CharacterGender.Male, "他问道")]
    [InlineData(CharacterGender.Female, CharacterGender.Female, "她问道")]
    [InlineData(CharacterGender.Male, CharacterGender.Unknown, "他问道")]
    [InlineData(CharacterGender.Unknown, CharacterGender.Female, "她问道")]
    [InlineData(CharacterGender.Unknown, CharacterGender.Unknown, "她问道")]
    [InlineData(CharacterGender.Male, CharacterGender.Female, "它问道")]
    [InlineData(CharacterGender.Male, CharacterGender.Female, "他们问道")]
    public void Ambiguous_or_unsupported_pronouns_do_not_fall_back_to_alternation(CharacterGender first, CharacterGender second, string lead)
    {
        var (chapter, _) = Chapter(Anchors + lead + "：“第三句。”\n“第四句。”");
        chapter.CharacterProject.Profiles[0].Gender = first;
        chapter.CharacterProject.Profiles[1].Gender = second;
        Assert.All(Infer(chapter).Skip(2), t => { Assert.Null(t.Speaker); Assert.False(t.IsInferred); });
    }

    [Fact]
    public void Explicit_attribution_overrides_alternation_and_is_not_marked_inferred()
    {
        var (chapter, segment) = Chapter(Anchors + "王秀兰说道：“第三句。”\n“第四句。”");
        var turns = Infer(chapter);
        Assert.Equal("王秀兰", turns[2].Speaker?.ChineseName);
        Assert.Equal(SpeakerEvidence.Explicit, turns[2].Line.SpeakerEvidence);
        Assert.False(turns[2].IsInferred);
        Assert.Null(turns[3].Speaker);
        string entry = Assert.Single(DialogueMapBuilder.Build(chapter, segment).Split('\n'), l => l.Contains("第三句"));
        Assert.Contains("Vương Tú Lan →", entry);
        Assert.DoesNotContain("(inferred)", entry);
    }

    [Fact]
    public void Four_inferred_quotes_is_the_limit_and_fresh_anchors_can_restart()
    {
        var (chapter, _) = Chapter(Anchors + string.Concat(Enumerable.Repeat("“继续谈。”\n", 6)) + Anchors + "“重新谈。”");
        var turns = Infer(chapter);
        Assert.All(turns.Skip(2).Take(4), t => Assert.True(t.IsInferred));
        Assert.All(turns.Skip(6).Take(2), t => Assert.Null(t.Speaker));
        Assert.True(turns[^1].IsInferred);
        Assert.Equal("林哲", turns[^1].Speaker?.ChineseName);
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(61, false)]
    public void Narration_length_bounds_inference(int length, bool expected)
    {
        var (chapter, _) = Chapter(Anchors + new string('静', length) + "\n“第三句。”");
        Assert.Equal(expected, Infer(chapter)[2].IsInferred);
    }

    [Fact]
    public void Two_narration_paragraphs_stop_inference()
    {
        var (chapter, _) = Chapter(Anchors + "夜色渐深。\n风吹过树梢。\n“第三句。”");
        Assert.Null(Infer(chapter)[2].Speaker);
    }

    [Fact]
    public void Aliases_share_identity_and_do_not_look_like_a_third_participant()
    {
        var (chapter, _) = Chapter("小哲说道：“第一句。”\n王秀兰说道：“第二句。”\n林哲点了点头。“第三句。”");
        chapter.CharacterProject.Profiles[0].Aliases.Add("小哲");
        var turn = Infer(chapter)[2];
        Assert.True(turn.IsInferred);
        Assert.Same(chapter.CharacterProject.Profiles[0], turn.Speaker);
    }

    [Fact]
    public void One_person_under_two_aliases_cannot_seed_alternation()
    {
        var (chapter, _) = Chapter("林哲说道：“第一句。”\n小哲说道：“第二句。”\n“第三句。”");
        chapter.CharacterProject.Profiles[0].Aliases.Add("小哲");
        Assert.Null(Infer(chapter)[2].Speaker);
    }

    [Theory]
    [InlineData("男人")]
    [InlineData("女孩")]
    [InlineData("角色1")]
    public void Generic_or_numbered_names_cannot_seed_inference(string generic)
    {
        var (chapter, segment) = Chapter($"林哲说道：“第一句。”\n{generic}说道：“第二句。”\n“第三句。”");
        chapter.CharacterProject.Profiles[1].ChineseName = generic;
        string map = DialogueMapBuilder.Build(chapter, segment);
        Assert.DoesNotContain("第二句", map);
        Assert.DoesNotContain("第三句", map);
        Assert.DoesNotContain("(inferred)", map);
    }

    [Theory]
    [InlineData("“砰！”")]
    [InlineData("“……”")]
    [InlineData("“林哲，你听着。”")]
    public void Sounds_and_contradictory_vocatives_are_not_inferred(string quote)
    {
        var (chapter, _) = Chapter(Anchors + quote + "\n“第四句。”");
        Assert.All(Infer(chapter).Skip(2), t => Assert.Null(t.Speaker));
    }

    [Fact]
    public void Survey_task_size_limits_do_not_break_chapter_inference()
    {
        var (chapter, segment) = Chapter(string.Concat(Enumerable.Repeat(Anchors, 6)) + "“第三句。”");
        Assert.Contains("“第三句。”: Lâm Triết → Vương Tú Lan (inferred)", DialogueMapBuilder.Build(chapter, segment));
    }

    [Fact]
    public void Shared_alias_does_not_attribute_a_speaker_in_the_map()
    {
        var (chapter, segment) = Chapter("小林说道：“第一句。”\n王秀兰说道：“第二句。”\n“第三句。”");
        foreach (var profile in chapter.CharacterProject.Profiles) profile.Aliases.Add("小林");
        string map = DialogueMapBuilder.Build(chapter, segment);
        Assert.DoesNotContain("第一句", map);
        Assert.DoesNotContain("第三句", map);
    }

    [Fact]
    public void Scene_parties_remain_available_for_gender_after_repeated_speaker()
    {
        var (chapter, _) = Chapter(Anchors + "她问道：“第三句。”\n他答道：“第四句。”\n“第五句。”");
        var turns = Infer(chapter);
        Assert.Equal(new[] { "王秀兰", "林哲", "王秀兰" }, turns.Skip(2).Select(t => t.Speaker?.ChineseName));
        Assert.All(turns.Skip(2), t => Assert.True(t.IsInferred));
    }

    [Fact]
    public void Third_speaker_attributed_after_a_quote_stops_old_pair_inference()
    {
        var (chapter, _) = Chapter(Anchors + "“第三句。”陈安说道。\n“第四句。”");
        chapter.CharacterProject.Profiles.Add(new CharacterProfile { ChineseName = "陈安" });
        var turns = Infer(chapter);
        Assert.Equal("陈安", turns[2].Speaker?.ChineseName);
        Assert.False(turns[2].IsInferred);
        Assert.Null(turns[3].Speaker);
    }

    [Fact]
    public void Previous_narration_can_seed_a_pair_without_changing_its_evidence()
    {
        var (chapter, _) = Chapter("林哲抬起头。\n“第一句。”\n王秀兰说道：“第二句。”\n“第三句。”");
        var turns = Infer(chapter);
        Assert.Equal(SpeakerEvidence.PreviousNarration, turns[0].Line.SpeakerEvidence);
        Assert.False(turns[0].IsInferred);
        Assert.Equal("林哲", turns[2].Speaker?.ChineseName);
        Assert.True(turns[2].IsInferred);
    }

    private static IReadOnlyList<DialogueTurn> Infer(ChapterDocument chapter)
    {
        var owners = chapter.CharacterProject.Profiles
            .SelectMany(p => new[] { p.ChineseName }.Concat(p.Aliases).Select(n => (Name: n, Profile: p)))
            .ToDictionary(x => x.Name, x => x.Profile, StringComparer.Ordinal);
        var scenes = AddressingDialogueSurvey.Scenes([new CensusSource(chapter.Id, 1, chapter.OriginalText)],
            owners.Keys.ToArray(), maximumQuotes: int.MaxValue, maximumCharacters: int.MaxValue);
        return DialogueTurnInference.Infer(scenes, owners);
    }

    private static (ChapterDocument Chapter, TranslationSegment Segment) Chapter(string text)
    {
        var first = new CharacterProfile { ChineseName = "林哲", VietnameseName = "Lâm Triết", Gender = CharacterGender.Male };
        var second = new CharacterProfile { ChineseName = "王秀兰", VietnameseName = "Vương Tú Lan", Gender = CharacterGender.Female };
        first.Relationships.Add(new CharacterRelationship { TargetCharacterId = second.Id, ListenerCharacterId = second.Id,
            SelfReference = "con", AddresseeReference = "mẹ", Enabled = true });
        var chapter = new ChapterDocument { Id = Guid.NewGuid(), ChapterNumber = 1, OriginalText = text,
            CharacterProject = new CharacterProject { Profiles = [first, second] } };
        return (chapter, new TranslationSegment { StartUtf16 = 0, SourceText = text });
    }
}
