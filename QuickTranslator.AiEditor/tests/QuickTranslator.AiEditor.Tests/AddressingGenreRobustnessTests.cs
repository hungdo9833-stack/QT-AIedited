using QuickTranslator.AiEditor.Core.Addressing;
using QuickTranslator.AiEditor.Core.BookIntelligence;
using QuickTranslator.AiEditor.Core.Projects;

namespace QuickTranslator.AiEditor.Tests;

/// <summary>
/// CPU heuristics must hold across genres and writing styles, not one test book: urban/romance,
/// xianxia/wuxia and historical text, hard-wrapped or not, "X说道：" or narration-then-quote.
/// </summary>
public sealed class AddressingGenreRobustnessTests
{
    private static IReadOnlyDictionary<string, MentionCandidateCategory> Census(string text)
    {
        var id = Guid.NewGuid();
        var resources = new ProjectResources { ProjectId = id, Characters = new() { Id = id } };
        return CharacterMentionCensus.Scan(id, 0, [new CensusSource(Guid.NewGuid(), 1, text)], resources, new BookIntelligenceState { ProjectId = id })
            .Candidates.ToDictionary(c => c.ChineseName, c => c.Category);
    }
    private static string Times(string sentence, int n) => string.Concat(Enumerable.Repeat(sentence, n));

    [Theory]
    [InlineData("宁夕")]      // modern romance heroine; 宁 was missing from the old lexicon
    [InlineData("肖然")]
    [InlineData("欧阳锋")]    // wuxia compound surname
    [InlineData("上官婉儿")]  // historical compound surname
    [InlineData("司徒南")]
    public void Common_and_compound_surnames_are_discovered(string name)
    {
        var census = Census(Times(name + "看着窗外。" + name + "走了过去。", 3));
        Assert.Equal(MentionCandidateCategory.PersonCandidate, census[name]);
    }

    [Theory]
    [InlineData("连忙站了起来。")]   // 连 surname, 连忙 "hurriedly"
    [InlineData("简单说了几句。")]   // 简 / 简单
    [InlineData("修长的手指。")]     // 修 / 修长
    [InlineData("车子停在门口。")]   // 车 / 车子
    [InlineData("哈哈大笑起来。")]   // laughter
    [InlineData("沙发上的人走了。")] // locative phrase
    [InlineData("明白了他的意思。")] // common word with a common-tier surname
    public void Ordinary_words_starting_with_a_surname_are_not_people(string sentence)
    {
        var census = Census(Times("    " + sentence + "\n", 8));
        Assert.DoesNotContain(census, c => c.Value is MentionCandidateCategory.PersonCandidate && sentence.StartsWith(c.Key, StringComparison.Ordinal));
    }

    [Fact]
    public void Extended_tier_surname_is_a_person_with_strong_evidence()
    {
        // 简 starts ordinary words, but an introduction is evidence only people get.
        var census = Census(Times("她笑道：“我叫简宁。”简宁看着他。", 3));
        Assert.Equal(MentionCandidateCategory.PersonCandidate, census["简宁"]);
    }

    [Theory]
    [InlineData("陆氏集团", true)]
    [InlineData("天剑宗", true)]
    [InlineData("青云门派", true)]
    [InlineData("盘龙大厦", true)]
    [InlineData("张建国", false)]  // 国 ends many real names
    [InlineData("花满楼", false)]  // 楼 ends a famous character's name
    [InlineData("王氏", false)]    // historical: Mrs Wang is a person
    [InlineData("李长城", false)]
    public void Organization_and_place_filter_does_not_drop_person_names(string name, bool organization) =>
        Assert.Equal(organization, AddressingAiSurvey.IsOrganizationOrPlace(name));

    private static IReadOnlyList<DialogueLine> Lines(string text, params string[] names) =>
        AddressingDialogueSurvey.Extract(new CensusSource(Guid.NewGuid(), 1, text), names);

    [Fact]
    public void Romance_style_quote_after_a_narration_paragraph_gets_a_weaker_narration_speaker()
    {
        const string text = "    宁夕唇角微勾，看着陆霆骁。\n    “你发烧了？”\n    她笑了。\n    “好。”\n    陆霆骁说道：“走吧。”\n    “嗯。”\n";
        var lines = Lines(text, "宁夕", "陆霆骁");
        Assert.Equal("宁夕", lines[0].Speaker); Assert.Equal(SpeakerEvidence.PreviousNarration, lines[0].SpeakerEvidence);
        Assert.Equal("陆霆骁", lines[0].Listener); Assert.Equal("宁夕唇角微勾，看着陆霆骁", lines[0].LeadClause); // Shown to AI/reviewer.
        Assert.Null(lines[1].Speaker); Assert.Equal(SpeakerEvidence.Pronoun, lines[1].SpeakerEvidence);
        Assert.Equal(SpeakerEvidence.Explicit, lines[2].SpeakerEvidence); // An explicit verb still wins.
        Assert.Equal(SpeakerEvidence.Unattributed, lines[3].SpeakerEvidence); // Previous paragraph is dialogue, not narration.
    }

    [Fact]
    public void Narration_guess_is_dropped_when_the_quote_names_the_guessed_speaker_or_is_a_sound()
    {
        // Real-corpus failure: narration about 小宝, then someone else asks "小宝这是怎么了？".
        const string text = "    小宝用力拍打着仓库的门。\n    “小宝这是怎么了？”\n    宁夕愣住了。\n    “砰！”\n    宁夕说道：“OK。”\n";
        var lines = Lines(text, "小宝", "宁夕");
        Assert.Null(lines[0].Speaker); Assert.Equal(SpeakerEvidence.Unattributed, lines[0].SpeakerEvidence);
        Assert.Null(lines[1].Speaker); // A sound effect is nobody's speech.
        Assert.Equal("宁夕", lines[2].Speaker); // Non-Han speech ("OK") is still speech.
        Assert.True(AddressingDialogueSurvey.IsSoundOnly("轰隆隆……")); Assert.False(AddressingDialogueSurvey.IsSoundOnly("好。"));
    }

    [Fact]
    public void Narration_guesses_enter_pair_tasks_only_with_a_vocative_listener()
    {
        string text = Times("    宁夕看着陆霆骁，眼神温柔。\n    “你回来了。”\n", 4);
        var sources = new[] { new CensusSource(Guid.NewGuid(), 1, text) };
        Assert.DoesNotContain(AddressingAiSurvey.PlanTasks(sources, ["宁夕", "陆霆骁"]), t => t.Kind == AddressingTaskKind.Pair);
        string vocative = Times("    宁夕看着他，眼神温柔。\n    “陆霆骁，你回来了。”\n", 4);
        Assert.Contains(AddressingAiSurvey.PlanTasks([new(Guid.NewGuid(), 1, vocative)], ["宁夕", "陆霆骁"]), t => t.Kind == AddressingTaskKind.Pair && t.Subject == "宁夕");
    }

    [Fact]
    public void Xianxia_and_wuxia_lead_clauses_are_explicit()
    {
        const string text = "    萧炎目光一凝，沉声道：“你找死！”\n    林动冷笑道：“就凭你？”\n    欧阳锋看着林动说道：“小子，好胆。”\n";
        var lines = Lines(text, "萧炎", "林动", "欧阳锋");
        Assert.Equal(["萧炎", "林动", "欧阳锋"], lines.Select(l => l.Speaker));
        Assert.All(lines, l => Assert.Equal(SpeakerEvidence.Explicit, l.SpeakerEvidence));
        Assert.Equal("林动", lines[2].Listener);
    }

    [Fact]
    public void Uncorroborated_narration_lines_never_dilute_explicit_pair_samples()
    {
        string text = Times("    宁夕看着陆霆骁，眼神温柔。\n    “你回来了。”\n", 3) + Times("    宁夕看着陆霆骁说道：“吃饭吧。”\n", 3);
        var sources = new[] { new CensusSource(Guid.NewGuid(), 1, text) };
        var task = AddressingAiSurvey.PlanTasks(sources, ["宁夕", "陆霆骁"], new(SamplesPerTask: 3))
            .Single(t => t.Kind == AddressingTaskKind.Pair && t.Subject == "宁夕");
        Assert.All(task.Samples, s => Assert.Contains("说道", s.Lead)); // Explicit lines fill the quota first.
        var all = AddressingAiSurvey.PlanTasks(sources, ["宁夕", "陆霆骁"], new(SamplesPerTask: 8))
            .Single(t => t.Kind == AddressingTaskKind.Pair && t.Subject == "宁夕");
        Assert.Equal(3, all.Samples.Count); // The narration lines have no vocative listener: report only, no AI task.
    }
}
